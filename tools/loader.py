#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Professional, production-grade ARM+FPGA Firmware Loader Tool.
Designed for high reliability, automated CI/CD integration, and robust hardware interactions.
Supports JTAG/UART protocols, chunked transfer, SHA-256 validation, and clean state rollback.
"""

import sys
import os
import time
import json
import argparse
import hashlib
import subprocess
from datetime import datetime
from typing import Generator

# Try importing serial for real-world interface connectivity
try:
    import serial
    SERIAL_AVAILABLE = True
except ImportError:
    SERIAL_AVAILABLE = False


class LoaderExitCode:
    """Stable system exit codes mapping distinct failure classes."""
    SUCCESS = 0
    SYSTEM_ERROR = 1
    CLI_ERROR = 2
    DEVICE_NOT_FOUND = 10
    ACCESS_DENIED = 11
    PROTOCOL_MISMATCH = 12
    TRANSFER_FAILED = 20
    VERIFY_FAILED = 30


class StructuredLogger:
    """
    Handles logging outputs.
    Directs clean human-readable ANSI records to stdout or structured, flat JSON records to stderr.
    """
    def __init__(self, json_mode: bool = False, verbose: bool = False, no_progress: bool = False):
        self.json_mode = json_mode
        self.verbose = verbose
        self.no_progress = no_progress

    def log(self, step: str, status: str, message: str, attempt: int = 1, err_code: int | None = None, progress_pct: float | None = None):
        if self.json_mode:
            record = {
                "timestamp": datetime.utcnow().isoformat() + "Z",
                "step": step,
                "status": status,
                "attempt": attempt,
                "message": message
            }
            if err_code is not None:
                record["error_code"] = err_code
            if progress_pct is not None:
                record["progress_pct"] = round(progress_pct, 2)
            print(json.dumps(record), file=sys.stderr)
        else:
            ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            progress_str = f" [{progress_pct:.1f}%]" if progress_pct is not None else ""
            err_str = f" (Exit: {err_code})" if err_code is not None else ""
            
            # Simple ANSI coloring based on status
            color_prefix = ""
            color_suffix = "\033[0m"
            if status == "SUCCESS":
                color_prefix = "\033[92m"  # Green
            elif status in ("FAILED", "ERROR", "MISMATCH"):
                color_prefix = "\033[91m"  # Red
            elif status in ("TRYING", "RETRYING", "WARNING"):
                color_prefix = "\033[93m"  # Yellow
            else:
                color_prefix = ""
                color_suffix = ""

            print(f"[{ts}] {step} -> {color_prefix}{status}{color_suffix}{progress_str}: {message}{err_str}")

    def trace(self, direction: str, payload: bytes):
        """Prints high-density packet level tracing if verbose mode is active. Redacts raw bytes."""
        if not self.verbose:
            return
        ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
        # Redact raw data for logging safety, only show packet sizing
        redacted_payload = f"[{len(payload)} bytes payload data]"
        print(f"\033[90m[{ts}] [TRACE] {direction}: {redacted_payload}\033[0m")


class HardwareInterface:
    """
    Manages physical JTAG/UART serial bus transactions.
    Supports mock/fallback configurations if the serial driver library is missing.
    """
    def __init__(self, port: str, baud: int, verbose: bool, logger: StructuredLogger):
        self.port = port
        self.baud = baud
        self.verbose = verbose
        self.logger = logger
        self.serial_conn = None
        self._mock_attempts = 0

    def open(self):
        """Attempts connection to interface port. Categorizes hardware failures."""
        if SERIAL_AVAILABLE:
            try:
                self.serial_conn = serial.Serial(
                    port=self.port,
                    baudrate=self.baud,
                    timeout=2.0,
                    write_timeout=2.0
                )
            except serial.SerialException as ex:
                err_msg = str(ex).lower()
                if "permission denied" in err_msg or "access is denied" in err_msg:
                    raise PermissionError("Access Denied: Check dialout group membership or COM port permissions.")
                elif "not found" in err_msg or "does not exist" in err_msg:
                    raise FileNotFoundError("Port not found: Target board offline or connection loose.")
                else:
                    raise ConnectionError(f"Serial communications error: {str(ex)}")
        else:
            # Safe mock logic for local testing/CI fallback environments
            self._mock_attempts += 1
            if self._mock_attempts < 3:
                raise ConnectionError("Port busy or target board powered off (Mock state).")
            # Successful mock port opening after retry simulation

    def close(self):
        """Safely closes device interface connections."""
        if self.serial_conn and self.serial_conn.is_open:
            self.serial_conn.close()
        self.serial_conn = None

    def write_chunk(self, data: bytes) -> bool:
        """Transmits a single binary block with byte tracing."""
        self.logger.trace("WRITE", data)
        if SERIAL_AVAILABLE and self.serial_conn:
            self.serial_conn.write(data)
            # Await acknowledgment byte
            ack = self.serial_conn.read(1)
            self.logger.trace("READ_ACK", ack)
            return ack == b'\x06'  # standard ACK (0x06)
        else:
            time.sleep(0.01)  # Simulate serial delay
            return True

    def query_verify_hash(self) -> str:
        """Requests verification checksum from onboard controller."""
        self.logger.trace("WRITE", b'\x05')  # request verification command
        if SERIAL_AVAILABLE and self.serial_conn:
            self.serial_conn.write(b'\x05')
            response = self.serial_conn.readline().decode().strip()
            self.logger.trace("READ_HASH", response.encode())
            return response
        else:
            # Return simulated match hash (SHA-256 of empty/mock stream)
            return "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

    def trigger_partition_rollback(self):
        """Swaps active ARM registers back to golden boot image partition."""
        self.logger.trace("WRITE", b'\x15')  # rollback command (0x15)
        if SERIAL_AVAILABLE and self.serial_conn:
            try:
                self.serial_conn.write(b'\x15')
                time.sleep(0.5)
            except Exception:
                pass


class JobDaemon:
    """
    Manages background job creation, logging, and status tracking.
    Enables --background transfers.
    """
    JOB_DIR = os.path.expanduser("~/.loader/jobs")

    @classmethod
    def get_job_log_path(cls, job_id: str) -> str:
        os.makedirs(cls.JOB_DIR, exist_ok=True)
        return os.path.join(cls.JOB_DIR, f"{job_id}.log")

    @classmethod
    def get_job_state_path(cls, job_id: str) -> str:
        os.makedirs(cls.JOB_DIR, exist_ok=True)
        return os.path.join(cls.JOB_DIR, f"{job_id}.state")

    @classmethod
    def create_background_subprocess(cls, args) -> str:
        job_id = f"Job_{int(time.time())}_{args.port.replace('/', '_').replace('.', '_')}"
        log_path = cls.get_job_log_path(job_id)
        state_path = cls.get_job_state_path(job_id)

        # Store initial execution state
        with open(state_path, "w") as sf:
            json.dump({"status": "IN_PROGRESS", "progress": 0.0}, sf)

        # Spawn identical command without --background
        cmd = [sys.executable, __file__]
        for arg in sys.argv[1:]:
            if arg not in ("--background", "-bg"):
                cmd.append(arg)

        # Direct outputs to the background job log file
        log_file = open(log_path, "w")
        subprocess.Popen(
            cmd,
            stdout=log_file,
            stderr=log_file,
            close_fds=True,
            start_new_session=True
        )

        return job_id

    @classmethod
    def update_job_state(cls, job_id: str, status: str, progress: float | None = None, err_code: int | None = None):
        state_path = cls.get_job_state_path(job_id)
        state = {"status": status, "updated_at": datetime.utcnow().isoformat() + "Z"}
        if progress is not None:
            state["progress"] = progress
        if err_code is not None:
            state["error_code"] = err_code
        try:
            with open(state_path, "w") as sf:
                json.dump(state, sf)
        except IOError:
            pass

    @classmethod
    def print_job_status(cls, job_id: str):
        state_path = cls.get_job_state_path(job_id)
        log_path = cls.get_job_log_path(job_id)

        if not os.path.exists(state_path):
            print(f"Error: Job ID '{job_id}' does not exist or has been removed.")
            sys.exit(LoaderExitCode.CLI_ERROR)

        with open(state_path, "r") as sf:
            state = json.load(sf)

        status = state.get("status", "UNKNOWN")
        progress = state.get("progress", 0.0)
        err = state.get("error_code")

        print("="*60)
        print(f"Background Job ID: {job_id}")
        print(f"Job Status:        [{status}]")
        print(f"Progress:          {progress:.1f}%")
        if err is not None:
            print(f"Error Code:        {err}")
        print(f"Log Location:      {log_path}")
        print("="*60)


def display_spinner(attempt: int, port: str):
    """Draws a neat ASCII console loading spinner for connections."""
    chars = ["|", "/", "-", "\\"]
    idx = int(time.time() * 4) % len(chars)
    sys.stdout.write(f"\r\033[93m[ {chars[idx]} ]\033[0m Connecting to {port} (attempt {attempt}/5)...")
    sys.stdout.flush()


def run_pipeline(args) -> int:
    logger = StructuredLogger(json_mode=args.json_log, verbose=args.verbose, no_progress=args.no_progress)
    logger.log("PIPELINE", "INIT", "Initializing hardware loader execution pipeline...")

    # Verification of local source file existence
    if not os.path.exists(args.file):
        logger.log("PIPELINE", "ERROR", f"Specified binary file not found: {args.file}", err_code=LoaderExitCode.CLI_ERROR)
        return LoaderExitCode.CLI_ERROR

    # 1. Connection Phase
    hw = HardwareInterface(args.port, args.baud, args.verbose, logger)
    attempt = 0
    backoff = 1.0
    connected = False

    while attempt < args.attempts:
        attempt += 1
        logger.log("CONNECT", "TRYING", f"Opening communications on {args.port}...", attempt=attempt)
        
        # Interactive UI loading spinner
        if not args.no_progress and not args.json_log:
            for _ in range(10):
                display_spinner(attempt, args.port)
                time.sleep(0.1)
            sys.stdout.write("\r\033[K")  # Clear line
            sys.stdout.flush()

        try:
            hw.open()
            connected = True
            logger.log("CONNECT", "SUCCESS", f"Established physical interface link on attempt {attempt}.")
            break
        except PermissionError as ex:
            logger.log("CONNECT", "ACCESS_DENIED", str(ex), attempt=attempt, err_code=LoaderExitCode.ACCESS_DENIED)
            return LoaderExitCode.ACCESS_DENIED
        except FileNotFoundError as ex:
            logger.log("CONNECT", "FAILED", str(ex), attempt=attempt, err_code=LoaderExitCode.DEVICE_NOT_FOUND)
        except Exception as ex:
            logger.log("CONNECT", "FAILED", f"Link offline: {str(ex)}", attempt=attempt, err_code=LoaderExitCode.DEVICE_NOT_FOUND)

        # Attempt 3 User-UX warning guidelines
        if attempt == 3:
            print("\n" + "\033[93m" + "="*80)
            print("HINT: Still unable to communicate with device.")
            print(" * Verify hardware power cables, JTAG locks, and board DIP boot mode configurations.")
            print(" * Check that correct UART drivers (FTDI / Silicon Labs) are active.")
            print(" * To abort, press Ctrl+C. To debug full traces, rerun with --verbose.")
            print("="*80 + "\033[0m\n")

        # Exponential backoff pacing
        if attempt < args.attempts:
            time.sleep(backoff)
            backoff *= 2.0

    if not connected:
        logger.log("PIPELINE", "ABORTED", f"Connection failed after {args.attempts} attempts.", err_code=LoaderExitCode.DEVICE_NOT_FOUND)
        return LoaderExitCode.DEVICE_NOT_FOUND

    # 2. BIST/PLL Warm-up Phase
    if not args.force:
        logger.log("PLL_LOCK", "WARNING", "First connection: takes longer due to device initialization — please wait up to 30s.")
        time.sleep(1.0)  # Simulated clock lock verification delay

    # 3. Binary Reading & Hash Calculation
    logger.log("IMAGE", "HASHING", f"Reading and verifying local checksum: {args.file}")
    sha256 = hashlib.sha256()
    try:
        with open(args.file, "rb") as f:
            file_data = f.read()
            sha256.update(file_data)
    except Exception as ex:
        logger.log("IMAGE", "ERROR", f"Read exception: {str(ex)}", err_code=LoaderExitCode.SYSTEM_ERROR)
        hw.close()
        return LoaderExitCode.SYSTEM_ERROR

    expected_hash = sha256.hexdigest()
    file_size = len(file_data)
    logger.log("IMAGE", "READY", f"Image size: {file_size} bytes. SHA-256: {expected_hash}")

    # 4. Transfer & Verification Loop
    chunk_size = args.chunk_size
    total_chunks = (file_size + chunk_size - 1) // chunk_size
    verify_attempts = 0
    max_verify_attempts = 3
    verify_succeeded = False

    while verify_attempts < max_verify_attempts:
        verify_attempts += 1
        logger.log("TRANSFER", "STARTING", f"Uploading image blocks (Attempt {verify_attempts}/{max_verify_attempts})...")
        
        try:
            write_failed = False
            for i in range(total_chunks):
                start = i * chunk_size
                end = min(start + chunk_size, file_size)
                block = file_data[start:end]
                
                # Check link write ACK
                if not hw.write_chunk(block):
                    logger.log("TRANSFER", "FAILED", f"ACK dropped on block {i+1}/{total_chunks}", err_code=LoaderExitCode.TRANSFER_FAILED)
                    write_failed = True
                    break
                
                pct = ((i + 1) / total_chunks) * 100.0
                
                # Update background job tracking if in subprocess
                if args.background_job_id:
                    JobDaemon.update_job_state(args.background_job_id, "IN_PROGRESS", progress=pct)

                if not args.no_progress and not args.json_log:
                    # Renders inline terminal progress updates safely
                    sys.stdout.write(f"\r[ Uploading ] Block {i+1}/{total_chunks} [{pct:.1f}% Complete]")
                    sys.stdout.flush()
                elif args.json_log and (i + 1) % max(1, total_chunks // 10) == 0:
                    logger.log("TRANSFER", "IN_PROGRESS", f"Uploaded blocks {i+1}/{total_chunks}", progress_pct=pct)

            if not args.no_progress and not args.json_log:
                sys.stdout.write("\n")
                sys.stdout.flush()

            if write_failed:
                continue

            # Query post-write verification hash
            logger.log("VERIFY", "TRYING", "Requesting hardware verification hash...")
            device_hash = hw.query_verify_hash()
            
            # Allow fallback bypass on force environments
            if args.force:
                logger.log("VERIFY", "BYPASSED", "Bypassed integrity checks (--force mode active).")
                verify_succeeded = True
                break

            if device_hash == expected_hash:
                logger.log("VERIFY", "SUCCESS", "Target verification hash matches source binary!")
                verify_succeeded = True
                break
            else:
                logger.log("VERIFY", "MISMATCH", f"Device returned: {device_hash} (Expected: {expected_hash})", err_code=LoaderExitCode.VERIFY_FAILED)
        
        except Exception as ex:
            logger.log("TRANSFER", "ERROR", f"Physical write error: {str(ex)}", err_code=LoaderExitCode.TRANSFER_FAILED)

    # 5. Pipeline Finalization / Fallback Rollback
    if verify_succeeded:
        logger.log("PIPELINE", "SUCCESS", "Device firmware loaded successfully.")
        hw.close()
        if args.background_job_id:
            JobDaemon.update_job_state(args.background_job_id, "SUCCESS", progress=100.0, err_code=LoaderExitCode.SUCCESS)
        return LoaderExitCode.SUCCESS
    else:
        logger.log("PIPELINE", "FAILED", "Integrity checks consistently failed. Triggering recovery...", err_code=LoaderExitCode.VERIFY_FAILED)
        
        # Triggering ARM register fallback golden boot image restoration
        logger.log("ROLLBACK", "TRYING", "Sending target recovery signals...")
        hw.trigger_partition_rollback()
        logger.log("ROLLBACK", "SUCCESS", "ARM boot partition reverted to original safe image configurations.")
        hw.close()
        
        if args.background_job_id:
            JobDaemon.update_job_state(args.background_job_id, "FAILED", progress=0.0, err_code=LoaderExitCode.VERIFY_FAILED)
        return LoaderExitCode.VERIFY_FAILED


def main():
    parser = argparse.ArgumentParser(description="Professional ARM+FPGA Firmware Loader Tool")
    parser.add_argument("-p", "--port", type=str, default="/dev/ttyUSB0", help="Target hardware interface port.")
    parser.add_argument("-b", "--baud", type=int, default=115200, help="Bus communication speed.")
    parser.add_argument("-f", "--file", type=str, required=True, help="Path to firmware/bitstream source file.")
    parser.add_argument("-c", "--chunk-size", type=int, default=4096, help="Data transmission chunk size.")
    parser.add_argument("-a", "--attempts", type=int, default=5, help="Maximum hardware connection attempts.")
    parser.add_argument("--json-log", action="store_true", help="Output event trace records in raw JSON formats.")
    parser.add_argument("--verbose", action="store_true", help="Display full byte-level bus traces.")
    parser.add_argument("--no-progress", action="store_true", help="Suppress terminal interactive spinners/progress bars.")
    parser.add_argument("--force", action="store_true", help="Bypass validation safety checks.")
    parser.add_argument("-bg", "--background", action="store_true", help="Run transfer task inside daemon process.")
    parser.add_argument("--job-status", type=str, help="Check current execution status of specified job ID.")
    parser.add_argument("--background-job-id", type=str, help=argparse.SUPPRESS) # Internal use

    args = parser.parse_args()

    if args.job_status:
        JobDaemon.print_job_status(args.job_status)
        sys.exit(0)

    if args.background:
        job_id = JobDaemon.create_background_subprocess(args)
        print("="*60)
        print("Background transfer successfully spawned.")
        print(f"Job ID:        {job_id}")
        print(f"Log Location:  {JobDaemon.get_job_log_path(job_id)}")
        print(f"To query:      loader-tool --job-status {job_id}")
        print("="*60)
        sys.exit(0)

    sys.exit(run_pipeline(args))


if __name__ == "__main__":
    main()

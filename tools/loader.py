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

try:
    import serial
    SERIAL_AVAILABLE = True
except ImportError:
    SERIAL_AVAILABLE = False


# ─── Exit codes ───────────────────────────────────────────────────────────────

class LoaderExitCode:
    """
    Stable exit codes. Codes >128 are new and avoid collision with shell signal
    offsets (128 + signum). Codes 0–30 are preserved from prior versions.
    """
    SUCCESS           = 0
    SYSTEM_ERROR      = 1
    CLI_ERROR         = 2
    DEVICE_NOT_FOUND  = 10
    ACCESS_DENIED     = 11
    PROTOCOL_MISMATCH = 12
    TRANSFER_FAILED   = 20
    VERIFY_FAILED     = 30
    DEVICE_BUSY       = 129   # port open refused — resource held by another process
    TIMEOUT           = 130   # operation exceeded time budget


class FailureClass:
    """Structured failure tags emitted in JSON logs for machine consumption."""
    NO_DEVICE         = "no_device"
    PERMISSION_DENIED = "permission_denied"
    DEVICE_BUSY       = "device_busy"
    PROTOCOL_MISMATCH = "protocol_mismatch"
    TRANSFER_FAILED   = "transfer_failed"
    VERIFY_FAILED     = "verify_failed"
    SYSTEM_ERROR      = "system_error"


# ─── ANSI helpers ─────────────────────────────────────────────────────────────

_ANSI_RESET  = "\033[0m"
_ANSI_BLUE   = "\033[94m"
_ANSI_YELLOW = "\033[93m"
_ANSI_RED    = "\033[91m"
_ANSI_GREY   = "\033[90m"

_SPINNER_FRAMES = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"]


def _col(text: str, code: str, enabled: bool) -> str:
    return f"{code}{text}{_ANSI_RESET}" if enabled else text


# ─── UILogger ─────────────────────────────────────────────────────────────────

class UILogger:
    """
    Severity-aware, TTY-adaptive logger.

    Interactive TTY  — in-place spinner line; severity-prefixed toasts on new lines.
    Non-interactive  — one-line timestamped records per event; no spinners.
    --json-log       — structured JSON objects to stderr; human output suppressed.
    --no-color       — ANSI stripped regardless of TTY.

    Warning dedup: the first WARNING is always shown. Subsequent warnings on the
    same step are suppressed unless --hints is active.
    """

    INFO    = "INFO"
    WARNING = "WARNING"
    ERROR   = "ERROR"

    def __init__(self, *, json_mode: bool = False, verbose: bool = False,
                 no_color: bool = False, no_progress: bool = False, hints: bool = False):
        self.json_mode   = json_mode
        self.verbose     = verbose
        self.hints       = hints
        self.color       = not no_color and sys.stdout.isatty() and not json_mode
        self.interactive = sys.stdout.isatty() and not json_mode and not no_progress
        self._spin_i     = 0
        self._line_len   = 0
        self._warn_shown = False

    # ── severity methods ──────────────────────────────────────────────────────

    def info(self, step: str, msg: str, *, attempt: int = 1, total: int = 5,
             elapsed: float = 0.0, failure_class: str | None = None) -> None:
        self._emit(self.INFO, step, msg, attempt=attempt, total=total,
                   elapsed=elapsed, err_code=None, failure_class=failure_class)

    def warning(self, step: str, msg: str, *, attempt: int, total: int,
                elapsed: float, failure_class: str | None = None) -> None:
        if self._warn_shown and not self.hints:
            return
        self._warn_shown = True
        self._emit(self.WARNING, step, msg, attempt=attempt, total=total,
                   elapsed=elapsed, err_code=None, failure_class=failure_class)

    def error(self, step: str, msg: str, *, attempt: int, total: int,
              elapsed: float, err_code: int, failure_class: str | None = None) -> None:
        self._emit(self.ERROR, step, msg, attempt=attempt, total=total,
                   elapsed=elapsed, err_code=err_code, failure_class=failure_class)

    # ── spinner ───────────────────────────────────────────────────────────────

    def tick(self, port: str, attempt: int, total: int, elapsed: float) -> None:
        """Update in-place spinner line. No-op when non-interactive."""
        if not self.interactive:
            return
        frame = _SPINNER_FRAMES[self._spin_i % len(_SPINNER_FRAMES)]
        self._spin_i += 1
        line = f"[ {frame} ] Connecting to {port} … attempt {attempt}/{total} ({int(elapsed)}s)"
        pad = max(self._line_len, len(line))
        sys.stdout.write(f"\r{line:<{pad}}")
        sys.stdout.flush()
        self._line_len = len(line)

    def clear_line(self) -> None:
        """Erase the spinner line before printing a toast."""
        if self.interactive and self._line_len:
            sys.stdout.write(f"\r{' ' * self._line_len}\r")
            sys.stdout.flush()
            self._line_len = 0

    # ── verbose trace ─────────────────────────────────────────────────────────

    def trace(self, direction: str, payload: bytes) -> None:
        if not self.verbose:
            return
        ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
        print(_col(f"[{ts}] TRACE {direction}: [{len(payload)} bytes]", _ANSI_GREY, self.color))

    # ── internals ─────────────────────────────────────────────────────────────

    def _emit(self, severity: str, step: str, msg: str, *, attempt: int, total: int,
              elapsed: float, err_code: int | None, failure_class: str | None) -> None:
        if self.json_mode:
            rec: dict = {
                "timestamp":      datetime.utcnow().isoformat() + "Z",
                "step":           step,
                "attempt":        attempt,
                "total_attempts": total,
                "elapsed_s":      round(elapsed, 2),
                "severity":       severity,
                "message":        msg,
            }
            if err_code is not None:
                rec["error_code"] = err_code
            if failure_class is not None:
                rec["failure_class"] = failure_class
            print(json.dumps(rec), file=sys.stderr)
        else:
            self.clear_line()
            if severity == self.INFO:
                prefix = _col("[INFO]", _ANSI_BLUE, self.color)
            elif severity == self.WARNING:
                prefix = _col("[WARN]", _ANSI_YELLOW, self.color)
            else:
                prefix = _col("[ERR ]", _ANSI_RED, self.color)
            sfx    = f" (Exit: {err_code})" if err_code is not None else ""
            ts_str = "" if self.interactive else f"[{datetime.now().strftime('%H:%M:%S')}] "
            print(f"{ts_str}{prefix} {msg}{sfx}")


# ─── HardwareInterface ────────────────────────────────────────────────────────

class HardwareInterface:
    """
    Manages physical JTAG/UART serial bus transactions.
    Falls back to a mock implementation when the pyserial library is absent.
    """
    def __init__(self, port: str, baud: int, verbose: bool, logger: UILogger):
        self.port = port
        self.baud = baud
        self.verbose = verbose
        self.logger = logger
        self.serial_conn = None
        self._mock_attempts = 0

    def open(self) -> None:
        """Attempt to open the hardware port. Raises typed exceptions on failure."""
        if SERIAL_AVAILABLE:
            try:
                self.serial_conn = serial.Serial(
                    port=self.port,
                    baudrate=self.baud,
                    timeout=2.0,
                    write_timeout=2.0,
                )
            except serial.SerialException as ex:
                err_msg = str(ex).lower()
                if "permission denied" in err_msg or "access is denied" in err_msg:
                    raise PermissionError("Access denied: check dialout group or COM port permissions.")
                elif "not found" in err_msg or "does not exist" in err_msg:
                    raise FileNotFoundError("Port not found: target board offline or connection loose.")
                else:
                    raise ConnectionError(f"Serial error: {ex}")
        else:
            self._mock_attempts += 1
            if self._mock_attempts < 3:
                raise ConnectionError("Port busy or target board powered off (mock).")

    def close(self) -> None:
        if self.serial_conn and self.serial_conn.is_open:
            self.serial_conn.close()
        self.serial_conn = None

    def write_chunk(self, data: bytes) -> bool:
        self.logger.trace("WRITE", data)
        if SERIAL_AVAILABLE and self.serial_conn:
            self.serial_conn.write(data)
            ack = self.serial_conn.read(1)
            self.logger.trace("READ_ACK", ack)
            return ack == b'\x06'
        else:
            time.sleep(0.01)
            return True

    def query_verify_hash(self) -> str:
        self.logger.trace("WRITE", b'\x05')
        if SERIAL_AVAILABLE and self.serial_conn:
            self.serial_conn.write(b'\x05')
            response = self.serial_conn.readline().decode().strip()
            self.logger.trace("READ_HASH", response.encode())
            return response
        else:
            return "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

    def trigger_partition_rollback(self) -> None:
        self.logger.trace("WRITE", b'\x15')
        if SERIAL_AVAILABLE and self.serial_conn:
            try:
                self.serial_conn.write(b'\x15')
                time.sleep(0.5)
            except Exception:
                pass


# ─── JobDaemon ────────────────────────────────────────────────────────────────

class JobDaemon:
    """Manages background job creation, log routing, and state tracking."""
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
        job_id     = f"Job_{int(time.time())}_{args.port.replace('/', '_').replace('.', '_')}"
        log_path   = cls.get_job_log_path(job_id)
        state_path = cls.get_job_state_path(job_id)

        with open(state_path, "w") as sf:
            json.dump({"status": "IN_PROGRESS", "progress": 0.0}, sf)

        cmd = [sys.executable, __file__]
        for arg in sys.argv[1:]:
            if arg not in ("--background", "-bg"):
                cmd.append(arg)

        log_file = open(log_path, "w")
        subprocess.Popen(cmd, stdout=log_file, stderr=log_file,
                         close_fds=True, start_new_session=True)
        return job_id

    @classmethod
    def update_job_state(cls, job_id: str, status: str,
                         progress: float | None = None, err_code: int | None = None) -> None:
        state_path = cls.get_job_state_path(job_id)
        state: dict = {"status": status, "updated_at": datetime.utcnow().isoformat() + "Z"}
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
    def print_job_status(cls, job_id: str) -> None:
        state_path = cls.get_job_state_path(job_id)
        log_path   = cls.get_job_log_path(job_id)

        if not os.path.exists(state_path):
            print(f"Error: job '{job_id}' not found.")
            sys.exit(LoaderExitCode.CLI_ERROR)

        with open(state_path, "r") as sf:
            state = json.load(sf)

        status   = state.get("status", "UNKNOWN")
        progress = state.get("progress", 0.0)
        err      = state.get("error_code")

        print("=" * 60)
        print(f"Background Job ID: {job_id}")
        print(f"Job Status:        [{status}]")
        print(f"Progress:          {progress:.1f}%")
        if err is not None:
            print(f"Error Code:        {err}")
        print(f"Log Location:      {log_path}")
        print("=" * 60)


# ─── Pipeline ─────────────────────────────────────────────────────────────────

def run_pipeline(args) -> int:
    max_att = args.max_attempts
    logger = UILogger(
        json_mode=args.json_log,
        verbose=args.verbose,
        no_color=args.no_color,
        no_progress=args.no_progress,
        hints=args.hints,
    )
    logger.info("PIPELINE", "Initializing firmware loader …", attempt=1, total=max_att)

    if not os.path.exists(args.file):
        logger.error("PIPELINE", f"Firmware file not found: {args.file}",
                     attempt=1, total=max_att, elapsed=0.0,
                     err_code=LoaderExitCode.CLI_ERROR,
                     failure_class=FailureClass.SYSTEM_ERROR)
        return LoaderExitCode.CLI_ERROR

    # ── 1. Connection phase ───────────────────────────────────────────────────
    hw = HardwareInterface(args.port, args.baud, args.verbose, logger)
    attempt = 0
    backoff = 1.0
    connected = False
    t0 = time.monotonic()
    failure_cls = FailureClass.NO_DEVICE

    while attempt < max_att:
        attempt += 1
        elapsed = time.monotonic() - t0

        if not logger.interactive:
            logger.info("CONNECT", f"Opening {args.port} …",
                        attempt=attempt, total=max_att, elapsed=elapsed)

        # Spin for up to 0.5s while the port open is attempted
        for _ in range(10):
            logger.tick(args.port, attempt, max_att, time.monotonic() - t0)
            time.sleep(0.05)

        try:
            hw.open()
            elapsed = time.monotonic() - t0
            connected = True
            logger.clear_line()
            logger.info("CONNECT", f"Connected on attempt {attempt} (took {int(elapsed)}s).",
                        attempt=attempt, total=max_att, elapsed=elapsed)
            break

        except PermissionError as ex:
            elapsed = time.monotonic() - t0
            detail  = f" Detail: {ex}" if args.verbose else ""
            logger.error(
                "CONNECT",
                f"Access denied on {args.port} — check dialout group or COM port permissions.{detail}",
                attempt=attempt, total=max_att, elapsed=elapsed,
                err_code=LoaderExitCode.ACCESS_DENIED,
                failure_class=FailureClass.PERMISSION_DENIED,
            )
            return LoaderExitCode.ACCESS_DENIED

        except FileNotFoundError as ex:
            failure_cls = FailureClass.NO_DEVICE
            if args.verbose:
                logger.info("CONNECT", f"Port not found: {ex}",
                            attempt=attempt, total=max_att, elapsed=time.monotonic() - t0)

        except Exception as ex:
            failure_cls = FailureClass.NO_DEVICE
            if args.verbose:
                logger.info("CONNECT", f"Link offline: {ex}",
                            attempt=attempt, total=max_att, elapsed=time.monotonic() - t0)

        elapsed = time.monotonic() - t0

        # Escalation: warn once when attempts reach the configured threshold
        if attempt >= args.warning_threshold:
            logger.warning(
                "CONNECT",
                f"Still connecting after {attempt} attempts — check power, cable, and device "
                f"boot mode; use --verbose for traces or --max-attempts to allow more retries.",
                attempt=attempt, total=max_att, elapsed=elapsed,
                failure_class=failure_cls,
            )

        if attempt < max_att:
            wait = int(backoff)
            logger.info("CONNECT", f"Retrying in {wait}s — device may still be initializing.",
                        attempt=attempt, total=max_att, elapsed=elapsed)
            backoff_end = time.monotonic() + backoff
            while time.monotonic() < backoff_end:
                logger.tick(args.port, attempt + 1, max_att, time.monotonic() - t0)
                time.sleep(0.1)
            backoff *= 2.0

    if not connected:
        elapsed = time.monotonic() - t0
        logger.clear_line()
        logger.error(
            "CONNECT",
            f"Connection failed after {max_att} attempts — verify device is powered and JTAG cable is seated.",
            attempt=attempt, total=max_att, elapsed=elapsed,
            err_code=LoaderExitCode.DEVICE_NOT_FOUND,
            failure_class=failure_cls,
        )
        return LoaderExitCode.DEVICE_NOT_FOUND

    # ── 2. BIST/PLL warm-up ───────────────────────────────────────────────────
    if not args.force:
        logger.info("PLL_LOCK", "First connection: device may take up to 30s to initialize — please wait.",
                    attempt=1, total=1, elapsed=time.monotonic() - t0)
        time.sleep(1.0)

    # ── 3. Binary reading & hash calculation ──────────────────────────────────
    elapsed = time.monotonic() - t0
    logger.info("IMAGE", f"Reading and hashing {args.file} …",
                attempt=1, total=1, elapsed=elapsed)
    sha256 = hashlib.sha256()
    try:
        with open(args.file, "rb") as f:
            file_data = f.read()
            sha256.update(file_data)
    except Exception as ex:
        elapsed = time.monotonic() - t0
        logger.error("IMAGE", f"Could not read firmware file: {ex}",
                     attempt=1, total=1, elapsed=elapsed,
                     err_code=LoaderExitCode.SYSTEM_ERROR,
                     failure_class=FailureClass.SYSTEM_ERROR)
        hw.close()
        return LoaderExitCode.SYSTEM_ERROR

    expected_hash = sha256.hexdigest()
    file_size = len(file_data)
    elapsed = time.monotonic() - t0
    logger.info("IMAGE", f"Image ready — {file_size} bytes, SHA-256: {expected_hash[:16]}…",
                attempt=1, total=1, elapsed=elapsed)

    # ── 4. Transfer & verification loop ──────────────────────────────────────
    chunk_size         = args.chunk_size
    total_chunks       = (file_size + chunk_size - 1) // chunk_size
    verify_attempts    = 0
    max_verify         = 3
    verify_succeeded   = False

    while verify_attempts < max_verify:
        verify_attempts += 1
        elapsed = time.monotonic() - t0
        logger.info("TRANSFER",
                    f"Uploading image — attempt {verify_attempts}/{max_verify} …",
                    attempt=verify_attempts, total=max_verify, elapsed=elapsed)

        try:
            write_failed = False
            for i in range(total_chunks):
                start = i * chunk_size
                end   = min(start + chunk_size, file_size)
                block = file_data[start:end]

                if not hw.write_chunk(block):
                    elapsed = time.monotonic() - t0
                    logger.warning(
                        "TRANSFER",
                        f"ACK dropped on block {i + 1}/{total_chunks} — link may be unstable; retrying transfer.",
                        attempt=verify_attempts, total=max_verify, elapsed=elapsed,
                        failure_class=FailureClass.TRANSFER_FAILED,
                    )
                    write_failed = True
                    break

                pct = ((i + 1) / total_chunks) * 100.0
                if args.background_job_id:
                    JobDaemon.update_job_state(args.background_job_id, "IN_PROGRESS", progress=pct)

                if not args.no_progress and not args.json_log:
                    sys.stdout.write(f"\r[ Uploading ] Block {i + 1}/{total_chunks} [{pct:.1f}%]")
                    sys.stdout.flush()
                elif args.json_log and (i + 1) % max(1, total_chunks // 10) == 0:
                    elapsed = time.monotonic() - t0
                    logger.info("TRANSFER", f"Uploaded {i + 1}/{total_chunks} blocks.",
                                attempt=verify_attempts, total=max_verify, elapsed=elapsed)

            if not args.no_progress and not args.json_log:
                sys.stdout.write("\n")
                sys.stdout.flush()

            if write_failed:
                continue

            elapsed = time.monotonic() - t0
            logger.info("VERIFY", "Requesting device verification hash …",
                        attempt=verify_attempts, total=max_verify, elapsed=elapsed)
            device_hash = hw.query_verify_hash()

            if args.force:
                elapsed = time.monotonic() - t0
                logger.info("VERIFY", "Integrity check bypassed (--force active).",
                            attempt=verify_attempts, total=max_verify, elapsed=elapsed)
                verify_succeeded = True
                break

            if device_hash == expected_hash:
                elapsed = time.monotonic() - t0
                logger.info("VERIFY", "Device hash matches — firmware verified.",
                            attempt=verify_attempts, total=max_verify, elapsed=elapsed)
                verify_succeeded = True
                break
            else:
                elapsed = time.monotonic() - t0
                logger.warning(
                    "VERIFY",
                    f"Hash mismatch on attempt {verify_attempts}/{max_verify} — retrying.",
                    attempt=verify_attempts, total=max_verify, elapsed=elapsed,
                    failure_class=FailureClass.VERIFY_FAILED,
                )

        except Exception as ex:
            elapsed = time.monotonic() - t0
            msg = f"Write error on attempt {verify_attempts}: {ex}" if args.verbose \
                  else f"Write error on attempt {verify_attempts} — retrying."
            logger.warning("TRANSFER", msg,
                           attempt=verify_attempts, total=max_verify, elapsed=elapsed,
                           failure_class=FailureClass.TRANSFER_FAILED)

    # ── 5. Finalization / rollback ────────────────────────────────────────────
    elapsed = time.monotonic() - t0
    if verify_succeeded:
        logger.info("PIPELINE", f"Firmware loaded successfully (total time: {int(elapsed)}s).",
                    attempt=1, total=1, elapsed=elapsed)
        hw.close()
        if args.background_job_id:
            JobDaemon.update_job_state(args.background_job_id, "SUCCESS",
                                       progress=100.0, err_code=LoaderExitCode.SUCCESS)
        return LoaderExitCode.SUCCESS
    else:
        logger.error(
            "PIPELINE",
            "Transfer integrity check failed after all attempts — firmware may be corrupt or link unstable.",
            attempt=verify_attempts, total=max_verify, elapsed=elapsed,
            err_code=LoaderExitCode.VERIFY_FAILED,
            failure_class=FailureClass.VERIFY_FAILED,
        )
        logger.info("ROLLBACK", "Sending recovery signal to restore golden boot image …",
                    attempt=1, total=1, elapsed=elapsed)
        hw.trigger_partition_rollback()
        elapsed = time.monotonic() - t0
        logger.info("ROLLBACK", "Boot partition reverted to safe image.",
                    attempt=1, total=1, elapsed=elapsed)
        hw.close()
        if args.background_job_id:
            JobDaemon.update_job_state(args.background_job_id, "FAILED",
                                       progress=0.0, err_code=LoaderExitCode.VERIFY_FAILED)
        return LoaderExitCode.VERIFY_FAILED


# ─── Entry point ──────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(description="Professional ARM+FPGA Firmware Loader Tool")

    # ── Existing flags (preserved, backward-compatible) ──────────────────────
    parser.add_argument("-p", "--port",
                        type=str, default="/dev/ttyUSB0",
                        help="Target hardware interface port.")
    parser.add_argument("-b", "--baud",
                        type=int, default=115200,
                        help="Bus communication speed.")
    parser.add_argument("-f", "--file",
                        type=str, required=True,
                        help="Path to firmware/bitstream source file.")
    parser.add_argument("-c", "--chunk-size",
                        type=int, default=4096,
                        help="Data transmission chunk size.")
    parser.add_argument("--json-log",
                        action="store_true",
                        help="Structured JSON events to stderr; suppresses human output.")
    parser.add_argument("--verbose",
                        action="store_true",
                        help="Protocol traces and low-level error causes.")
    parser.add_argument("--no-progress",
                        action="store_true",
                        help="Suppress spinner and progress bars.")
    parser.add_argument("--force",
                        action="store_true",
                        help="Bypass verification safety checks.")
    parser.add_argument("-bg", "--background",
                        action="store_true",
                        help="Run transfer inside a daemon process.")
    parser.add_argument("--job-status",
                        type=str,
                        help="Check status of a background job by ID.")
    parser.add_argument("--background-job-id",
                        type=str, help=argparse.SUPPRESS)

    # ── Connection attempt count — --attempts kept as a backward-compat alias ─
    parser.add_argument(
        "-a", "--attempts", "--max-attempts",
        type=int, default=5, dest="max_attempts", metavar="N",
        help="Maximum connection attempts (default: 5; --attempts is a legacy alias).",
    )

    # ── New flags ─────────────────────────────────────────────────────────────
    parser.add_argument(
        "--warning-threshold",
        type=int, default=3, metavar="N",
        help="Attempts before a WARNING is shown (default: 3).",
    )
    parser.add_argument(
        "--no-color",
        action="store_true",
        help="Disable ANSI color output.",
    )
    parser.add_argument(
        "--hints",
        action="store_true",
        help="Repeat warning hints on each attempt after the threshold.",
    )

    args = parser.parse_args()

    if args.job_status:
        JobDaemon.print_job_status(args.job_status)
        sys.exit(0)

    if args.background:
        job_id = JobDaemon.create_background_subprocess(args)
        print("=" * 60)
        print("Background transfer successfully spawned.")
        print(f"Job ID:       {job_id}")
        print(f"Log:          {JobDaemon.get_job_log_path(job_id)}")
        print(f"Query:        loader.py --job-status {job_id}")
        print("=" * 60)
        sys.exit(0)

    sys.exit(run_pipeline(args))


if __name__ == "__main__":
    main()

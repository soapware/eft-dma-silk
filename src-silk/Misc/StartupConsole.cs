// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc
{
    /// <summary>State of a startup step — controls the symbol and color printed.</summary>
    internal enum StepState { Pending, Ok, Warn, Fail }

    /// <summary>
    /// Live-status TUI startup console.
    /// Renders a fixed layout (5 status slots + tip box) and updates rows in-place via
    /// Console.SetCursorPosition. A 400ms timer blinks ▌ on the currently-active slot.
    /// All methods are no-ops when <see cref="Log.CleanMode"/> is false (debug mode).
    /// Thread-safe: all console operations serialised through <see cref="_lock"/>.
    /// </summary>
    internal static class StartupConsole
    {
        // ── Column/box dimensions ─────────────────────────────────────────────────
        private const int LabelW   = 10;  // label column width
        private const int MsgW     = 46;  // message column (padded to clear stale text)
        private const int BoxIW    = 65;  // tip box inner width — 65+4=69 chars total, matches banner
        private const int TipLines = 5;   // content rows inside tip box

        // ── Slot indices ──────────────────────────────────────────────────────────
        private const int SlotUSB     = 0;
        private const int SlotXilinx  = 1;
        private const int SlotGame    = 2;
        private const int SlotOffsets = 3;
        private const int SlotRadar   = 4;
        private const int SlotCount   = 5;

        private static readonly string[]    _labels = ["USB", "Xilinx", "Game", "Offsets", "Radar"];
        private static readonly string[]    _msgs   = new string[SlotCount];
        private static readonly StepState[] _states = new StepState[SlotCount];

        // ── Stored layout rows (absolute console buffer rows) ─────────────────────
        private static int _slot0Row = -1;   // console row of slot 0
        private static int _tipRow   = -1;   // console row of tip box top border ┌

        // ── Blink state ───────────────────────────────────────────────────────────
        private static int    _blinkSlot  = -1;   // which slot shows ▌ (-1 = none)
        private static bool   _blinkOn    = false;
        private static Timer? _blinkTimer;

        // ── Tip box content ───────────────────────────────────────────────────────
        private static string[] _tip = [];

        // ── Thread safety ─────────────────────────────────────────────────────────
        private static readonly object _lock = new();

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the full startup layout: banner, all status rows, and empty tip box.
        /// Records each row's console position for in-place updates. Starts blink timer.
        /// Called once at program entry.
        /// </summary>
        public static void PrintHeader()
        {
            if (!Log.CleanMode) return;
            Console.Title = "EFT (Silk.NET) - Console";
            Console.CursorVisible = false;

            lock (_lock)
            {
                const string title = "HuiTeab's EFT DMA Radar  |  Silk.NET Edition  (soapware fork)";
                string bar = new string('─', title.Length + 4);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine();
                Console.WriteLine($"  ┌{bar}┐");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"  │  {title}  │");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  └{bar}┘");
                Console.WriteLine();
                Console.ResetColor();

                // Record slot area and write initial rows
                _slot0Row = Console.CursorTop;
                for (int i = 0; i < SlotCount; i++)
                {
                    _msgs[i]   = "—";
                    _states[i] = StepState.Pending;
                    RenderSlotRow(i);
                    Console.WriteLine();
                }

                Console.WriteLine();

                // Tip box
                _tipRow = Console.CursorTop;
                _tip = [];
                RenderTipBox();

                // Blink timer: 400ms, toggles ▌ on active slot
                _blinkTimer?.Dispose();
                _blinkTimer = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        if (_slot0Row < 0 || _blinkSlot < 0) return;
                        try
                        {
                            _blinkOn = !_blinkOn;
                            Console.SetCursorPosition(0, _slot0Row + _blinkSlot);
                            RenderSlotRow(_blinkSlot);
                            RestoreCursor();
                        }
                        catch { /* swallow — console resize can throw */ }
                    }
                }, null, 400, 400);
            }
        }

        /// <summary>Updates the named status slot. Label must match one of the 5 slot names.</summary>
        public static void PrintStep(string label, string value, StepState state)
        {
            if (!Log.CleanMode) return;
            int slot = MapLabel(label);
            if (slot < 0) return;
            lock (_lock) { UpdateSlot(slot, value, state); RestoreCursor(); }
        }

        /// <summary>
        /// Updates the Xilinx slot with the current DMA connection attempt state and
        /// updates the tip box with attempt-range-specific guidance.
        /// </summary>
        public static void PrintDmaAttempt(int attempt, bool connected)
        {
            if (!Log.CleanMode) return;
            lock (_lock)
            {
                if (connected)
                {
                    UpdateSlot(SlotXilinx, $"Connected  (attempt {attempt})", StepState.Ok);
                    SetTip([]);
                    RestoreCursor();
                    return;
                }

                string msg = attempt switch
                {
                    1    => "Connecting...",
                    <= 3 => $"Attempt {attempt,2}  still connecting...",
                    <= 6 => $"Attempt {attempt,2}  waiting for card...",
                    <= 9 => $"Attempt {attempt,2}  card not responding",
                    _    => $"Attempt {attempt,2}  card not found",
                };
                UpdateSlot(SlotXilinx, msg, StepState.Pending);

                string[] tips = attempt switch
                {
                    <= 2  => [],
                    <= 5  => [
                        "Card not responding — Try:",
                        "  1. Unplug and replug the USB cable from the DMA card",
                        "  2. Use a USB 3.0 port (blue connector) on this PC",
                    ],
                    <= 9  => [
                        "Still not found — Check:",
                        "  1. PCIe slot seating — reseat the card if accessible",
                        "  2. Verify 'deviceStr' in config.json matches your card",
                        "  3. USB cable connected on both ends",
                    ],
                    _     => [
                        "Card unresponsive — Try:",
                        "  1. Verify 'deviceStr' in config.json matches your card type",
                        "  2. Fully reboot the game PC",
                        "  3. Check Device Manager for unknown / error USB devices",
                        "  4. Confirm FPGA firmware is loaded (check card LEDs)",
                    ],
                };
                SetTip(tips);
                RestoreCursor();
            }
        }

        /// <summary>Shows a USB-layer diagnostic in the tip box (from the per-5-attempt FT601 probe).</summary>
        public static void PrintUsbDiag(string message)
        {
            if (!Log.CleanMode) return;
            lock (_lock)
            {
                SetTip([
                    "USB: " + message,
                    "",
                    "If FT601 is not seen by the driver:",
                    "  • Try a different USB 3.0 port (blue connector)",
                    "  • Check Device Manager for unknown / error devices",
                ]);
                RestoreCursor();
            }
        }

        /// <summary>
        /// Resets the Game slot to Pending and clears the tip box.
        /// Call at the start of each new game-search session.
        /// </summary>
        public static void ResetErrorState()
        {
            if (!Log.CleanMode) return;
            lock (_lock)
            {
                UpdateSlot(SlotGame, "Waiting for EscapeFromTarkov.exe...", StepState.Pending);
                SetTip([]);
                RestoreCursor();
            }
        }

        /// <summary>Shows a generic error context + actionable suggestion in the tip box.</summary>
        public static void PrintError(string context, string suggestion)
        {
            if (!Log.CleanMode) return;
            lock (_lock)
            {
                SetTip([context, "", "→  " + suggestion]);
                RestoreCursor();
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static int MapLabel(string label) => label switch
        {
            "USB"     => SlotUSB,
            "Xilinx"  => SlotXilinx,
            "Game"    => SlotGame,
            "Offsets" => SlotOffsets,
            "Radar"   => SlotRadar,
            _         => -1,
        };

        private static void UpdateSlot(int slot, string msg, StepState state)
        {
            _msgs[slot]   = msg;
            _states[slot] = state;

            if (state == StepState.Pending)
            {
                _blinkSlot = slot;
            }
            else if (_blinkSlot == slot)
            {
                // Slot resolved — find the next still-pending slot (last one wins)
                _blinkSlot = Array.FindLastIndex(_states, s => s == StepState.Pending);
            }

            if (_slot0Row >= 0)
            {
                try
                {
                    Console.SetCursorPosition(0, _slot0Row + slot);
                    RenderSlotRow(slot);
                }
                catch { }
            }
        }

        private static void RenderSlotRow(int slot)
        {
            bool pending = _states[slot] == StepState.Pending;
            bool active  = pending && slot == _blinkSlot;

            // ── Symbol column ──────────────────────────────────────────────
            Console.Write("  ");
            if (!pending)
            {
                var (sym, clr) = _states[slot] switch
                {
                    StepState.Ok   => ("[OK]", ConsoleColor.Green),
                    StepState.Warn => ("[ !]", ConsoleColor.Yellow),
                    _              => ("[!!]", ConsoleColor.Red),
                };
                Console.ForegroundColor = clr;
                Console.Write(sym);
            }
            else
            {
                // Blink inside the bracket
                char b = (active && _blinkOn) ? '▌' : ' ';
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("[");
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.Write(b);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("]");
            }
            Console.Write(" ");

            // ── Label column ───────────────────────────────────────────────
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(_labels[slot].PadRight(LabelW));

            // ── Message column (truncate to MsgW to prevent stale-char overflow) ──
            ConsoleColor msgClr = pending              ? ConsoleColor.Gray
                                : _states[slot] == StepState.Ok   ? ConsoleColor.Green
                                : _states[slot] == StepState.Warn ? ConsoleColor.Yellow
                                : ConsoleColor.Red;
            Console.ForegroundColor = msgClr;
            string msgText = _msgs[slot] ?? string.Empty;
            if (msgText.Length > MsgW) msgText = msgText[..(MsgW - 1)] + "…";
            Console.Write(msgText.PadRight(MsgW));

            Console.ResetColor();
            Console.Write("\x1b[K"); // erase any stale chars to end of line
        }

        private static void SetTip(string[] lines)
        {
            _tip = lines;
            if (_tipRow >= 0)
            {
                try
                {
                    Console.SetCursorPosition(0, _tipRow);
                    RenderTipBox();
                }
                catch { }
            }
        }

        /// <summary>
        /// Writes the full tip box at the current cursor position.
        /// 9 rows total: ┌ + blank + TipLines content + blank + └
        /// </summary>
        private static void RenderTipBox()
        {
            string emptyInner = new string(' ', BoxIW);
            string bottomRule = new string('─', BoxIW);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  ┌─ STATUS {new string('─', BoxIW - 9)}┐");
            Console.WriteLine($"  │{emptyInner}│");

            for (int i = 0; i < TipLines; i++)
            {
                string line = i < _tip.Length ? _tip[i] : string.Empty;
                int max = BoxIW - 4;
                if (line.Length > max) line = line[..max];
                line = line.PadRight(max);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("  │  ");
                Console.ForegroundColor = (i == 0 && _tip.Length > 0) ? ConsoleColor.White : ConsoleColor.DarkGray;
                Console.Write(line);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  │");
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  │{emptyInner}│");
            Console.WriteLine($"  └{bottomRule}┘");
            Console.ResetColor();
        }

        /// <summary>Parks the cursor safely below the tip box so no future output overlaps the TUI.</summary>
        private static void RestoreCursor()
        {
            if (_tipRow < 0) return;
            try { Console.SetCursorPosition(0, _tipRow + TipLines + 4); } catch { }
        }
    }
}

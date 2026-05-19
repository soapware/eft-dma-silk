// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc
{
    /// <summary>State of a startup step — controls the symbol and color printed.</summary>
    internal enum StepState { Pending, Ok, Warn, Fail }

    /// <summary>
    /// Clean startup console presentation layer.
    /// All methods are no-ops when <see cref="Log.CleanMode"/> is false (debug mode).
    /// Thread-safe: writes are serialised through <see cref="Log"/>'s existing lock.
    /// </summary>
    internal static class StartupConsole
    {
        private const int LabelW = 12; // label column width (chars)

        // ── Attempt hint thresholds ──────────────────────────────────────────────
        private static readonly (int MinAttempt, string Hint)[] Hints =
        [
            (3,  "Try replugging the USB cable from the DMA card"),
            (6,  "Check PCIe slot — card may not be fully seated"),
            (10, "Verify 'deviceStr' in config.json matches your card type"),
        ];

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Prints the startup banner. Called once at program entry.</summary>
        public static void PrintHeader()
        {
            if (!Log.CleanMode) return;
            Console.Title = "EFT (Silk.NET) - Console";

            const string title = "EFT DMA Radar  |  Silk.NET Edition  (soapware)";
            string bar = new string('─', title.Length + 4); // ─ repeated, 2-space pad each side

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine();
            Console.WriteLine($"  ┌{bar}┐");   // ┌───┐
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  │  {title}  │"); // │  title  │
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  └{bar}┘");   // └───┘
            Console.WriteLine();
            Console.ResetColor();
        }

        /// <summary>Prints a single status row: symbol + label + value.</summary>
        public static void PrintStep(string label, string value, StepState state)
        {
            if (!Log.CleanMode) return;
            var (sym, labelColor, valueColor) = state switch
            {
                StepState.Ok      => ("[OK]", ConsoleColor.Green,    ConsoleColor.Green),
                StepState.Warn    => ("[ !]", ConsoleColor.Yellow,   ConsoleColor.Yellow),
                StepState.Fail    => ("[!!]", ConsoleColor.Red,      ConsoleColor.Red),
                _                 => ("[  ]", ConsoleColor.DarkGray, ConsoleColor.Gray),  // Pending
            };

            Console.ForegroundColor = labelColor;
            Console.Write($"  {sym} ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(label.PadRight(LabelW));
            Console.ForegroundColor = valueColor;
            Console.WriteLine(value);
            Console.ResetColor();
        }

        /// <summary>
        /// Prints a DMA connection attempt line with a color that shifts from
        /// green (early attempts) through yellow to red (many retries).
        /// Hints are printed on a second line when applicable.
        /// </summary>
        public static void PrintDmaAttempt(int attempt, bool connected)
        {
            if (!Log.CleanMode) return;

            var color = GetAttemptColor(attempt);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  [  ] {"Xilinx".PadRight(LabelW)}");
            Console.ForegroundColor = color;

            if (connected)
            {
                Console.WriteLine($"Connected  (attempt {attempt})");
                Console.ResetColor();
                return;
            }

            string status = attempt switch
            {
                1     => "Connecting...",
                <= 3  => $"Attempt {attempt,2} — still connecting...",
                <= 6  => $"Attempt {attempt,2} — waiting for card...",
                <= 9  => $"Attempt {attempt,2} — card not responding",
                _     => $"Attempt {attempt,2} — card not found",
            };
            Console.WriteLine(status);

            // Print hint on a second line when threshold is crossed
            string? hint = null;
            foreach (var (min, h) in Hints)
                if (attempt == min) { hint = h; break; }

            if (hint is not null)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"           {"".PadRight(LabelW)}>> {hint}");
            }

            Console.ResetColor();
        }

        // ── Retry-error state (for blinking-dot in-place update) ─────────────────
        private static bool _errorShown;
        private static int  _dotIdx;
        private static readonly string[] _retryDots = [".", "..", "..."];

        /// <summary>
        /// Resets error state so a fresh error block prints on next call.
        /// Call at the start of each new game-search session.
        /// </summary>
        public static void ResetErrorState()
        {
            if (_errorShown)
                Console.WriteLine(); // move off the partial dot line
            _errorShown = false;
            _dotIdx = 0;
        }

        /// <summary>
        /// First call: prints the error header + a suggestion line with a dot.
        /// Subsequent calls: overwrites only the suggestion line with a cycling dot
        /// so the console doesn't spam the same message on every retry.
        /// </summary>
        public static void PrintError(string context, string suggestion)
        {
            if (!Log.CleanMode) return;

            string pad = "".PadRight(LabelW);

            if (!_errorShown)
            {
                // Print the [!!] header on its own line, then the suggestion without newline
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  [!!] {context}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"       {pad}>> {suggestion} .");
                _errorShown = true;
                _dotIdx = 0;
            }
            else
            {
                // Overwrite the suggestion line with updated dot — \r returns to line start
                _dotIdx = (_dotIdx + 1) % _retryDots.Length;
                string dot = _retryDots[_dotIdx];
                Console.ForegroundColor = ConsoleColor.DarkGray;
                // Extra trailing spaces erase any previously longer dot sequence
                Console.Write($"\r       {pad}>> {suggestion} {dot}   ");
            }

            Console.ResetColor();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static ConsoleColor GetAttemptColor(int n) => n switch
        {
            <= 3 => ConsoleColor.Green,
            <= 6 => ConsoleColor.Yellow,
            <= 9 => ConsoleColor.DarkYellow,
            _    => ConsoleColor.Red,
        };
    }
}

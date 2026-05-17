// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.UI
{
    /// <summary>
    /// Loads font resources for SkiaSharp rendering.
    /// <para>
    /// <b>Regular</b> (Segoe UI from file) — full Cyrillic + Latin coverage for all label text:
    /// player names, scav names, boss names, loot labels, killfeed, tooltips.
    /// </para>
    /// <para>
    /// <b>MsGothic</b> (MS Gothic from msgothic.ttc) — status banners.
    /// <b>Consolas</b> (from consola.ttf) — player counter, info sub-text.
    /// </para>
    /// </summary>
    internal static class CustomFonts
    {
        private const string EmbeddedFontResourceName = "eft_dma_radar.Silk.NeoSansStdRegular.otf";

        /// <summary>
        /// Segoe UI — general-purpose label font with full Cyrillic + Latin coverage.
        /// Used for all player names, scav/boss names, loot labels, killfeed, tooltips.
        /// </summary>
        public static SKTypeface Regular { get; }

        /// <summary>MS Gothic — legacy, retained for compatibility.</summary>
        public static SKTypeface MsGothic { get; }

        /// <summary>Cutive Mono — status banners and idle-screen text.</summary>
        public static SKTypeface CutiveMono { get; }

        /// <summary>Consolas — player counter, info sub-lines, monospace readouts.</summary>
        public static SKTypeface Consolas { get; }

        static CustomFonts()
        {
            Regular    = LoadLabelFont();
            MsGothic   = LoadSystemFont("msgothic.ttc", 0, "MS Gothic", "MS PGothic", "Yu Gothic");
            CutiveMono = LoadCutiveMono();
            Consolas   = LoadSystemFont("consola.ttf",  0, "Consolas",  "Courier New", "Lucida Console");
        }

        private static SKTypeface LoadCutiveMono()
        {
            const string bundled = @"C:\DMA\eft-dma-radar-silk\src-silk\assets\fonts\CutiveMono-Regular.ttf";
            if (File.Exists(bundled))
            {
                var tf = SKTypeface.FromFile(bundled);
                if (tf is not null) return tf;
            }
            // Fallback to system monospace fonts
            foreach (var name in new[] { "Courier New", "Lucida Console", "Consolas" })
            {
                var tf = SKTypeface.FromFamilyName(name, SKFontStyle.Normal);
                if (tf is not null && !tf.FamilyName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return tf;
                tf?.Dispose();
            }
            return SKTypeface.Default;
        }

        // ── Segoe UI — Cyrillic-capable label font ───────────────────────────

        private static SKTypeface LoadLabelFont()
        {
            var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

            // Prefer file-path load — guaranteed to find the right typeface
            var path = Path.Combine(fontsDir, "segoeui.ttf");
            if (File.Exists(path))
            {
                var tf = SKTypeface.FromFile(path);
                if (tf is not null) return tf;
            }

            // Family-name fallbacks
            foreach (var name in new[] { "Segoe UI", "Arial", "Tahoma" })
            {
                var tf = SKTypeface.FromFamilyName(name);
                if (tf is not null && !tf.FamilyName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return tf;
                tf?.Dispose();
            }

            // Last resort: embedded NeoSansStd (no Cyrillic but won't crash)
            return LoadEmbedded(EmbeddedFontResourceName);
        }

        // ── System font loader (file-path first, family-name fallback) ────────

        private static SKTypeface LoadSystemFont(string fileName, int ttcIndex, params string[] familyNameFallbacks)
        {
            var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var path = Path.Combine(fontsDir, fileName);
            if (File.Exists(path))
            {
                var tf = SKTypeface.FromFile(path, ttcIndex);
                if (tf is not null) return tf;
            }
            foreach (var name in familyNameFallbacks)
            {
                var tf = SKTypeface.FromFamilyName(name);
                if (tf is not null && !tf.FamilyName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return tf;
                tf?.Dispose();
            }
            return SKTypeface.Default;
        }

        // ── Embedded font helpers ─────────────────────────────────────────────

        private static SKTypeface LoadEmbedded(string resourceName)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded font resource '{resourceName}' not found.");
            return SKTypeface.FromStream(stream);
        }

        /// <summary>
        /// Returns the raw bytes of the embedded Neo Sans Std font.
        /// Used by ImGui contexts that need to load the font from memory (separate from Skia).
        /// </summary>
        internal static byte[]? GetEmbeddedFontData()
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedFontResourceName);
                if (stream is null) return null;
                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                return data;
            }
            catch
            {
                return null;
            }
        }
    }
}

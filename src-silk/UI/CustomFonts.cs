// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.UI
{
    /// <summary>
    /// Loads embedded Neo Sans Std font resources for SkiaSharp rendering.
    /// Also provides system font references for MS Gothic (status banners) and Consolas (info text).
    /// </summary>
    internal static class CustomFonts
    {
        private const string FontResourceName = "eft_dma_radar.Silk.NeoSansStdRegular.otf";

        public static SKTypeface Regular { get; }

        /// <summary>MS Gothic — used for status banners and ESP status text.</summary>
        public static SKTypeface MsGothic { get; }

        /// <summary>Consolas — used for player counter, tooltips, and info sub-text.</summary>
        public static SKTypeface Consolas { get; }

        static CustomFonts()
        {
            Regular = LoadFont(FontResourceName);
            MsGothic = LoadSystemFont("MS Gothic", "MS PGothic", "Yu Gothic", "NSimSun");
            Consolas  = LoadSystemFont("Consolas", "Courier New", "Lucida Console");
        }

        private static SKTypeface LoadSystemFont(params string[] names)
        {
            foreach (var name in names)
            {
                var tf = SKTypeface.FromFamilyName(name);
                if (tf is not null && !tf.FamilyName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return tf;
                tf?.Dispose();
            }
            return SKTypeface.Default;
        }

        /// <summary>
        /// Returns the raw embedded font file bytes.
        /// Used by ImGui contexts that need to load the font from memory.
        /// </summary>
        internal static byte[]? GetEmbeddedFontData()
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FontResourceName);
                if (stream is null)
                    return null;

                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                return data;
            }
            catch
            {
                return null;
            }
        }

        private static SKTypeface LoadFont(string resourceName)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded font resource '{resourceName}' not found.");
            return SKTypeface.FromStream(stream);
        }
    }
}

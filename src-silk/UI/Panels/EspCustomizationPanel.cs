// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    /// <summary>
    /// ESP Visuals customization panel — per-feature toggles and inline color pickers
    /// modelled on the Copland render → players and render → indicators sub-tabs.
    /// </summary>
    internal static class EspCustomizationPanel
    {
        public static bool IsOpen { get; set; } = false;

        private static readonly IReadOnlyList<string> _fontNames =
            ["Regular (12 px)", "Consolas (11 px)", "Cutive Mono (14 px)"];

        private static SilkConfig Config => SilkProgram.Config;
        private static float UIScale => Config.UIScale;

        public static void Draw()
        {
            if (!IsOpen) return;

            ImGui.SetNextWindowSize(new Vector2(480f * UIScale, 460f * UIScale), ImGuiCond.FirstUseEver);
            bool open = IsOpen;
            ImGui.Begin("ESP Visuals", ref open);
            IsOpen = open;

            if (ImGui.BeginTabBar("##espvis-tabs"))
            {
                if (ImGui.BeginTabItem("Players"))    { DrawPlayersTab();    ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Indicators")) { DrawIndicatorsTab(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }

            ImGui.End();
        }

        // ── Players tab ───────────────────────────────────────────────────────────

        private static void DrawPlayersTab()
        {
            SectionHeader("Overlay");

            bool en = Config.EspShowPlayers;
            if (ImGui.Checkbox("##en", ref en)) { Config.EspShowPlayers = en; Config.MarkDirty(); }
            ImGui.SameLine(); ImGui.TextUnformatted("Enabled");
            Tip("Master toggle — show/hide all player ESP");

            bool showName = Config.EspShowName; uint nameClr = Config.EspNameColor;
            if (ColorFeatureRow("Name", ref showName, ref nameClr, "Name label above the bounding box"))
            { Config.EspShowName = showName; Config.EspNameColor = nameClr; Config.MarkDirty(); }

            uint boxClr = Config.EspBoxColorOvr;
            if (ColorOnlyRow("Box", ref boxClr, "Box outline color (alpha=0 → per-type player color)"))
            { Config.EspBoxColorOvr = boxClr; Config.MarkDirty(); }

            bool showHealth = Config.EspShowHealth; uint healthHi = Config.EspHealthColHigh;
            if (ColorFeatureRow("Health", ref showHealth, ref healthHi, "Health bar (high-health color)"))
            { Config.EspShowHealth = showHealth; Config.EspHealthColHigh = healthHi; Config.MarkDirty(); }

            bool showHNum = Config.EspShowHealthNum;
            if (ImGui.Checkbox("##hn", ref showHNum)) { Config.EspShowHealthNum = showHNum; Config.MarkDirty(); }
            ImGui.SameLine(); ImGui.TextUnformatted("Health Number");
            Tip("Show HP % number on the health bar");

            bool showWeapon = Config.EspShowWeapon; uint weapClr = Config.EspWeaponColor;
            if (ColorFeatureRow("Weapon", ref showWeapon, ref weapClr, "Current weapon short name label"))
            { Config.EspShowWeapon = showWeapon; Config.EspWeaponColor = weapClr; Config.MarkDirty(); }

            bool showHL = Config.EspShowHighlight; uint hlClr = Config.EspHighlightColor;
            if (ColorFeatureRow("Highlight Target", ref showHL, ref hlClr, "Tint on current aim target"))
            { Config.EspShowHighlight = showHL; Config.EspHighlightColor = hlClr; Config.MarkDirty(); }

            bool showDist = Config.EspShowDistLabel; uint distClr = Config.EspDistColor;
            if (ColorFeatureRow("Distance Label", ref showDist, ref distClr, "Distance in metres below box"))
            { Config.EspShowDistLabel = showDist; Config.EspDistColor = distClr; Config.MarkDirty(); }

            SectionHeader("Range");

            float d = Config.EspPlayerDistance;
            if (UIControls.StepperFloat("Max Distance", ref d, 10f, 2000f, 25f, "{0:0} m",
                "Cull players farther than this"))
            { Config.EspPlayerDistance = d; Config.MarkDirty(); }
        }

        // ── Indicators tab ────────────────────────────────────────────────────────

        private static void DrawIndicatorsTab()
        {
            SectionHeader("Settings");

            int fi = Config.EspNameFontIdx;
            if (UIControls.ComboRow("Name Font", ref fi, _fontNames, "Font used for player name labels"))
            { Config.EspNameFontIdx = fi; Config.MarkDirty(); }

            SectionHeader("Health Colors");

            uint hh = Config.EspHealthColHigh;
            uint hm = Config.EspHealthColMid;
            uint hl = Config.EspHealthColLow;
            if (ColorOnlyRow("High  (> 50 %)",  ref hh, "Health bar fill when HP > 50%"))
            { Config.EspHealthColHigh = hh; Config.MarkDirty(); }
            if (ColorOnlyRow("Mid   (25–50 %)", ref hm, "Health bar fill when HP 25–50%"))
            { Config.EspHealthColMid = hm; Config.MarkDirty(); }
            if (ColorOnlyRow("Low   (< 25 %)",  ref hl, "Health bar fill when HP < 25%"))
            { Config.EspHealthColLow = hl; Config.MarkDirty(); }

            SectionHeader("Flags");

            bool fd = Config.EspFlagDistance; uint fdClr = Config.EspFlagDistColor;
            if (ColorFeatureRow("Distance", ref fd, ref fdClr, "Append distance next to name"))
            { Config.EspFlagDistance = fd; Config.EspFlagDistColor = fdClr; Config.MarkDirty(); }

            bool fa = Config.EspFlagAimTarget; uint faClr = Config.EspFlagAimColor;
            if (ColorFeatureRow("Aim Target", ref fa, ref faClr, "Indicator when player is your aim target"))
            { Config.EspFlagAimTarget = fa; Config.EspFlagAimColor = faClr; Config.MarkDirty(); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        // Checkbox on left, label, color swatch on right — returns true if anything changed
        private static bool ColorFeatureRow(string label, ref bool toggle, ref uint color, string tip)
        {
            bool changed  = false;
            float swatchSz = 16f * UIScale;

            bool t = toggle;
            if (ImGui.Checkbox("##t" + label, ref t)) { toggle = t; changed = true; }
            ImGui.SameLine();
            ImGui.TextUnformatted(label);
            Tip(tip);

            ImGui.SameLine(ImGui.GetContentRegionMax().X - swatchSz);
            if (ColorSwatch("##c" + label, ref color, label + " color")) changed = true;

            return changed;
        }

        // Dim label + color swatch only — returns true if color changed
        private static bool ColorOnlyRow(string label, ref uint color, string tip)
        {
            float swatchSz = 16f * UIScale;
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.60f, 0.65f, 1f));
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();
            Tip(tip);

            ImGui.SameLine(ImGui.GetContentRegionMax().X - swatchSz);
            return ColorSwatch("##co" + label, ref color, tip);
        }

        // Small color button that opens a full color picker popup — returns true if changed
        private static bool ColorSwatch(string id, ref uint color, string tooltip)
        {
            float sz   = 16f * UIScale;
            var vec    = ToVec4(color);
            bool changed = false;

            if (ImGui.ColorButton(id, vec,
                ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoBorder,
                new Vector2(sz, sz)))
                ImGui.OpenPopup(id + "p");

            Tip(tooltip);

            if (ImGui.BeginPopup(id + "p"))
            {
                if (ImGui.ColorPicker4(id + "pk", ref vec,
                    ImGuiColorEditFlags.AlphaBar |
                    ImGuiColorEditFlags.DisplayRGB |
                    ImGuiColorEditFlags.DisplayHex))
                { color = FromVec4(vec); changed = true; }
                ImGui.EndPopup();
            }
            return changed;
        }

        private static void SectionHeader(string label)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.58f, 0.62f, 1f));
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();
            ImGui.Separator();
            ImGui.Spacing();
        }

        private static void Tip(string tip)
        {
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        }

        private static Vector4 ToVec4(uint c) =>
            new((c >> 16 & 0xFF) / 255f, (c >> 8 & 0xFF) / 255f, (c & 0xFF) / 255f, (c >> 24 & 0xFF) / 255f);

        private static uint FromVec4(Vector4 v) =>
            ((uint)(v.W * 255) << 24) | ((uint)(v.X * 255) << 16) | ((uint)(v.Y * 255) << 8) | (uint)(v.Z * 255);
    }
}

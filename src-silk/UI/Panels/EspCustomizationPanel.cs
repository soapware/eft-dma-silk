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

            ImGui.SetNextWindowSize(new Vector2(520f * UIScale, 480f * UIScale), ImGuiCond.FirstUseEver);
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
            UIControls.Section("Overlay");

            bool en = Config.EspShowPlayers;
            if (UIControls.ToggleRow("Enabled", ref en, "Master toggle — show/hide all player ESP"))
            { Config.EspShowPlayers = en; Config.MarkDirty(); }

            // Name
            bool showName = Config.EspShowName; uint nameClr = Config.EspNameColor;
            if (ColorFeatureRow("Name", ref showName, ref nameClr, "Name label above the bounding box"))
            { Config.EspShowName = showName; Config.EspNameColor = nameClr; Config.MarkDirty(); }

            // Box (no toggle, just color override)
            uint boxClr = Config.EspBoxColorOvr;
            if (ColorOnlyRow("Box", ref boxClr, "Box outline color (alpha=0 → per-type player color)"))
            { Config.EspBoxColorOvr = boxClr; Config.MarkDirty(); }

            // Health
            bool showHealth = Config.EspShowHealth; uint healthHi = Config.EspHealthColHigh;
            if (ColorFeatureRow("Health", ref showHealth, ref healthHi, "Health bar (high-health color)"))
            { Config.EspShowHealth = showHealth; Config.EspHealthColHigh = healthHi; Config.MarkDirty(); }

            // Health number
            bool showHNum = Config.EspShowHealthNum;
            if (UIControls.ToggleRow("Health Number", ref showHNum, "Show HP % number on the health bar"))
            { Config.EspShowHealthNum = showHNum; Config.MarkDirty(); }

            // Weapon
            bool showWeapon = Config.EspShowWeapon; uint weapClr = Config.EspWeaponColor;
            if (ColorFeatureRow("Weapon", ref showWeapon, ref weapClr, "Current weapon short name label"))
            { Config.EspShowWeapon = showWeapon; Config.EspWeaponColor = weapClr; Config.MarkDirty(); }

            // Highlight target
            bool showHL = Config.EspShowHighlight; uint hlClr = Config.EspHighlightColor;
            if (ColorFeatureRow("Highlight Target", ref showHL, ref hlClr, "Tint on current aim target"))
            { Config.EspShowHighlight = showHL; Config.EspHighlightColor = hlClr; Config.MarkDirty(); }

            // Distance label
            bool showDist = Config.EspShowDistLabel; uint distClr = Config.EspDistColor;
            if (ColorFeatureRow("Distance Label", ref showDist, ref distClr, "Distance in metres below the box"))
            { Config.EspShowDistLabel = showDist; Config.EspDistColor = distClr; Config.MarkDirty(); }

            UIControls.Section("Range");

            float d = Config.EspPlayerDistance;
            if (UIControls.StepperFloat("Max Distance", ref d, 10f, 2000f, 25f, "{0:0} m",
                "Cull players farther than this"))
            { Config.EspPlayerDistance = d; Config.MarkDirty(); }
        }

        // ── Indicators tab ────────────────────────────────────────────────────────

        private static void DrawIndicatorsTab()
        {
            UIControls.Section("Settings");

            int fi = Config.EspNameFontIdx;
            if (UIControls.ComboRow("Name Font", ref fi, _fontNames, "Font used for player name labels"))
            { Config.EspNameFontIdx = fi; Config.MarkDirty(); }

            UIControls.Section("Health Colors");

            uint hh = Config.EspHealthColHigh;
            uint hm = Config.EspHealthColMid;
            uint hl = Config.EspHealthColLow;
            if (ColorOnlyRow("High  (> 50 %)",  ref hh, "Health bar fill when HP > 50%"))
            { Config.EspHealthColHigh = hh; Config.MarkDirty(); }
            if (ColorOnlyRow("Mid   (25–50 %)", ref hm, "Health bar fill when HP 25–50%"))
            { Config.EspHealthColMid = hm; Config.MarkDirty(); }
            if (ColorOnlyRow("Low   (< 25 %)",  ref hl, "Health bar fill when HP < 25%"))
            { Config.EspHealthColLow = hl; Config.MarkDirty(); }

            UIControls.Section("Flags");

            bool fd = Config.EspFlagDistance; uint fdClr = Config.EspFlagDistColor;
            if (ColorFeatureRow("Distance", ref fd, ref fdClr, "Append distance next to name"))
            { Config.EspFlagDistance = fd; Config.EspFlagDistColor = fdClr; Config.MarkDirty(); }

            bool fa = Config.EspFlagAimTarget; uint faClr = Config.EspFlagAimColor;
            if (ColorFeatureRow("Aim Target", ref fa, ref faClr, "Indicator when player is your aim target"))
            { Config.EspFlagAimTarget = fa; Config.EspFlagAimColor = faClr; Config.MarkDirty(); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        // Returns true if anything changed
        private static bool ColorFeatureRow(string label, ref bool toggle, ref uint color, string tip)
        {
            bool changed   = false;
            float swatchSz = 16f * UIScale;
            float rowH     = 36f * UIScale;
            float availW   = ImGui.GetContentRegionAvail().X;
            var   curPos   = ImGui.GetCursorPos();
            var   curScr   = ImGui.GetCursorScreenPos();

            // Clip toggle draw area to leave space for swatch
            ImGui.PushClipRect(
                curScr,
                new Vector2(curScr.X + availW - swatchSz - 10f * UIScale, curScr.Y + rowH),
                true);
            bool t = toggle;
            if (UIControls.ToggleRow(label, ref t, tip)) { toggle = t; changed = true; }
            ImGui.PopClipRect();

            // Color swatch right-aligned on same row
            ImGui.SetCursorPos(new Vector2(curPos.X + availW - swatchSz - 4f * UIScale,
                                           curPos.Y + (rowH - swatchSz) * 0.5f));
            if (ColorSwatch("##c" + label, ref color, label + " color")) changed = true;
            return changed;
        }

        // Row with just dim label + color swatch, returns true if color changed
        private static bool ColorOnlyRow(string label, ref uint color, string tip)
        {
            float swatchSz = 16f * UIScale;
            float availW   = ImGui.GetContentRegionAvail().X;

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.60f, 0.65f, 1f));
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);

            ImGui.SameLine(availW - swatchSz - 4f * UIScale);
            return ColorSwatch("##co" + label, ref color, tip);
        }

        // Returns true if color changed
        private static bool ColorSwatch(string id, ref uint color, string tooltip)
        {
            float sz   = 16f * UIScale;
            var vec    = ToVec4(color);
            bool changed = false;

            if (ImGui.ColorButton(id, vec,
                ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoBorder,
                new Vector2(sz, sz)))
                ImGui.OpenPopup(id + "p");

            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

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

        private static Vector4 ToVec4(uint c) =>
            new((c >> 16 & 0xFF) / 255f, (c >> 8 & 0xFF) / 255f, (c & 0xFF) / 255f, (c >> 24 & 0xFF) / 255f);

        private static uint FromVec4(Vector4 v) =>
            ((uint)(v.W * 255) << 24) | ((uint)(v.X * 255) << 16) | ((uint)(v.Y * 255) << 8) | (uint)(v.Z * 255);
    }
}

// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    /// <summary>
    /// ESP Visuals customization panel — per-feature toggles and inline color pickers.
    /// </summary>
    internal static class EspCustomizationPanel
    {
        public static bool IsOpen { get; set; } = false;

        private static readonly IReadOnlyList<string> _fontNames =
            ["Segoe UI (12 px)", "Arial (11 px)", "Tahoma (11 px)", "Courier New (11 px)"];

        private static readonly IReadOnlyList<string> _boxStyles =
            ["Corners", "Full", "Top + Bottom"];

        private static SilkConfig Config => SilkProgram.Config;
        private static float UIScale => Config.UIScale;

        // Flags used for every color picker button
        private const ImGuiColorEditFlags PickerFlags =
            ImGuiColorEditFlags.NoLabel    |
            ImGuiColorEditFlags.NoInputs   |
            ImGuiColorEditFlags.AlphaPreview |
            ImGuiColorEditFlags.AlphaBar;

        public static void Draw()
        {
            if (!IsOpen) return;
            bool isOpen = IsOpen;
            using var scope = PanelWindow.Begin("ESP Visuals", ref isOpen, new Vector2(480, 520));
            if (!isOpen && IsOpen)
                ImGui.CloseCurrentPopup(); // flush any dangling combo/color popup before the window closes
            IsOpen = isOpen;
            if (!scope.Visible) return;

            if (ImGui.BeginTabBar("##espvis-tabs"))
            {
                try
                {
                    if (ImGui.BeginTabItem("Players"))
                        { try { ImGui.PushID("players_tab"); DrawPlayersTab(); } finally { ImGui.PopID(); ImGui.EndTabItem(); } }
                    if (ImGui.BeginTabItem("Box"))
                        { try { ImGui.PushID("box_tab"); DrawBoxTab(); } finally { ImGui.PopID(); ImGui.EndTabItem(); } }
                    if (ImGui.BeginTabItem("Labels"))
                        { try { ImGui.PushID("labels_tab"); DrawLabelsTab(); } finally { ImGui.PopID(); ImGui.EndTabItem(); } }
                    if (ImGui.BeginTabItem("Loot"))
                        { try { ImGui.PushID("loot_tab"); DrawLootTab(); } finally { ImGui.PopID(); ImGui.EndTabItem(); } }
                    if (ImGui.BeginTabItem("Ballistics"))
                        { try { ImGui.PushID("ballistics_tab"); DrawBallisticsTab(); } finally { ImGui.PopID(); ImGui.EndTabItem(); } }
                    if (ImGui.BeginTabItem("Window"))
                        { try { ImGui.PushID("window_tab"); DrawWindowTab(); } finally { ImGui.PopID(); ImGui.EndTabItem(); } }
                }
                finally
                {
                    ImGui.EndTabBar();
                }
            }
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
            if (ColorRow("Name", ref showName, ref nameClr, "Name label above the bounding box"))
            { Config.EspShowName = showName; Config.EspNameColor = nameClr; Config.MarkDirty(); }

            uint boxClr = Config.EspBoxColorOvr;
            if (ColorOnlyRow("Box", ref boxClr, "Box outline color (alpha=0 → per-type player color)"))
            { Config.EspBoxColorOvr = boxClr; Config.MarkDirty(); }

            bool showHealth = Config.EspShowHealth; uint healthHi = Config.EspHealthColHigh;
            if (ColorRow("Health", ref showHealth, ref healthHi, "Health bar (high-health color)"))
            { Config.EspShowHealth = showHealth; Config.EspHealthColHigh = healthHi; Config.MarkDirty(); }

            bool showHNum = Config.EspShowHealthNum;
            if (ImGui.Checkbox("##hn", ref showHNum)) { Config.EspShowHealthNum = showHNum; Config.MarkDirty(); }
            ImGui.SameLine(); ImGui.TextUnformatted("Health Number");
            Tip("Show HP % number on the health bar");

            bool showWeapon = Config.EspShowWeapon; uint weapClr = Config.EspWeaponColor;
            if (ColorRow("Weapon", ref showWeapon, ref weapClr, "Current weapon short name label"))
            { Config.EspShowWeapon = showWeapon; Config.EspWeaponColor = weapClr; Config.MarkDirty(); }

            bool showHL = Config.EspShowHighlight; uint hlClr = Config.EspHighlightColor;
            if (ColorRow("Highlight Target", ref showHL, ref hlClr, "Tint on current aim target"))
            { Config.EspShowHighlight = showHL; Config.EspHighlightColor = hlClr; Config.MarkDirty(); }

            bool showDist = Config.EspShowDistLabel; uint distClr = Config.EspDistColor;
            if (ColorRow("Distance Label", ref showDist, ref distClr, "Distance in metres below box"))
            { Config.EspShowDistLabel = showDist; Config.EspDistColor = distClr; Config.MarkDirty(); }

            SectionHeader("Range");

            float d = Config.EspPlayerDistance;
            if (UIControls.StepperFloat("Max Distance", ref d, 10f, 2000f, 25f, "{0:0} m",
                "Cull players farther than this"))
            { Config.EspPlayerDistance = d; Config.MarkDirty(); }
        }

        // ── Labels tab ────────────────────────────────────────────────────────────

        private static void DrawLabelsTab()
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
            if (ColorRow("Distance", ref fd, ref fdClr, "Append distance next to name"))
            { Config.EspFlagDistance = fd; Config.EspFlagDistColor = fdClr; Config.MarkDirty(); }

            bool fa = Config.EspFlagAimTarget; uint faClr = Config.EspFlagAimColor;
            if (ColorRow("Aim Target", ref fa, ref faClr, "Indicator when player is your aim target"))
            { Config.EspFlagAimTarget = fa; Config.EspFlagAimColor = faClr; Config.MarkDirty(); }
        }

        // ── Box tab ───────────────────────────────────────────────────────────────

        private static void DrawBoxTab()
        {
            SectionHeader("Style");

            int style = Config.EspBoxStyle;
            if (UIControls.ComboRow("Box Style", ref style, _boxStyles, "Shape of the bounding box"))
            { Config.EspBoxStyle = style; Config.MarkDirty(); }

            SectionHeader("Corners");

            float cf = Config.EspBoxCornerFr;
            ImGui.TextUnformatted("Corner Length");
            Tip("How much of each side is drawn as a corner (Corners style only)");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##cf", ref cf, 0.05f, 0.49f, "%.2f"))
            { Config.EspBoxCornerFr = cf; Config.MarkDirty(); }

            SectionHeader("Thickness");

            float th = Config.EspBoxThickness;
            ImGui.TextUnformatted("Line Thickness");
            Tip("Stroke width in pixels");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##th", ref th, 0.5f, 5f, "%.1f px"))
            { Config.EspBoxThickness = th; Config.MarkDirty(); }
        }

        // ── Loot tab ──────────────────────────────────────────────────────────────

        private static void DrawLootTab()
        {
            SectionHeader("Corpse Boxes");

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.55f, 0.6f, 1f));
            ImGui.TextUnformatted("Toggle: Settings → ESP → Corpses");
            ImGui.PopStyleColor();

            ImGui.PushID("corpses");
            uint corpseClr = Config.EspCorpseBoxColor;
            if (ColorOnlyRow("Box Color", ref corpseClr, "Corpse bounding box outline and name label color"))
            { Config.EspCorpseBoxColor = corpseClr; Config.MarkDirty(); }
            ImGui.PopID();

            SectionHeader("Backpack Boxes");

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.55f, 0.6f, 1f));
            ImGui.TextUnformatted("Toggle: Settings → ESP → Backpacks");
            ImGui.PopStyleColor();

            ImGui.PushID("backpacks");
            uint bpClr = Config.EspBackpackBoxColor;
            if (ColorOnlyRow("Box Color", ref bpClr, "Backpack bounding box outline and name label color"))
            { Config.EspBackpackBoxColor = bpClr; Config.MarkDirty(); }
            ImGui.PopID();

            SectionHeader("Containers");

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.55f, 0.6f, 1f));
            ImGui.TextUnformatted("Toggle: Settings → ESP → Containers");
            ImGui.PopStyleColor();
        }

        // ── Ballistics tab ────────────────────────────────────────────────────────

        private static void DrawBallisticsTab()
        {
            var bcfg = Config.Ballistics ??= new();

            SectionHeader("Local Player Trails");

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.55f, 0.6f, 1f));
            ImGui.TextUnformatted("Toggles: Settings → ESP → Ballistics");
            ImGui.PopStyleColor();

            uint hitCol = bcfg.LocalShotHitColor;
            if (ColorOnlyRow("Hit Trail Color", ref hitCol,
                "Color applied to your bullet trail the moment it hits something (wall, enemy, floor).\nSet alpha to 0 to keep the trail at the highlight color instead."))
            { bcfg.LocalShotHitColor = hitCol; Config.MarkDirty(); }
        }

        // ── Window tab ────────────────────────────────────────────────────────────

        private static void DrawWindowTab()
        {
            SectionHeader("Overlay Opacity");

            ImGui.TextUnformatted("ESP Window Opacity");
            Tip("Transparency of the entire ESP overlay window (100 = fully opaque)");
            ImGui.SetNextItemWidth(-1);
            int opacity = Config.EspOpacity;
            if (ImGui.SliderInt("##op", ref opacity, 10, 100, "%d %%"))
            { Config.EspOpacity = opacity; Config.MarkDirty(); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        // Checkbox + label + inline color picker — returns true if anything changed
        private static bool ColorRow(string label, ref bool toggle, ref uint color, string tip)
        {
            bool changed = false;
            bool t = toggle;
            if (ImGui.Checkbox("##t" + label, ref t)) { toggle = t; changed = true; }
            ImGui.SameLine();
            ImGui.TextUnformatted(label);
            Tip(tip);
            ImGui.SameLine();
            var vec = ToVec4(color);
            if (ImGui.ColorEdit4("##c" + label, ref vec, PickerFlags))
            { color = FromVec4(vec); changed = true; }
            return changed;
        }

        // Dim label + inline color picker — returns true if color changed
        private static bool ColorOnlyRow(string label, ref uint color, string tip)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.60f, 0.65f, 1f));
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();
            Tip(tip);
            ImGui.SameLine();
            var vec = ToVec4(color);
            bool changed = false;
            if (ImGui.ColorEdit4("##co" + label, ref vec, PickerFlags))
            { color = FromVec4(vec); changed = true; }
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

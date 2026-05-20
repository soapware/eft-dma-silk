// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Features.Ballistics;
using eft_dma_radar.Silk.UI.Widgets;
using ImGuiNET;

namespace eft_dma_radar.Silk.UI.Panels
{
    internal static partial class SettingsPanel
    {
        private static readonly string[] _espRenderModes = ["None", "Bones", "Box", "Head Dot"];
        private static readonly string[] _espCrosshairTypes = ["Plus", "Cross", "Circle", "Dot", "Square", "Diamond"];

        private static List<MonitorInfo>? _monitors;
        private static string[]? _monitorNames;

        private static void RefreshMonitors()
        {
            _monitors = MonitorInfo.GetAllMonitors();
            _monitorNames = _monitors.Select(m => m.DisplayName).ToArray();
        }

        private static void DrawEspTab()
        {
            ImGui.Spacing();

            // ── Window state ──
            bool open = eft_dma_radar.Silk.UI.ESP.EspWindow.IsOpen;
            if (UIControls.ToggleRow("ESP Window Open", ref open))
            {
                eft_dma_radar.Silk.UI.ESP.EspWindow.Toggle();
                Config.ShowEspWidget = eft_dma_radar.Silk.UI.ESP.EspWindow.IsOpen;
                Config.MarkDirty();
            }

            int espFps = Config.EspTargetFps;
            if (UIControls.Stepper("ESP Target FPS", ref espFps, 0, 360, 5,
                tooltip: "Render rate of the ESP window (0 = unlimited).\nIndependent of the radar FPS."))
            {
                Config.EspTargetFps = espFps;
                eft_dma_radar.Silk.UI.ESP.EspWindow.ApplyTargetFps();
                Config.MarkDirty();
            }

            UIControls.Section("Monitor");

            if (_monitors is null || _monitorNames is null)
                RefreshMonitors();

            int targetScreen = Config.EspTargetScreen;
            if (UIControls.ComboRow("Target Monitor", ref targetScreen, _monitorNames!,
                "Which monitor the ESP window opens on.\nUse 'Move ESP to Monitor' to reposition a running window."))
            {
                Config.EspTargetScreen = targetScreen;
                Config.MarkDirty();
            }

            if (ImGui.SmallButton("Refresh Monitors"))
                RefreshMonitors();

            if (eft_dma_radar.Silk.UI.ESP.EspWindow.IsOpen)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Move ESP to Monitor"))
                    eft_dma_radar.Silk.UI.ESP.EspWindow.ApplyTargetMonitor();
            }

            UIControls.Section("Players");

            bool showPlayers = Config.EspShowPlayers;
            if (UIControls.ToggleRow("Show Players", ref showPlayers))
            {
                Config.EspShowPlayers = showPlayers;
                Config.MarkDirty();
            }

            int mode = Config.EspRenderMode;
            if (UIControls.ComboRow("Render Mode", ref mode, _espRenderModes,
                "How each player is drawn.\nAlso cyclable via hotkey."))
            {
                Config.EspRenderMode = mode;
                Config.MarkDirty();
            }

            if (mode == 2) // Box
            {
                bool bones = Config.EspShowBones;
                if (UIControls.ToggleRow("Show Bones Inside Box", ref bones))
                {
                    Config.EspShowBones = bones;
                    Config.MarkDirty();
                }
            }

            float pDist = Config.EspPlayerDistance;
            if (UIControls.StepperFloat("Max Distance", ref pDist, 10f, 2000f, 10f, "{0:0}m",
                "Players beyond this distance are not drawn"))
            {
                Config.EspPlayerDistance = pDist;
                Config.MarkDirty();
            }

            UIControls.Section("Loot");

            bool showLoot = Config.EspShowLoot;
            if (UIControls.ToggleRow("Show Loot", ref showLoot))
            {
                Config.EspShowLoot = showLoot;
                Config.MarkDirty();
            }

            float lDist = Config.EspLootDistance;
            if (UIControls.StepperFloat("Max Distance ", ref lDist, 10f, 500f, 5f, "{0:0}m",
                "Loot beyond this distance is not drawn"))
            {
                Config.EspLootDistance = lDist;
                Config.MarkDirty();
            }

            UIControls.Section("Crosshair");

            bool crosshair = Config.EspShowCrosshair;
            if (UIControls.ToggleRow("Show Crosshair", ref crosshair))
            {
                Config.EspShowCrosshair = crosshair;
                Config.MarkDirty();
            }

            if (Config.EspShowCrosshair)
            {
                ImGui.Indent(16);

                int cType = Config.EspCrosshairType;
                if (UIControls.ComboRow("Style", ref cType, _espCrosshairTypes,
                    "Crosshair shape drawn at screen center"))
                {
                    Config.EspCrosshairType = cType;
                    Config.MarkDirty();
                }

                float cScale = Config.EspCrosshairScale;
                if (UIControls.StepperFloat("Scale", ref cScale, 0.5f, 5f, 0.1f, "{0:0.0}x",
                    "Crosshair size multiplier"))
                {
                    Config.EspCrosshairScale = cScale;
                    Config.MarkDirty();
                }

                ImGui.Unindent(16);
            }

            UIControls.Section("HUD");

            bool showFps = Config.EspShowFps;
            if (UIControls.ToggleRow("Show FPS", ref showFps))
            {
                Config.EspShowFps = showFps;
                Config.MarkDirty();
            }

            bool showStatus = Config.EspShowStatusText;
            if (UIControls.ToggleRow("Show Status Text", ref showStatus, "Banner listing active memory-write features (LEAN, 3P, NV, THERMAL, ...)"))
            {
                Config.EspShowStatusText = showStatus;
                Config.MarkDirty();
            }

            bool showEnergyHydration = Config.EspShowEnergyHydration;
            if (UIControls.ToggleRow("Show Energy / Hydration", ref showEnergyHydration, "Bottom-right bars showing local player energy + hydration"))
            {
                Config.EspShowEnergyHydration = showEnergyHydration;
                Config.MarkDirty();
            }

            UIControls.Section("Ballistics (debug)");

            var bcfg = Config.Ballistics ??= new BallisticsConfig();

            bool ballEnabled = bcfg.Enabled;
            if (UIControls.ToggleRow("Enable Ballistics", ref ballEnabled,
                "Master toggle for ballistics simulation + debug overlays."))
            {
                bcfg.Enabled = ballEnabled;
                Config.MarkDirty();
            }

            if (bcfg.Enabled)
            {
                ImGui.Indent(16);

                bool drawPredicted = bcfg.DrawPredictedTrajectory;
                if (UIControls.ToggleRow("Predicted Arc (red)", ref drawPredicted,
                    "Simulated trajectory from your muzzle to the predicted impact point."))
                {
                    bcfg.DrawPredictedTrajectory = drawPredicted;
                    Config.MarkDirty();
                }

                bool drawLive = bcfg.DrawLiveShots;
                if (UIControls.ToggleRow("Live Tracers (green)", ref drawLive,
                    "Real-time bullet trails read from the game's BallisticsCalculator.Shots list."))
                {
                    bcfg.DrawLiveShots = drawLive;
                    Config.MarkDirty();
                }

                bool showHud = bcfg.ShowDebugHud;
                if (UIControls.ToggleRow("Debug HUD Window", ref showHud,
                    "Floating window with ammo / muzzle velocity / drop table / G1 source."))
                {
                    bcfg.ShowDebugHud = showHud;
                    BallisticsDebugWidget.IsOpen = showHud;
                    Config.MarkDirty();
                }

                bool liveG1 = bcfg.UseGameG1Table;
                if (UIControls.ToggleRow("Use Live G1 Table", ref liveG1,
                    "Replace the hardcoded G1 table with the game's own once a bullet is observed."))
                {
                    bcfg.UseGameG1Table = liveG1;
                    if (!liveG1) G1Table.Reset();
                    Config.MarkDirty();
                }

                float lineWidth = bcfg.LineWidth;
                if (UIControls.StepperFloat("Line Width", ref lineWidth, 0.5f, 6f, 0.25f, "{0:0.0}px",
                    "Stroke width for predicted + live shot lines."))
                {
                    bcfg.LineWidth = lineWidth;
                    Config.MarkDirty();
                }

                int samples = bcfg.PredictedSamples;
                if (UIControls.Stepper("Predicted Samples", ref samples, 8, 512, 8,
                    tooltip: "Number of points sampled along the predicted arc."))
                {
                    bcfg.PredictedSamples = samples;
                    Config.MarkDirty();
                }

                float maxDist = bcfg.PredictedMaxDistance;
                if (UIControls.StepperFloat("Predicted Max Distance", ref maxDist, 25f, 2000f, 25f, "{0:0}m",
                    "Stop the predicted arc after this many meters from the muzzle."))
                {
                    bcfg.PredictedMaxDistance = maxDist;
                    Config.MarkDirty();
                }

                float lifetime = bcfg.LiveShotLifetime;
                if (UIControls.StepperFloat("Live Shot Lifetime", ref lifetime, 0.5f, 15f, 0.5f, "{0:0.0}s",
                    "How long bullet trails stay visible after the bullet stops moving."))
                {
                    bcfg.LiveShotLifetime = lifetime;
                    BallisticsFeature.Instance.Tracker.Lifetime = TimeSpan.FromSeconds(lifetime);
                    Config.MarkDirty();
                }

                ImGui.Unindent(16);
            }
        }
    }
}

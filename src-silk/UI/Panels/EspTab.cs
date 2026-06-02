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
        private static readonly string[] _espRenderModes   = ["None", "Bones", "Box", "Head Dot", "Skeleton + Head Dot"];
        private static readonly string[] _lootedModes      = ["Show", "Dim", "Hide"];
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
            if (UIControls.ToggleRow("ESP Window Open", ref open,
                "Open or close the ESP overlay window."))
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

            float textScale = Config.EspTextScale;
            if (UIControls.StepperFloat("Text Scale", ref textScale, 0.5f, 3.0f, 0.1f, "{0:0.0}x",
                "Global multiplier for all ESP label font sizes.\n1.0 = default (10–12 px). Increase for chroma-keyed overlays or high-DPI monitors."))
            {
                Config.EspTextScale = textScale;
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

            // ── Players ────────────────────────────────────────────────────────────

            UIControls.Section("Players");

            bool showPlayers = Config.EspShowPlayers;
            if (UIControls.ToggleRow("Show Players", ref showPlayers,
                "Draw boxes, bones, or head dots for all players visible to the ESP camera."))
            {
                Config.EspShowPlayers = showPlayers;
                Config.MarkDirty();
            }

            int mode = Config.EspRenderMode;
            if (UIControls.ComboRow("Render Mode", ref mode, _espRenderModes,
                "How each player is drawn. Also cyclable via hotkey.\n  None — no marker\n  Bones — skeleton lines\n  Box — bounding box\n  Head Dot — single circle at head\n  Skeleton + Head Dot — both together"))
            {
                Config.EspRenderMode = mode;
                Config.MarkDirty();
            }

            if (mode == 2) // Box
            {
                bool bones = Config.EspShowBones;
                if (UIControls.ToggleRow("Show Bones Inside Box", ref bones,
                    "Also draw the skeleton inside the bounding box when Box mode is active."))
                {
                    Config.EspShowBones = bones;
                    Config.MarkDirty();
                }
            }

            float pDist = Config.EspPlayerDistance;
            if (UIControls.StepperFloat("Max Distance", ref pDist, 10f, 2000f, 10f, "{0:0}m",
                "Players beyond this distance are not drawn on the ESP."))
            {
                Config.EspPlayerDistance = pDist;
                Config.MarkDirty();
            }

            // ── Corpses ────────────────────────────────────────────────────────────

            UIControls.Section("Corpses");

            bool showCorpses = Config.EspShowCorpses;
            if (UIControls.ToggleRow("Show Corpse Boxes", ref showCorpses,
                "Draw bounding boxes around dead players. Box color is set in ESP Visuals → Loot."))
            {
                Config.EspShowCorpses = showCorpses;
                Config.MarkDirty();
            }
            if (showCorpses)
            {
                float cDist = Config.EspCorpseDistance;
                if (UIControls.StepperFloat("Corpse Dist.", ref cDist, 10f, 500f, 10f, "{0:0}m",
                    "Corpses beyond this distance are hidden."))
                {
                    Config.EspCorpseDistance = cDist;
                    Config.MarkDirty();
                }

                int lootedMode = Config.EspLootedCorpseMode;
                if (UIControls.ComboRow("Looted Corpses", ref lootedMode, _lootedModes,
                    "How to display corpses whose gear has been fully looted.\n  Show — always visible\n  Dim — draw at reduced opacity\n  Hide — remove from overlay"))
                {
                    Config.EspLootedCorpseMode = lootedMode;
                    Config.MarkDirty();
                }
            }

            // ── Backpacks ──────────────────────────────────────────────────────────

            UIControls.Section("Backpacks");

            bool showBackpacks = Config.EspShowBackpacks;
            if (UIControls.ToggleRow("Show Backpack Boxes", ref showBackpacks,
                "Draw bounding boxes around dropped backpacks. Box color is set in ESP Visuals → Loot."))
            {
                Config.EspShowBackpacks = showBackpacks;
                Config.MarkDirty();
            }
            if (showBackpacks)
            {
                float bDist = Config.EspBackpackDistance;
                if (UIControls.StepperFloat("Backpack Dist.", ref bDist, 10f, 500f, 10f, "{0:0}m",
                    "Dropped backpacks beyond this distance are hidden."))
                {
                    Config.EspBackpackDistance = bDist;
                    Config.MarkDirty();
                }
            }

            // ── Containers ─────────────────────────────────────────────────────────

            UIControls.Section("Containers");

            bool showContainers = Config.EspShowContainers;
            if (UIControls.ToggleRow("Show Containers", ref showContainers,
                "Draw a dot and label at each static loot container (duffle bags, crates, toolboxes)."))
            {
                Config.EspShowContainers = showContainers;
                Config.MarkDirty();
            }
            if (showContainers)
            {
                float cDist = Config.EspContainerDistance;
                if (UIControls.StepperFloat("Container Dist.", ref cDist, 10f, 500f, 10f, "{0:0}m",
                    "Containers beyond this distance are hidden."))
                {
                    Config.EspContainerDistance = cDist;
                    Config.MarkDirty();
                }

                int searchedMode = Config.EspSearchedContainerMode;
                if (UIControls.ComboRow("Searched Containers", ref searchedMode, _lootedModes,
                    "How to display containers that have already been opened.\n  Show — always visible\n  Dim — draw at reduced opacity\n  Hide — remove from overlay"))
                {
                    Config.EspSearchedContainerMode = searchedMode;
                    Config.MarkDirty();
                }
            }

            // ── Loot ───────────────────────────────────────────────────────────────

            UIControls.Section("Loot");

            bool showLoot = Config.EspShowLoot;
            if (UIControls.ToggleRow("Show Loot", ref showLoot,
                "Draw value labels on nearby loot items that pass the price filter.\nPrice thresholds are set in Loot Filters."))
            {
                Config.EspShowLoot = showLoot;
                Config.MarkDirty();
            }

            float lDist = Config.EspLootDistance;
            if (UIControls.StepperFloat("Loot Dist.", ref lDist, 10f, 500f, 5f, "{0:0}m",
                "Loot items beyond this distance are hidden."))
            {
                Config.EspLootDistance = lDist;
                Config.MarkDirty();
            }

            bool wishlistOnly = Config.EspLootWishlistOnly;
            if (UIControls.ToggleRow("Wishlist Items Only", ref wishlistOnly,
                "Show only wishlisted items on the ESP, ignoring the price/visibility filter.\nUseful for targeted loot runs."))
            {
                Config.EspLootWishlistOnly = wishlistOnly;
                Config.MarkDirty();
            }

            // ── Extracts ───────────────────────────────────────────────────────────

            UIControls.Section("Extracts");

            bool showExfils = Config.EspShowExfils;
            if (UIControls.ToggleRow("Show Extracts", ref showExfils,
                "Draw eligible extract points on the ESP overlay.\nColor indicates status: green = open, yellow = pending, red = closed."))
            {
                Config.EspShowExfils = showExfils;
                Config.MarkDirty();
            }

            // ── Crosshair ──────────────────────────────────────────────────────────

            UIControls.Section("Crosshair");

            bool crosshair = Config.EspShowCrosshair;
            if (UIControls.ToggleRow("Show Crosshair", ref crosshair,
                "Draw a crosshair indicator at the center of the ESP window."))
            {
                Config.EspShowCrosshair = crosshair;
                Config.MarkDirty();
            }

            if (Config.EspShowCrosshair)
            {
                ImGui.Indent(16);

                int cType = Config.EspCrosshairType;
                if (UIControls.ComboRow("Style", ref cType, _espCrosshairTypes,
                    "Crosshair shape drawn at screen center."))
                {
                    Config.EspCrosshairType = cType;
                    Config.MarkDirty();
                }

                float cScale = Config.EspCrosshairScale;
                if (UIControls.StepperFloat("Scale", ref cScale, 0.5f, 5f, 0.1f, "{0:0.0}x",
                    "Crosshair size multiplier (1.0 = default size)."))
                {
                    Config.EspCrosshairScale = cScale;
                    Config.MarkDirty();
                }

                ImGui.Unindent(16);
            }

            // ── HUD ────────────────────────────────────────────────────────────────

            UIControls.Section("HUD");

            bool showFps = Config.EspShowFps;
            if (UIControls.ToggleRow("Show FPS", ref showFps,
                "Show the current ESP render rate in the top-left corner of the overlay."))
            {
                Config.EspShowFps = showFps;
                Config.MarkDirty();
            }

            bool showStatus = Config.EspShowStatusText;
            if (UIControls.ToggleRow("Show Status Text", ref showStatus,
                "Banner listing active memory-write features (LEAN, 3P, NV, THERMAL, ...)."))
            {
                Config.EspShowStatusText = showStatus;
                Config.MarkDirty();
            }

            bool showEnergyHydration = Config.EspShowEnergyHydration;
            if (UIControls.ToggleRow("Show Energy / Hydration", ref showEnergyHydration,
                "Bottom-right bars showing local player energy + hydration levels."))
            {
                Config.EspShowEnergyHydration = showEnergyHydration;
                Config.MarkDirty();
            }

            bool textOutline = Config.EspTextOutline;
            if (UIControls.ToggleRow("Text Outline", ref textOutline,
                "Draw a black stroke outline around all text labels instead of a drop shadow.\nGreatly improves legibility on chroma-keyed and transparent overlays."))
            {
                Config.EspTextOutline = textOutline;
                Config.MarkDirty();
            }

            if (Config.EspTextOutline)
            {
                ImGui.Indent(16);
                float outlineW = Config.EspTextOutlineWidth;
                if (UIControls.StepperFloat("Outline Width", ref outlineW, 0.5f, 6f, 0.25f, "{0:0.0}px",
                    "Stroke width of the text outline halo in pixels. 2–3 px is typical."))
                {
                    Config.EspTextOutlineWidth = outlineW;
                    Config.MarkDirty();
                }
                ImGui.Unindent(16);
            }

            // ── Ballistics ─────────────────────────────────────────────────────────

            UIControls.Section("Ballistics");

            var bcfg = Config.Ballistics ??= new BallisticsConfig();

            bool ballEnabled = bcfg.Enabled;
            if (UIControls.ToggleRow("Enable Ballistics", ref ballEnabled,
                "Master toggle for the ballistics system (trajectory arc, live tracers, impact markers)."))
            {
                bcfg.Enabled = ballEnabled;
                Config.MarkDirty();
            }

            if (bcfg.Enabled)
            {
                ImGui.Indent(16);

                bool drawPredicted = bcfg.DrawPredictedTrajectory;
                if (UIControls.ToggleRow("Predicted Arc", ref drawPredicted,
                    "Draw a simulated red arc from your muzzle to the predicted bullet impact point."))
                {
                    bcfg.DrawPredictedTrajectory = drawPredicted;
                    Config.MarkDirty();
                }

                bool autoRange = bcfg.AutoRange;
                if (UIControls.ToggleRow("Auto-Range to Target", ref autoRange,
                    "Automatically extend the arc to the nearest enemy in your crosshair (within 200 px).\nFalls back to Predicted Max Distance when no target is in range."))
                {
                    bcfg.AutoRange = autoRange;
                    Config.MarkDirty();
                }

                bool drawLive = bcfg.DrawLiveShots;
                if (UIControls.ToggleRow("Live Tracers", ref drawLive,
                    "Draw real-time bullet trails for all in-flight bullets read from the game.\nYour own trails are highlighted separately if 'Highlight My Trails' is on."))
                {
                    bcfg.DrawLiveShots = drawLive;
                    Config.MarkDirty();
                }

                bool highlightLocal = bcfg.HighlightLocalShotTrail;
                if (UIControls.ToggleRow("Highlight My Trails", ref highlightLocal,
                    "Draw your own bullet trails in orange/yellow so they stand out from other tracers.\nHit color is set in ESP Visuals → Ballistics."))
                {
                    bcfg.HighlightLocalShotTrail = highlightLocal;
                    Config.MarkDirty();
                }

                bool localOnly = bcfg.LocalShotTrailOnly;
                if (UIControls.ToggleRow("My Trails Only", ref localOnly,
                    "Hide all other players' bullet trails — show only your own."))
                {
                    bcfg.LocalShotTrailOnly = localOnly;
                    Config.MarkDirty();
                }

                bool impactDots = bcfg.DrawShotImpactMarkers;
                if (UIControls.ToggleRow("Impact Markers", ref impactDots,
                    "Show a fading dot where your bullets stop (enemy hit, wall, or floor).\nFades over 1.5 seconds."))
                {
                    bcfg.DrawShotImpactMarkers = impactDots;
                    Config.MarkDirty();
                }

                bool showHud = bcfg.ShowDebugHud;
                if (UIControls.ToggleRow("Debug HUD", ref showHud,
                    "Show a floating window with ammo name, muzzle velocity, G1 source, and drop table."))
                {
                    bcfg.ShowDebugHud = showHud;
                    BallisticsDebugWidget.IsOpen = showHud;
                    Config.MarkDirty();
                }

                if (UIControls.BeginAdvanced("Settings"))
                {
                    float lineWidth = bcfg.LineWidth;
                    if (UIControls.StepperFloat("Line Width", ref lineWidth, 0.5f, 6f, 0.25f, "{0:0.0}px",
                        "Stroke width in pixels for predicted arc and live tracer lines."))
                    {
                        bcfg.LineWidth = lineWidth;
                        Config.MarkDirty();
                    }

                    int samples = bcfg.PredictedSamples;
                    if (UIControls.Stepper("Arc Samples", ref samples, 8, 512, 8,
                        tooltip: "Number of points sampled along the predicted arc. Higher = smoother curve, more CPU."))
                    {
                        bcfg.PredictedSamples = samples;
                        Config.MarkDirty();
                    }

                    float maxDist = bcfg.PredictedMaxDistance;
                    if (UIControls.StepperFloat("Arc Max Dist.", ref maxDist, 25f, 2000f, 25f, "{0:0}m",
                        "Stop the predicted arc after this many meters. Used as fallback when Auto-Range is off or no target in range."))
                    {
                        bcfg.PredictedMaxDistance = maxDist;
                        Config.MarkDirty();
                    }

                    float lifetime = bcfg.LiveShotLifetime;
                    if (UIControls.StepperFloat("Trail Lifetime", ref lifetime, 0.5f, 15f, 0.5f, "{0:0.0}s",
                        "How long bullet trail history stays visible after the bullet stops moving."))
                    {
                        bcfg.LiveShotLifetime = lifetime;
                        BallisticsFeature.Instance.Tracker.Lifetime = TimeSpan.FromSeconds(lifetime);
                        Config.MarkDirty();
                    }

                    int maxShots = bcfg.MaxLiveShots;
                    if (UIControls.Stepper("Max Live Shots", ref maxShots, 1, 256, 8,
                        tooltip: "Hard cap on simultaneously-tracked in-flight bullets. Lower values reduce memory use on full servers."))
                    {
                        bcfg.MaxLiveShots = maxShots;
                        Config.MarkDirty();
                    }

                    bool liveG1 = bcfg.UseGameG1Table;
                    if (UIControls.ToggleRow("Use Live G1 Table", ref liveG1,
                        "Capture the game's own G1 drag table from the first bullet observed in raid.\nProvides the most accurate drop simulation. Disable to use the built-in fallback table."))
                    {
                        bcfg.UseGameG1Table = liveG1;
                        if (!liveG1) G1Table.Reset();
                        Config.MarkDirty();
                    }

                    UIControls.EndAdvanced();
                }

                ImGui.Unindent(16);
            }
        }
    }
}

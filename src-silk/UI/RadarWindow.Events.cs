// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.UI.Panels;
using eft_dma_radar.Silk.UI.Widgets;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace eft_dma_radar.Silk.UI
{
    internal static partial class RadarWindow
    {
        private static void OnResize(Vector2D<int> size)
        {
            _gl.Viewport(size);
            CreateSkiaSurface();
        }

        private static void OnClosing()
        {
            // Persist fullscreen state + windowed size/position
            Config.RadarFullscreen = _fakeFullscreen;
            if (_fakeFullscreen)
            {
                // Save pre-fullscreen coords so next launch can open windowed before going FS
                Config.WindowWidth  = _savedFsSize.X;
                Config.WindowHeight = _savedFsSize.Y;
                Config.RadarWindowX = _savedFsPos.X;
                Config.RadarWindowY = _savedFsPos.Y;
            }
            else
            {
                Config.WindowWidth  = _window.Size.X;
                Config.WindowHeight = _window.Size.Y;
                Config.RadarWindowX = _window.Position.X;
                Config.RadarWindowY = _window.Position.Y;
            }

            // Persist widget/panel visibility
            Config.ShowPlayersWidget = PlayerInfoWidget.IsOpen;
            Config.ShowLootWidget = LootWidget.IsOpen;
            Config.ShowAimviewWidget = AimviewWidget.IsOpen;
            Config.ShowSettingsOverlay = SettingsPanel.IsOpen;
            Config.ShowLootFiltersPanel = LootFiltersPanel.IsOpen;
            Config.ShowHotkeyPanel = HotkeyManagerPanel.IsOpen;
            Config.ShowHideoutPanel = HideoutPanel.IsOpen;
            Config.ShowQuestPanel = QuestPanel.IsOpen;
            Config.ShowQuestPlannerPanel = QuestPlannerPanel.IsOpen;
            Config.ShowPlayerHistoryPanel = PlayerHistoryPanel.IsOpen;
            Config.ShowPlayerWatchlistPanel = PlayerWatchlistPanel.IsOpen;
            Config.ShowEspWidget = EspWindow.IsOpen;

            Config.Save();

            // Close ESP window if open
            EspWindow.Close();

            // Signal the memory worker to stop cleanly before we release GPU resources
            Memory.Close();

            // Dispose GPU/UI resources
            _fpsTimer.Dispose();
            _imgui?.Dispose();
            if (_imguiFontHandle.IsAllocated)
                _imguiFontHandle.Free();
            if (_iconGlyphRangesHandle.IsAllocated)
                _iconGlyphRangesHandle.Free();
            if (_cyrillicGlyphRangesHandle.IsAllocated)
                _cyrillicGlyphRangesHandle.Free();
            _skSurface?.Dispose();
            _skBackendRenderTarget?.Dispose();
            _grContext?.Dispose();
            _input?.Dispose();

            Log.WriteLine("[RadarWindow] Closed.");
        }

        private static async Task RunFpsTimerAsync()
        {
            try
            {
                while (await _fpsTimer.WaitForNextTickAsync())
                {
                    _fps = Interlocked.Exchange(ref _fpsCounter, 0);
                }
            }
            catch (ObjectDisposedException) { }
        }
    }
}

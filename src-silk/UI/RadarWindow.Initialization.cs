// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using eft_dma_radar.Silk.UI.Panels;
using eft_dma_radar.Silk.UI.Widgets;
using ImGuiNET;
using SilkWindow = Silk.NET.Windowing.Window;

namespace eft_dma_radar.Silk.UI
{
    internal static partial class RadarWindow
    {
        internal static void Initialize()
        {
            Log.WriteLine("[RadarWindow] Initialize starting...");

            var options = WindowOptions.Default;
            options.Title        = SilkProgram.Name;
            options.VSync        = false;
            options.FramesPerSecond          = Config.TargetFps;
            options.PreferredStencilBufferBits = 8;
            options.PreferredBitDepth        = new Vector4D<int>(8, 8, 8, 8);
            options.WindowBorder             = WindowBorder.Resizable; // explicit — ensures title bar

            // Position on the configured monitor. Do NOT set WindowState in options —
            // GLFW ignores Position when Maximized/Fullscreen is set in options.
            var radarMon = MonitorInfo.GetMonitor(Config.RadarTargetScreen);
            // Offset 40px from monitor top so the title bar (~30px) is within the monitor.
            // GLFW positions by client-area top-left; without this offset the title bar
            // ends up at y = (radarMon.Top - 30) which is above the visible screen.
            const int TitleBarOffset = 40;
            options.Position = new Vector2D<int>(radarMon.Left, radarMon.Top + TitleBarOffset);
            options.Size = new Vector2D<int>(
                Math.Min(Config.WindowWidth,  radarMon.Width),
                Math.Min(Config.WindowHeight, radarMon.Height - TitleBarOffset));

            Log.WriteLine($"[RadarWindow] Creating window on monitor {Config.RadarTargetScreen} ({radarMon.Width}x{radarMon.Height} @ {radarMon.Left},{radarMon.Top})");

            _window = SilkWindow.Create(options);
            _window.Load += OnLoad;

            Log.WriteLine("[RadarWindow] Initialize complete, window created.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Run()
        {
            Log.WriteLine("[RadarWindow] Run() starting...");
            _window.Run();
            Log.WriteLine("[RadarWindow] Run() returned.");
        }

        private static void OnLoad()
        {
            try
            {
                Log.WriteLine("[RadarWindow] OnLoad starting...");

                _gl = GL.GetApi(_window);
                Log.WriteLine($"[RadarWindow] OpenGL: {_gl.GetStringS(StringName.Version)}");

                // Set PushPin icon after the window is fully loaded (GLFW requires this)
                ApplyWindowIcon(_window);

                // Create input context FIRST (before ImGuiController)
                _input = _window.CreateInput();

                // --- Skia GPU context ---
                var glInterface = GRGlInterface.Create(name =>
                    _window.GLContext!.TryGetProcAddress(name, out var addr) ? addr : 0);

                if (glInterface is null || !glInterface.Validate())
                {
                    Log.WriteLine("[RadarWindow] ERROR: GRGlInterface creation/validation failed!");
                    _window.Close();
                    return;
                }

                _grContext = GRContext.CreateGl(glInterface);
                if (_grContext is null)
                {
                    Log.WriteLine("[RadarWindow] ERROR: GRContext.CreateGl returned null!");
                    _window.Close();
                    return;
                }
                _grContext.SetResourceCacheLimit(512 * 1024 * 1024); // 512 MB

                // Set clear color once — never changes
                _gl.ClearColor(0f, 0f, 0f, 1f);

                CreateSkiaSurface();
                if (_skSurface is null)
                {
                    Log.WriteLine("[RadarWindow] ERROR: SKSurface creation failed!");
                    _window.Close();
                    return;
                }

                Log.WriteLine("[RadarWindow] SkiaSharp GPU context ready.");

                // ImGui controller
                _imgui = new ImGuiController(
                    gl: _gl,
                    view: _window,
                    input: _input,
                    onConfigureIO: () =>
                    {
                        var io = ImGui.GetIO();
                        // Keyboard navigation for remote-desktop / AnyDesk users.
                        // The focus cursor is also styled via ImGuiCol.NavCursor in ApplyImGuiDarkStyle().
                        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
                        LoadImGuiFont(io);
                    }
                );

                ApplyImGuiDarkStyle();
                ApplyImGuiFontScale();

                // Wire up events
                foreach (var mouse in _input.Mice)
                {
                    mouse.MouseDown += OnMouseDown;
                    mouse.MouseUp += OnMouseUp;
                    mouse.MouseMove += OnMouseMove;
                    mouse.Scroll += OnMouseScroll;
                }

                foreach (var keyboard in _input.Keyboards)
                {
                    keyboard.KeyDown += OnKeyDown;
                }

                _window.Render += OnRender;
                _window.Resize += OnResize;
                _window.Closing += OnClosing;

                // Start FPS timer
                _ = RunFpsTimerAsync();

                // Restore widget/panel visibility from config
                PlayerInfoWidget.IsOpen = Config.ShowPlayersWidget;
                LootWidget.IsOpen = Config.ShowLootWidget;
                AimviewWidget.IsOpen = Config.ShowAimviewWidget;
                SettingsPanel.IsOpen = Config.ShowSettingsOverlay;
                LootFiltersPanel.IsOpen = Config.ShowLootFiltersPanel;
                HotkeyManagerPanel.IsOpen = Config.ShowHotkeyPanel;
                HideoutPanel.IsOpen = Config.ShowHideoutPanel;
                QuestPanel.IsOpen = Config.ShowQuestPanel;
                QuestPlannerPanel.IsOpen = Config.ShowQuestPlannerPanel;
                PlayerHistoryPanel.IsOpen = Config.ShowPlayerHistoryPanel;
                PlayerWatchlistPanel.IsOpen = Config.ShowPlayerWatchlistPanel;

                EspWindow.Open(); // always auto-start — single-launch experience

                // Auto-open the hideout panel
                Memory.HideoutEntered += static (_, _) => HideoutPanel.IsOpen = true;

                // Wire up the notification callback into the silk Memory module
                Memory.ShowNotification ??= static (msg, level) =>
                    Log.WriteLine($"[Notification:{level}] {msg}");

                Log.WriteLine("[RadarWindow] OnLoad complete.");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"***** [RadarWindow] OnLoad FATAL: {ex}");
                try { _window.Close(); } catch { }
            }
        }

        private static void CreateSkiaSurface()
        {
            _skSurface?.Dispose();
            _skBackendRenderTarget?.Dispose();

            var size = _window.FramebufferSize;
            if (size.X <= 0 || size.Y <= 0 || _grContext is null)
            {
                _skSurface = null!;
                _skBackendRenderTarget = null!;
                return;
            }

            _gl.GetInteger(GetPName.SampleBuffers, out int sampleBuffers);
            _gl.GetInteger(GetPName.Samples, out int samples);
            if (sampleBuffers == 0)
                samples = 0;

            int stencilBits = 0;
            try
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                _gl.GetFramebufferAttachmentParameter(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.StencilAttachment,
                    FramebufferAttachmentParameterName.StencilSize,
                    out stencilBits);
            }
            catch
            {
                stencilBits = 8; // Assume 8-bit stencil if query fails
            }

            var fbInfo = new GRGlFramebufferInfo(0, (uint)InternalFormat.Rgba8);

            _skBackendRenderTarget = new GRBackendRenderTarget(
                size.X, size.Y, samples, stencilBits, fbInfo);

            _skSurface = SKSurface.Create(
                _grContext,
                _skBackendRenderTarget,
                GRSurfaceOrigin.BottomLeft,
                SKColorType.Rgba8888);

            if (_skSurface is null)
            {
                Log.WriteLine($"[RadarWindow] SKSurface.Create returned null! Size={size.X}x{size.Y}, Samples={samples}, Stencil={stencilBits}");
            }
        }

        /// <summary>
        /// Loads the PushPin .ico and applies it to the given window's taskbar + title-bar icon.
        /// Safe to call before OnLoad (icon is set at the OS level via GLFW).
        /// </summary>
        internal static void ApplyWindowIcon(IWindow window)
        {
            const string IconPath = @"C:\DMA\PushPin\source\push-pin.ico";
            if (!File.Exists(IconPath)) return;
            try
            {
                using var bmp = SKBitmap.Decode(IconPath)?.Copy(SKColorType.Rgba8888);
                if (bmp is null) return;
                var pixels = new Memory<byte>(bmp.Bytes);
                window.SetWindowIcon(new ReadOnlySpan<RawImage>(
                    new[] { new RawImage(bmp.Width, bmp.Height, pixels) }));
                Log.WriteLine($"[RadarWindow] Icon set ({bmp.Width}x{bmp.Height}).");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[RadarWindow] Icon load failed: {ex.Message}");
            }
        }
    }
}

// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Runtime.InteropServices;
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
            // RadarTargetScreen is the authoritative source for which monitor this window belongs to.
            // Saved X/Y are used for exact position restore only if they fall within that monitor's
            // bounds — stale coords outside the monitor are ignored and the window is centred instead.
            var radarMon = MonitorInfo.GetMonitor(Config.RadarTargetScreen);
            const int TitleBarOffset = 40;
            int winW  = Math.Min(Config.WindowWidth,  radarMon.Width);
            int winH  = Math.Min(Config.WindowHeight, radarMon.Height - TitleBarOffset);
            int centX = radarMon.Left + (radarMon.Width  - winW) / 2;
            int centY = radarMon.Top  + Math.Max(TitleBarOffset, (radarMon.Height - winH) / 2);
            int posX  = centX, posY = centY;
            if (Config.RadarWindowX >= radarMon.Left &&
                Config.RadarWindowX <  radarMon.Left + radarMon.Width &&
                Config.RadarWindowY >= radarMon.Top  &&
                Config.RadarWindowY <  radarMon.Top  + radarMon.Height)
            {
                posX = Config.RadarWindowX;
                posY = Config.RadarWindowY;
            }
            options.Position = new Vector2D<int>(posX, posY);
            options.Size     = new Vector2D<int>(winW, winH);

            Log.WriteLine($"[RadarWindow] Creating window on monitor {Config.RadarTargetScreen} ({radarMon.Width}x{radarMon.Height} @ {radarMon.Left},{radarMon.Top})");

            _window = SilkWindow.Create(options);
            _window.Load += OnLoad;
            Memory.GameStarted += (_, _) => _window.Title = SilkProgram.Name;
            Memory.GameStopped += (_, _) => _window.Title = "EFT (Silk.NET)";

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

                // Set window icon after fully loaded (GLFW requires this)
                ApplyWindowIcon(_window, IconRadar);

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
                        io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange; // keep OS cursor visible; we manage it
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

                // Clear any CursorMode.Hidden GLFW stored from a previous ImGui frame.
                foreach (var mouse in _input.Mice)
                    mouse.Cursor.CursorMode = CursorMode.Normal;
                ForceShowCursor(); // counteract any OBS game-capture ShowCursor(false) at startup

                // Re-assert Normal on focus gain — GLFW re-applies its stored cursor mode
                // when the window regains focus, so this fires before the next render frame.
                // Also counteracts OBS game-capture hiding the cursor when focus returns.
                _window.FocusChanged += isFocused =>
                {
                    if (!isFocused) return;
                    foreach (var mouse in _input.Mice)
                        mouse.Cursor.CursorMode = CursorMode.Normal;
                    ForceShowCursor();
                };

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

                // Defer fullscreen restore to first render frame so _window.Position is valid
                _needRestoreFullscreen = Config.RadarFullscreen;

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

        // ── Per-window icon paths ─────────────────────────────────────────────
        private const string IconRadar   = @"C:\DMA\eft-dma-radar-silk\src-silk\assets\icons\icon-radar.png";
        internal const string IconEsp    = @"C:\DMA\eft-dma-radar-silk\src-silk\assets\icons\icon-esp.png";
        internal const string IconStartup = @"C:\DMA\eft-dma-radar-silk\src-silk\assets\icons\icon-startup.png";

        /// <summary>
        /// Decodes an image file (PNG, ICO, etc.) and applies it as the window's taskbar + title-bar icon.
        /// SkiaSharp handles any format; GLFW receives raw RGBA pixels.
        /// </summary>
        internal static void ApplyWindowIcon(IWindow window, string iconPath)
        {
            if (!File.Exists(iconPath)) return;
            try
            {
                using var bmp = SKBitmap.Decode(iconPath)?.Copy(SKColorType.Rgba8888);
                if (bmp is null) return;
                var pixels = new Memory<byte>(bmp.Bytes);
                window.SetWindowIcon(new ReadOnlySpan<RawImage>(
                    new[] { new RawImage(bmp.Width, bmp.Height, pixels) }));
                Log.WriteLine($"[RadarWindow] Icon set ({bmp.Width}x{bmp.Height}) from {Path.GetFileName(iconPath)}.");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[RadarWindow] Icon load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Increments the Windows cursor display counter until the cursor is visible (counter ≥ 0).
        /// OBS game-capture decrements the counter via ShowCursor(false); this counteracts it.
        /// </summary>
        private static void ForceShowCursor()
        {
            while (ShowCursorNative(true) < 0) { }
        }

        [DllImport("user32.dll", EntryPoint = "ShowCursor")]
        private static extern int ShowCursorNative(bool bShow);
    }
}

// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Features.Ballistics;
using eft_dma_radar.Silk.Tarkov.GameWorld.Ballistics;

namespace eft_dma_radar.Silk.UI.ESP
{
    /// <summary>
    /// Skia ESP overlay for the ballistics debug view. Draws:
    ///   <list type="bullet">
    ///     <item>Predicted shot polyline (red) sampled from <see cref="BallisticsFeature.LocalTrajectory"/>.</item>
    ///     <item>Live in-flight bullet trails (green) from <see cref="LiveShotTracker.GetSnapshot"/>.</item>
    ///   </list>
    /// All world points are projected via <see cref="CameraManager.WorldToScreen"/> in viewport space —
    /// the caller is expected to have already applied the viewport→window scale.
    /// </summary>
    internal static class BallisticsRenderer
    {
        // Mutable paints for per-frame color/alpha overrides — only touched on the render thread.
        private static readonly SKPaint _trailDyn = new()
        {
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };
        private static readonly SKPaint _impactDyn = new()
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };

        public static void Draw(SKCanvas canvas)
        {
            var cfg = SilkProgram.Config?.Ballistics;
            if (cfg is null || !cfg.Enabled) return;
            if (!CameraManager.IsActive) return;

            var feature = BallisticsFeature.Instance;

            // Configure paint widths from config (cheap — Skia caches the dirty flag itself).
            EspPaints.PredictedTrajectory.StrokeWidth = cfg.LineWidth;
            EspPaints.LiveShotTrail.StrokeWidth = cfg.LineWidth;
            EspPaints.LocalShotTrail.StrokeWidth = cfg.LineWidth;

            if (cfg.DrawPredictedTrajectory)
                DrawPredictedTrajectory(canvas, feature.LocalTrajectory);

            ulong localBase = Memory.Game?.LocalPlayer?.Base ?? 0UL;

            if (cfg.DrawLiveShots || cfg.HighlightLocalShotTrail || cfg.LocalShotTrailOnly)
                DrawLiveShots(canvas, feature.Tracker, localBase, cfg);

            if (cfg.DrawShotImpactMarkers)
                DrawImpactMarkers(canvas, feature.Tracker);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKColor ToSKColor(uint argb) =>
            new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

        private static void DrawPredictedTrajectory(SKCanvas canvas, Vector3[] points)
        {
            if (points is null || points.Length < 2) return;

            float lineW  = SilkProgram.Config?.Ballistics?.LineWidth ?? 2f;
            float dotR   = Math.Max(3f, lineW * 2.5f);   // scales with line width
            float rimR   = dotR + 1.5f;

            // ── Find the true trajectory endpoint (last finite point in buffer) ──────
            var  impactWorld   = Vector3.Zero;
            int  impactWldIdx  = -1;
            for (int i = points.Length - 1; i >= 0; i--)
            {
                if (!IsFinite(points[i])) continue;
                impactWorld  = points[i];
                impactWldIdx = i;
                break;
            }
            if (impactWldIdx < 0) return;

            // Project impact with viewport tolerance so shallow off-screen arcs still get a dot.
            bool impactVisible = CameraManager.WorldToScreen(ref impactWorld, out var impactScr,
                onScreenCheck: true, useTolerance: true);

            // ── Build arc path (sprint-safe: skip >300 px jumps mid-arc) ────────────
            using var path = new SKPath();
            Vector2 lastScr = default;
            bool started = false;

            for (int i = 0; i < points.Length; i++)
            {
                var p = points[i];
                if (!IsFinite(p)) continue;
                if (!CameraManager.WorldToScreen(ref p, out var scr, onScreenCheck: true)) continue;

                if (!started)
                {
                    path.MoveTo(scr.X, scr.Y);
                    started = true;
                }
                else if (Vector2.DistanceSquared(lastScr, scr) > 300f * 300f)
                {
                    // Mid-arc sprint jump — restart segment to suppress spike.
                    path.MoveTo(scr.X, scr.Y);
                }
                else
                {
                    path.LineTo(scr.X, scr.Y);
                }
                lastScr = scr;
            }

            if (!started) return;

            // Close arc to actual impact dot — prevents gap when jump guard fired on last segment.
            if (impactVisible && Vector2.DistanceSquared(lastScr, impactScr) < 600f * 600f)
                path.LineTo(impactScr.X, impactScr.Y);

            canvas.DrawPath(path, EspPaints.PredictedTrajectory);

            // ── Impact dot at true trajectory endpoint ───────────────────────────────
            if (impactVisible)
            {
                canvas.DrawCircle(impactScr.X, impactScr.Y, rimR,  EspPaints.ImpactDotOutline);
                canvas.DrawCircle(impactScr.X, impactScr.Y, dotR,  EspPaints.ImpactDot);
                float dist = Vector3.Distance(points[0], impactWorld);
                canvas.DrawText($"~{dist:0}m", impactScr.X + rimR + 3f, impactScr.Y + 4f,
                    EspPaints.FontInfo, EspPaints.ImpactText);
            }

            // ── Muzzle dot ───────────────────────────────────────────────────────────
            var origin = points[0];
            if (IsFinite(origin) && CameraManager.WorldToScreen(ref origin, out var muzzle, onScreenCheck: true))
                canvas.DrawCircle(muzzle.X, muzzle.Y, lineW * 1.75f, EspPaints.MuzzleDot);
        }

        private struct LiveShotRenderState(SKCanvas canvas, SKPath path, ulong localBase, BallisticsConfig cfg)
        {
            public SKCanvas Canvas = canvas;
            public SKPath Path = path;
            public ulong LocalBase = localBase;
            public BallisticsConfig Config = cfg;
        }

        private static readonly Action<LiveShot, LiveShotRenderState> DrawSingleLiveShot = (shot, state) =>
        {
            bool isLocal = state.LocalBase != 0 && shot.OwnerPlayer == state.LocalBase;

            // Filtering: skip non-local shots when LocalShotTrailOnly is on,
            // or when live tracers are off but local highlighting is on.
            if (state.Config.LocalShotTrailOnly && !isLocal) return;
            if (!state.Config.DrawLiveShots && !isLocal) return;

            var trail = shot.Trail;
            if (trail.Count < 2) return;
            state.Path.Reset();

            bool started = false;
            for (int i = 0; i < trail.Count; i++)
            {
                var p = trail[i];
                if (!IsFinite(p)) continue;
                if (!CameraManager.WorldToScreen(ref p, out var scr, onScreenCheck: false)) continue;
                if (!started) { state.Path.MoveTo(scr.X, scr.Y); started = true; }
                else          { state.Path.LineTo(scr.X, scr.Y); }
            }
            if (!started) return;

            // Choose trail paint: hit color > local highlight color > default green.
            SKPaint trailPaint;
            bool hitConfirmed = isLocal && shot.HitTime > DateTime.MinValue;
            if (hitConfirmed && state.Config.LocalShotHitColor != 0)
            {
                _trailDyn.Color = ToSKColor(state.Config.LocalShotHitColor);
                _trailDyn.StrokeWidth = state.Config.LineWidth;
                trailPaint = _trailDyn;
            }
            else if (isLocal && state.Config.HighlightLocalShotTrail)
                trailPaint = EspPaints.LocalShotTrail;
            else
                trailPaint = EspPaints.LiveShotTrail;

            state.Canvas.DrawPath(state.Path, trailPaint);

            // Bullet head dot — omit when the shot has already hit (HitTime is set).
            if (!hitConfirmed)
            {
                var head = shot.CurrentPosition;
                var headPaint = isLocal && state.Config.HighlightLocalShotTrail
                    ? EspPaints.LocalShotHead
                    : EspPaints.LiveShotHead;
                if (IsFinite(head) && CameraManager.WorldToScreen(ref head, out var headScr))
                    state.Canvas.DrawCircle(headScr.X, headScr.Y, 2.5f, headPaint);
            }
        };

        private static void DrawLiveShots(SKCanvas canvas, LiveShotTracker tracker, ulong localBase, BallisticsConfig cfg)
        {
            using var path = new SKPath();
            var state = new LiveShotRenderState(canvas, path, localBase, cfg);
            tracker.DrawActiveShots(state, DrawSingleLiveShot);
        }

        private static readonly Action<LiveShotTracker.ImpactEvent, float, SKCanvas> DrawSingleImpact = (impact, age, canvas) =>
        {
            var pos = impact.Position;
            if (!CameraManager.WorldToScreen(ref pos, out var scr, onScreenCheck: true, useTolerance: true)) return;

            float alpha = Math.Clamp(1f - age / 1.5f, 0f, 1f);

            _impactDyn.Color = EspPaints.LocalImpactOutline.Color.WithAlpha((byte)(160 * alpha));
            canvas.DrawCircle(scr.X, scr.Y, 7f, _impactDyn);
            _impactDyn.Color = EspPaints.LocalImpactDot.Color.WithAlpha((byte)(240 * alpha));
            canvas.DrawCircle(scr.X, scr.Y, 5f, _impactDyn);
        };

        private static void DrawImpactMarkers(SKCanvas canvas, LiveShotTracker tracker)
        {
            tracker.DrawImpactMarkers(canvas, DrawSingleImpact);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}

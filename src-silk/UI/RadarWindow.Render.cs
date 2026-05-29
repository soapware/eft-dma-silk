// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov;
using eft_dma_radar.Silk.Tarkov.Unity.IL2CPP;
using EftPlayer = eft_dma_radar.Silk.Tarkov.GameWorld.Player.Player;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace eft_dma_radar.Silk.UI
{
    internal static partial class RadarWindow
    {
        private static void OnRender(double delta)
        {
            if (_grContext is null || _skSurface is null)
                return;

            if (_needRestoreFullscreen)
            {
                _needRestoreFullscreen = false;
                ToggleRadarFullscreen();
            }

            try
            {
                // Frame setup
                Interlocked.Increment(ref _fpsCounter);

                // Only reset GL state that ImGui touched — much cheaper than a full reset
                _grContext.ResetContext(
                    GRGlBackendState.RenderTarget |
                    GRGlBackendState.TextureBinding |
                    GRGlBackendState.View |
                    GRGlBackendState.Blend |
                    GRGlBackendState.Vertex |
                    GRGlBackendState.Program |
                    GRGlBackendState.PixelStore);

                // Periodic resource purge — scratch-only so permanent assets (fonts, map
                // tiles, gradients) are not evicted and immediately re-created. Full purge
                // every 1 s caused a visible GPU stall spike; 5 s scratch-only is safe.
                long now = Environment.TickCount64;
                if (now - _lastPurgeTick >= PurgeIntervalMs)
                {
                    _lastPurgeTick = now;
                    _grContext.PurgeUnlockedResources(scratchResourcesOnly: true);
                }

                // Skia scene render
                var fbSize = _window.FramebufferSize;
                DrawSkiaScene(ref fbSize);

                // ImGui UI render
                DrawImGuiUI(ref fbSize, delta);

                // Debounced config auto-save — persists any MarkDirty() call after the debounce interval
                Config.FlushIfDirty();
            }
            catch (Exception ex)
            {
                Log.WriteLine($"***** CRITICAL RENDER ERROR: {ex}");
            }
        }

        private static void DrawSkiaScene(ref Vector2D<int> fbSize)
        {
            _gl.Viewport(0, 0, (uint)fbSize.X, (uint)fbSize.Y);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.StencilBufferBit);

            var canvas = _skSurface.Canvas;
            canvas.Save();
            try
            {
                var scale = UIScale;
                canvas.Scale(scale, scale);

                if (InRaid)
                {
                    if (LocalPlayer is Player localPlayer)
                    {
                        var mapID = MapID;
                        if (!mapID.Equals(MapManager.Map?.ID, StringComparison.OrdinalIgnoreCase))
                            MapManager.LoadMap(mapID);

                        var map = MapManager.Map;
                        if (map is not null && localPlayer.HasValidPosition)
                        {
                            Memory.DiagnosticStatus = "Active";
                            DrawRadar(canvas, localPlayer, map, scale);
                        }
                        else if (MapManager.IsLoading)
                        {
                            Memory.DiagnosticStatus = "Parsing map geometry";
                            DrawStatusMessage(canvas, "Loading Map", scale, animated: true);
                        }
                        else
                        {
                            if (map is null)
                                Memory.DiagnosticStatus = $"Map '{mapID}' config not loaded";
                            else if (!localPlayer.HasValidPosition)
                                Memory.DiagnosticStatus = "Validating local player coordinates";

                            DrawStatusMessage(canvas, "Waiting for Raid Start", scale, animated: true);
                        }
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(Memory.DiagnosticStatus) ||
                            Memory.DiagnosticStatus == "Waiting for Raid Start" ||
                            Memory.DiagnosticStatus == "Raid Active")
                        {
                            Memory.DiagnosticStatus = "Discovering LocalPlayer structure";
                        }
                        DrawStatusMessage(canvas, "Waiting for Raid Start", scale, animated: true);
                    }
                }
                else if (Memory.InHideout)
                {
                    DrawStatusMessage(canvas, "In Hideout", scale);
                }
                else if (!Ready)
                {
                    var msg = Memory.WaitingForTarkov ? "Waiting for Tarkov" : "Starting Up";
                    DrawStatusMessage(canvas, msg, scale, animated: true);
                }
                else if (!InRaid)
                {
                    var matchingStage = MatchingProgressResolver.GetCachedStage();
                    string statusMsg;
                    if (matchingStage != EMatchingStage.None)
                    {
                        statusMsg = matchingStage.ToDisplayString();
                    }
                    else
                    {
                        statusMsg = "Waiting for Raid Start";
                    }
                    DrawStatusMessage(canvas, statusMsg, scale, animated: true);
                }

            }
            finally
            {
                canvas.Restore();
                _grContext.Flush();
            }
        }

        private static void DrawRadar(SKCanvas canvas, Player localPlayer, IRadarMap map, float scale)
        {
            LootFilter.AdvanceFrame();
            var localPlayerPos    = localPlayer.Position;
            var localPlayerMapPos = MapParams.ToMapPos(localPlayerPos, map.Config);

            var canvasSize = new SKSize(_window.Size.X / scale, _window.Size.Y / scale);
            MapParams mapParams;

            if (_freeMode)
            {
                if (_mapPanPosition == default)
                    _mapPanPosition = localPlayerMapPos;
                mapParams = map.GetParameters(canvasSize, _zoom, ref _mapPanPosition);
            }
            else
            {
                _mapPanPosition = default;
                mapParams = map.GetParameters(canvasSize, _zoom, ref localPlayerMapPos);
            }

            var mapCanvasBounds = new SKRect(0, 0, canvasSize.Width, canvasSize.Height);

            map.Draw(canvas, localPlayerPos.Y, mapParams.Bounds, mapCanvasBounds);

            // Viewport culling — world-space pre-cull avoids coordinate transforms for off-screen entities
            const float CullMargin = 120f;
            var worldBounds = mapParams.GetWorldBounds(CullMargin);
            var mapCfg = map.Config;

            // Snapshot players
            var allPlayersSnapshot = AllPlayers;

            List<Player>? normalPlayers = null;
            if (allPlayersSnapshot is not null)
            {
                _renderPlayers.Clear();
                foreach (var p in allPlayersSnapshot)
                {
                    if (p.IsRadarVisible)
                        _renderPlayers.Add(p);
                }
                _renderPlayers.Sort(static (a, b) => a.DrawPriority.CompareTo(b.DrawPriority));
                normalPlayers = _renderPlayers;
            }

            // Loot (skip in battle mode or if loot is disabled)
            if (!Config.BattleMode && Config.ShowLoot)
            {
                var loot = Memory.Loot;

                if (loot is not null)
                {
                    float playerY = localPlayerPos.Y;

                    int visibleCount = 0;
                    foreach (var item in loot)
                    {
                        if (!worldBounds.Contains(item.Position))
                            continue;
                        int price = item.DisplayPrice;
                        var result = item.Evaluate(price);
                        if (!result.Visible)
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(item.Position, mapCfg));
                        float dy = item.Position.Y - playerY;
                        bool underMap = dy < -15f;
                        item.Draw(canvas, sp, price, result, underMap, dy);
                        visibleCount++;
                    }
                    LootFilter.SetCounts(visibleCount, loot.Count);
                }
                else
                {
                    LootFilter.SetCounts(0, 0);
                }
            }
            else
            {
                LootFilter.SetCounts(0, 0);
            }

            // Corpses
            if (!Config.BattleMode && Config.ShowLoot && Config.ShowCorpses)
            {
                var corpses = Memory.Corpses;
                if (corpses is not null)
                {
                    foreach (var corpse in corpses)
                    {
                        if (!worldBounds.Contains(corpse.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(corpse.Position, mapCfg));
                        corpse.Draw(canvas, sp);
                    }
                }
            }

            // Static containers
            if (!Config.BattleMode && Config.ShowLoot && Config.ShowContainers)
            {
                var containers = Memory.Containers;
                if (containers is not null)
                {
                    float playerY = localPlayerPos.Y;
                    bool showNames = Config.ShowContainerNames;
                    bool hideSearched = Config.HideSearchedContainers;
                    var selectedIds = Config.SelectedContainers;

                    foreach (var container in containers)
                    {
                        if (hideSearched && container.Searched)
                            continue;
                        if (!worldBounds.Contains(container.Position))
                            continue;
                        if (!selectedIds.Contains(container.Id))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(container.Position, mapCfg));
                        container.Draw(canvas, sp, showNames);
                    }
                }
            }

            // Exfils (drawn before players so player dots render on top)
            if (Config.ShowExfils)
            {
                var exfils = Memory.Exfils;
                if (exfils is not null)
                {
                    var lp = localPlayer as Tarkov.GameWorld.Player.LocalPlayer;
                    foreach (var exfil in exfils)
                    {
                        if (!worldBounds.Contains(exfil.Position))
                            continue;
                        if (Config.HideInactiveExfils && lp is not null && !exfil.IsAvailableFor(lp))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(exfil.Position, mapCfg));
                        exfil.Draw(canvas, sp, localPlayer);
                    }
                }
            }

            // Transit points (drawn alongside exfils)
            if (Config.ShowTransits)
            {
                var transits = Memory.Transits;
                if (transits is not null)
                {
                    foreach (var transit in transits)
                    {
                        if (!worldBounds.Contains(transit.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(transit.Position, mapCfg));
                        transit.Draw(canvas, sp, localPlayer);
                    }
                }
            }

            // Doors (keyed doors with state)
            if (!Config.BattleMode && Config.ShowDoors)
            {
                var doors = Memory.Doors;
                if (doors is not null)
                {
                    bool filterByLoot = Config.DoorsOnlyNearLoot;

                    foreach (var door in doors)
                    {
                        if (!door.ShouldDraw())
                            continue;
                        if (filterByLoot && !door.IsNearLoot)
                            continue;
                        if (!worldBounds.Contains(door.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(door.Position, mapCfg));
                        door.Draw(canvas, sp, localPlayer);
                    }
                }
            }

            // Quest zones
            if (!Config.BattleMode && Config.ShowQuests)
            {
                var questLocations = Memory.QuestLocations;
                if (questLocations is not null)
                {
                    bool showOptional   = Config.QuestShowOptional;
                    bool showOutlines   = Config.QuestShowOutlines;
                    bool showKill       = Config.QuestShowKillZones;
                    bool showFind       = Config.QuestShowFindZones;
                    bool showPlace      = Config.QuestShowPlaceZones;
                    bool showReach      = Config.QuestShowReachZones;
                    float maxDist       = Config.QuestMaxDistance;
                    bool useMaxDist     = maxDist > 0f;

                    foreach (var loc in questLocations)
                    {
                        if (!worldBounds.Contains(loc.Position))
                            continue;
                        if (!showOptional && loc.Optional)
                            continue;

                        // Objective-type filter
                        bool typeAllowed = loc.ObjectiveType switch
                        {
                            Tarkov.GameWorld.Quests.QuestObjectiveType.FindItem      => showFind,
                            Tarkov.GameWorld.Quests.QuestObjectiveType.PlaceItem     => showPlace,
                            Tarkov.GameWorld.Quests.QuestObjectiveType.VisitLocation => showReach,
                            _                                                         => showKill,
                        };
                        if (!typeAllowed)
                            continue;

                        // Distance cull
                        if (useMaxDist && Vector3.Distance(localPlayer.Position, loc.Position) > maxDist)
                            continue;

                        // Draw outline polygon first (behind marker)
                        if (showOutlines)
                            loc.DrawOutlineProjected(canvas, mapParams, mapCfg);

                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(loc.Position, mapCfg));
                        loc.Draw(canvas, sp, localPlayer);
                    }
                }
            }

            // Explosives (grenades, tripwires, mortar projectiles)
            if (Config.ShowExplosives)
            {
                var explosives = Memory.Explosives;
                if (explosives is not null)
                {
                    foreach (var item in explosives)
                    {
                        if (!item.IsActive)
                            continue;
                        if (!worldBounds.Contains(item.Position))
                            continue;
                        item.Draw(canvas, mapParams, mapCfg, localPlayer);
                    }
                }

                // Pre-throw arc: local player has a grenade in hand and hasn't thrown yet
                var inHand = Memory.InHandGrenadePrediction;
                if (inHand is not null && inHand.Arc.Count > 1)
                {
                    // Arc path
                    using var arcPath = new SKPath();
                    var firstArcPt = mapParams.ToScreenPos(MapParams.ToMapPos(inHand.Arc[0], mapCfg));
                    arcPath.MoveTo(firstArcPt);
                    for (int i = 1; i < inHand.Arc.Count; i++)
                    {
                        arcPath.LineTo(mapParams.ToScreenPos(MapParams.ToMapPos(inHand.Arc[i], mapCfg)));
                    }
                    canvas.DrawPath(arcPath, SKPaints.PaintGrenadePrediction);

                    // Landing marker
                    var landingPt = mapParams.ToScreenPos(MapParams.ToMapPos(inHand.Landing, mapCfg));
                    canvas.DrawCircle(landingPt, 5f, SKPaints.ShapeBorder);
                    canvas.DrawCircle(landingPt, 5f, SKPaints.PaintGrenadeLanding);

                    // Blast radius circle at predicted landing
                    if (inHand.EffDist > 0f)
                    {
                        float landingRadius = inHand.EffDist * mapCfg.Scale * mapCfg.SvgScale * mapParams.XScale;
                        canvas.DrawCircle(landingPt, landingRadius, SKPaints.PaintExplosivesRadius);
                    }

                    // Grenade name label above landing marker
                    if (!string.IsNullOrEmpty(inHand.Name))
                    {
                        var nameWidth = SKPaints.FontRegular11.MeasureText(inHand.Name, SKPaints.TextExplosives);
                        var namePt = new SKPoint(landingPt.X - nameWidth / 2f, landingPt.Y - 12f);
                        canvas.DrawText(inHand.Name, namePt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextShadow);
                        canvas.DrawText(inHand.Name, namePt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextExplosives);
                    }

                    // Distance from local player to predicted landing
                    float landingDist = Vector3.Distance(localPlayer.Position, inHand.Landing);
                    var distText = $"{(int)landingDist}m";
                    var distWidth = SKPaints.FontRegular11.MeasureText(distText, SKPaints.TextExplosives);
                    var distPt = new SKPoint(landingPt.X - distWidth / 2f, landingPt.Y + 14f);
                    canvas.DrawText(distText, distPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextShadow);
                    canvas.DrawText(distText, distPt, SKTextAlign.Left, SKPaints.FontRegular11, SKPaints.TextExplosives);
                }
            }

            // BTR vehicle
            if (Config.ShowBTR)
            {
                var btr = Memory.Btr;
                if (btr is not null && btr.IsActive)
                {
                    if (worldBounds.Contains(btr.Position))
                        btr.Draw(canvas, mapParams, mapCfg, localPlayer, Config.ShowBTRRoute);
                }
            }

            // Airdrops
            if (Config.ShowAirdrops)
            {
                var airdrops = Memory.Airdrops;
                if (airdrops is not null)
                {
                    foreach (var drop in airdrops)
                    {
                        if (!worldBounds.Contains(drop.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(drop.Position, mapCfg));
                        float dist = Vector3.Distance(localPlayer.Position, drop.Position);
                        drop.Draw(canvas, sp, dist);
                    }
                }
            }

            // Switches (static map data)
            if (Config.ShowSwitches)
            {
                var switches = Memory.Switches;
                if (switches is not null)
                {
                    foreach (var sw in switches)
                    {
                        if (!worldBounds.Contains(sw.Position))
                            continue;
                        var sp = mapParams.ToScreenPos(MapParams.ToMapPos(sw.Position, mapCfg));
                        float dist = Vector3.Distance(localPlayer.Position, sw.Position);
                        sw.Draw(canvas, sp, dist);
                    }
                }
            }

            // Group connectors
            if (Config.ConnectGroups && normalPlayers is not null)
                DrawGroupConnectors(canvas, normalPlayers, map, mapParams);

            // Local player screen position — computed once, shared by rings + draw
            var localScreenPos = mapParams.ToScreenPos(MapParams.ToMapPos(localPlayer.Position, mapCfg));

            localPlayer.Draw(canvas, localScreenPos, localPlayer);

            if (normalPlayers is not null)
            {
                var btr = Memory.Btr;
                foreach (var player in normalPlayers)
                {
                    if (player.IsLocalPlayer)
                        continue;
                    if (!worldBounds.Contains(player.Position))
                        continue;

                    // Snap BTR passengers (turret operator / "scav on top") to the BTR's
                    // own XZ so they stop jittering relative to the moving vehicle.
                    var drawPos = player.Position;
                    btr?.TrySnapPassengerXZ(ref drawPos);

                    var sp = mapParams.ToScreenPos(MapParams.ToMapPos(drawPos, mapCfg));
                    player.Draw(canvas, sp, localPlayer);
                }
            }

            // Mouseover tooltips — drawn last so they're always on top
            DrawMouseoverTooltip(canvas, mapParams, map.Config, localPlayer);

            // Player counter overlay — draggable, top-left by default
            DrawPlayerCounter(canvas, normalPlayers?.Count ?? 0, canvasSize);

        }

        private static void DrawGroupConnectors(SKCanvas canvas, List<Player> players, IRadarMap map, MapParams mapParams)
        {
            // Reset pooled collections instead of allocating new ones each frame
            _connectorGroups.Clear();
            _connectorPoolIndex = 0;

            foreach (var p in players)
            {
                if (p.IsHuman && p.IsHostile && p.SpawnGroupID != -1)
                {
                    if (!_connectorGroups.TryGetValue(p.SpawnGroupID, out var list))
                    {
                        // Reuse pooled list or create a new one
                        if (_connectorPoolIndex < _connectorPointPool.Count)
                        {
                            list = _connectorPointPool[_connectorPoolIndex];
                            list.Clear();
                        }
                        else
                        {
                            list = new List<SKPoint>(4);
                            _connectorPointPool.Add(list);
                        }
                        _connectorPoolIndex++;
                        _connectorGroups[p.SpawnGroupID] = list;
                    }
                    list.Add(mapParams.ToScreenPos(MapParams.ToMapPos(p.Position, map.Config)));
                }
            }
            if (_connectorGroups.Count == 0)
                return;
            foreach (var grp in _connectorGroups.Values)
            {
                if (grp.Count <= 1)
                    continue;
                for (int i = 0; i < grp.Count - 1; i++)
                {
                    canvas.DrawLine(
                        grp[i].X, grp[i].Y,
                        grp[i + 1].X, grp[i + 1].Y,
                        SKPaints.PaintConnectorGroup);
                }
            }
        }

        private static void DrawStatusMessage(SKCanvas canvas, string message, float scale, bool animated = false)
        {
            float W = _window.Size.X / scale;
            float H = _window.Size.Y / scale;

            // ── Wave ping-pong animation ─────────────────────────────────────
            string displayText;
            _cyrillicPositions.Clear();
            if (animated)
            {
                long nowMs = Environment.TickCount64;
                float dt = _waveLastMs == 0 ? 0f : (nowMs - _waveLastMs) / 1000f;
                _waveLastMs = nowMs;

                // Wave speed scales with DMA throughput: fast card = fast wave, slow card = slow wave
                float wsBench  = DMA.DmaStats.MaxThroughputMBps;
                float wsCur    = DMA.DmaStats.ReadMBpsCurrent;
                float wsVal    = wsCur > 0f ? wsCur : wsBench;
                float wsCeil   = Math.Max(wsBench, DMA.DmaStats.ReadMBpsPeak);
                float wsRatio  = wsCeil > 0f ? Math.Clamp(wsVal / wsCeil, 0f, 1f) : 0.5f;
                float dynSpeed = WaveSpeed * (0.3f + 0.7f * wsRatio);

                _wavePos += (_waveRight ? dynSpeed : -dynSpeed) * dt;
                if (_wavePos >= 1f) { _wavePos = 1f; _waveRight = false; }
                if (_wavePos <= 0f) { _wavePos = 0f; _waveRight = true;  }

                // 1-in-20 chance to toggle accented-Latin glitch chars in trail
                if (_waveRng.Next(20) == 0) _waveRussian = !_waveRussian;

                displayText = ApplyWave(message, _wavePos, _waveRussian);
            }
            else
            {
                displayText = message;
                _waveLastMs = 0;
                _wavePos    = 0f;
                _waveRight  = true;
            }

            // ── Background panel (contained box, symmetric padding) ──────────
            var bannerFont  = SKPaints.FontBanner;
            float textWidth = bannerFont.MeasureText(displayText);
            float contentW  = Math.Max(textWidth + 120f, 650f);
            float panelX    = (W - contentW) * 0.5f;

            // Pre-compute content boundaries so top/bottom padding are identical
            float textY_    = H * 0.5f + bannerFont.Size * 0.35f;
            float textTop_  = textY_ - bannerFont.Size;          // approx glyph top
            float subY_     = textY_ + bannerFont.Size * 0.55f + 6f;
            float boxTopY_  = subY_  + SKPaints.FontBannerSub.Size * 1.8f;
            float boxH_     = (SKPaints.FontBannerSub.Size + 5f) * 2f + 2f;
            const float panelPad = 30f;
            float panelH = (boxTopY_ + boxH_ - textTop_) + panelPad * 2f;
            float panelY = textTop_ - panelPad;
            using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 175), IsAntialias = false };
            canvas.DrawRect(new SKRect(panelX, panelY, panelX + contentW, panelY + panelH), bgPaint);
            using var borderPaint = new SKPaint
            {
                Color = new SKColor(255, 0, 245, 80),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = false
            };
            canvas.DrawRect(new SKRect(panelX, panelY, panelX + contentW, panelY + panelH), borderPaint);

            // ── Banner text — Cutive Mono ────────────────────────────────────
            float textX = (W - textWidth) * 0.5f;
            float textY = H * 0.5f + bannerFont.Size * 0.35f;

            using var shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 160), IsAntialias = true };

            if (animated && _cyrillicPositions.Count > 0)
            {
                // Per-char render: Cyrillic positions in red, others white
                using var redPaint = new SKPaint { Color = new SKColor(220, 30, 30), IsAntialias = true };
                float cx = textX;
                for (int i = 0; i < displayText.Length; i++)
                {
                    string ch = displayText[i].ToString();
                    float cw = bannerFont.MeasureText(ch);
                    var paint = _cyrillicPositions.Contains(i) ? redPaint : SKPaints.TextRadarStatus;
                    canvas.DrawText(ch, cx + 2f, textY + 2f, bannerFont, shadowPaint);
                    canvas.DrawText(ch, cx, textY, bannerFont, paint);
                    cx += cw;
                }
            }
            else
            {
                canvas.DrawText(displayText, textX + 2f, textY + 2f, bannerFont, shadowPaint);
                canvas.DrawText(displayText, textX, textY, bannerFont, SKPaints.TextRadarStatus);
            }

            // ── Sub-line — Consolas ──────────────────────────────────────────
            string subLine = GetStatusSubLine(message, animated);
            float subY = textY + bannerFont.Size * 0.55f + 6f;
            if (!string.IsNullOrEmpty(subLine))
            {
                // Inject blinking cursor before closing bracket on animated states
                if (animated && subLine.EndsWith("]"))
                {
                    bool cursorOn = (_cursorBlinkSw.ElapsedMilliseconds / CursorBlinkMs) % 2 == 0;
                    char cursor = cursorOn ? 'o' : ' ';
                    subLine = subLine[..^1] + cursor + "]";
                }

                var subFont = SKPaints.FontBannerSub;
                float subW  = subFont.MeasureText(subLine);
                float subX  = (W - subW) * 0.5f;
                canvas.DrawText(subLine, subX + 1f, subY + 1f, subFont, shadowPaint);
                canvas.DrawText(subLine, subX, subY, subFont, SKPaints.TextRadarStatusSub);
            }

            // ── DMA stats box ────────────────────────────────────────────────
            if (animated)
            {
                float boxTopY = subY + SKPaints.FontBannerSub.Size * 1.8f;
                DrawDmaStatsBox(canvas, W, boxTopY);
            }
        }

        private static string GetStatusSubLine(string message, bool animated)
        {
            if (message.StartsWith("Waiting for Tarkov", StringComparison.Ordinal))
            {
                var diag = Memory.DiagnosticStatus;
                return !string.IsNullOrEmpty(diag) ? $"[ {diag.ToUpperInvariant()} ]" : "[ WAITING FOR TARKOV ]";
            }
            if (message.StartsWith("Starting", StringComparison.Ordinal))
                return "[ INITIALIZING DMA INTERFACE ]";

            // Use the rich real-time diagnostic status if available and not generic
            if (!string.IsNullOrEmpty(Memory.DiagnosticStatus) &&
                !Memory.DiagnosticStatus.Equals("Waiting for Raid Start", StringComparison.OrdinalIgnoreCase) &&
                !Memory.DiagnosticStatus.Equals("Raid Active", StringComparison.OrdinalIgnoreCase))
            {
                return $"[ {Memory.DiagnosticStatus.ToUpperInvariant()} ]";
            }

            if (message.StartsWith("Waiting", StringComparison.Ordinal))
                return animated ? "[ MONITORING GAME PROCESS ]" : "[ STANDBY ]";
            if (message.StartsWith("Loading Map", StringComparison.Ordinal))
                return "[ PARSING MAP GEOMETRY ]";
            if (message.StartsWith("In Hideout", StringComparison.Ordinal))
                return "[ HIDEOUT MODE ACTIVE ]";
            // Matching stages (queued, matching, loading, etc.)
            return animated ? "[ AWAITING RAID ENTRY ]" : "";
        }

        private static string ApplyWave(string text, float pos, bool useRussian)
        {
            // _cyrillicPositions cleared by caller; populated here for red rendering
            if (text.Length == 0) return text;
            var sb = new System.Text.StringBuilder(text);
            int crest = (int)(pos * (text.Length - 1));

            // Fade: wave zone shrinks to 0 within 12% of each edge — smooth start/stop
            const float fadeEdge = 0.12f;
            float fade = Math.Clamp(Math.Min(pos / fadeEdge, (1f - pos) / fadeEdge), 0f, 1f);

            int leadLen  = Math.Max(1, text.Length / 12);
            int trailLen = (int)Math.Round(Math.Max(0, text.Length / 4 - leadLen) * fade);

            for (int i = crest; i < Math.Min(crest + leadLen, text.Length); i++)
                sb[i] = WavePool[_waveRng.Next(WavePool.Length)];

            int trailStart = Math.Max(0, crest - trailLen);
            for (int i = trailStart; i < crest; i++)
            {
                if (useRussian && _waveRng.Next(3) == 0)
                {
                    sb[i] = RussianGhost[_waveRng.Next(RussianGhost.Length)];
                    _cyrillicPositions.Add(i);
                }
                else
                    sb[i] = WavePool[_waveRng.Next(WavePool.Length)];
            }
            return sb.ToString();
        }

        private static void DrawDmaStatsBox(SKCanvas canvas, float W, float boxTopY)
        {
            var font    = SKPaints.FontBannerSub;
            const float padX = 18f;
            const float padY = 5f;
            float lineH = font.Size + padY * 2f;

            float mbpsCur   = DMA.DmaStats.ReadMBpsCurrent;
            float mbpsBench = DMA.DmaStats.MaxThroughputMBps;

            // Before scatter reads start, fall back to the boot benchmark value
            bool  showingLive = mbpsCur > 0f;
            float displayVal  = showingLive ? mbpsCur : mbpsBench;

            string hwMaxLabel = showingLive ? "READ" : "BENCH";
            string hwMaxValue = $"{displayVal:F0} MB/s";
            string faultLabel = "FAULTS";
            string faultValue = $"{DMA.DmaStats.FaultCount}";

            float col1W = Math.Max(font.MeasureText(hwMaxLabel), font.MeasureText(hwMaxValue)) + padX * 2f;
            float col2W = Math.Max(font.MeasureText(faultLabel), font.MeasureText(faultValue)) + padX * 2f;
            float boxW  = col1W + col2W + 1f;
            float boxH  = lineH * 2f + 2f;
            float boxX  = (W - boxW) * 0.5f;
            float boxY  = boxTopY;

            using var borderPaint = new SKPaint
            {
                Color = new SKColor(255, 0, 245, 120),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = false
            };
            canvas.DrawRect(new SKRect(boxX, boxY, boxX + boxW, boxY + boxH), borderPaint);
            float divX = boxX + col1W;
            canvas.DrawLine(divX, boxY, divX, boxY + boxH, borderPaint);
            float sepY = boxY + lineH;
            canvas.DrawLine(boxX, sepY, boxX + boxW, sepY, borderPaint);

            using var shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 140), IsAntialias = true };
            float labelY = boxY + padY + font.Size;
            float valueY = sepY  + padY + font.Size;
            float c1cx   = boxX + col1W * 0.5f;
            float c2cx   = divX + col2W * 0.5f;

            void DrawCentered(string s, float cx, float y, SKPaint paint)
            {
                float x = cx - font.MeasureText(s) * 0.5f;
                canvas.DrawText(s, x + 1f, y + 1f, font, shadowPaint);
                canvas.DrawText(s, x, y, font, paint);
            }

            // Gradient applied to both BENCH and READ — fastest relative to ceiling = green
            float   ceiling = Math.Max(mbpsBench, DMA.DmaStats.ReadMBpsPeak);
            SKColor speedColor;
            if (ceiling <= 0f)
            {
                speedColor = SKPaints.TextRadarStatus.Color; // no reference yet — neutral
            }
            else
            {
                float ratio = Math.Clamp(displayVal / ceiling, 0f, 1f);
                byte  r     = ratio <= 0.5f ? (byte)255 : (byte)(255 * (1f - (ratio - 0.5f) * 2f));
                byte  g     = ratio <= 0.5f ? (byte)(255 * ratio * 2f) : (byte)255;
                speedColor  = new SKColor(r, g, 13);
            }

            using var speedPaint = new SKPaint { Color = speedColor, IsAntialias = true };

            DrawCentered(hwMaxLabel, c1cx, labelY, SKPaints.TextRadarStatusSub);
            DrawCentered(faultLabel, c2cx, labelY, SKPaints.TextRadarStatusSub);
            DrawCentered(hwMaxValue, c1cx, valueY, speedPaint);
            DrawCentered(faultValue, c2cx, valueY, SKPaints.TextRadarStatus);
        }

        /// <summary>
        /// Draws a compact player counter overlay showing:
        /// shown / tracked / list — where list turns orange when tracked &lt; list
        /// (indicating some players in game are not yet tracked by the radar).
        /// The overlay is draggable; position is persisted in config.
        /// </summary>
        private static void DrawPlayerCounter(SKCanvas canvas, int shown, SKSize canvasSize)
        {
            var players = AllPlayers;
            if (players is null) return;

            int tracked   = players.Count;
            int listCount = players.ListCount;

            // Skip until the game list has been read at least once.
            if (listCount <= 0) return;

            bool hasMissing = tracked < listCount;

            const float PadX    = 8f;
            const float PadY    = 6f;
            const float CornerR = 4f;
            const float Margin  = 8f;

            var font = SKPaints.FontInfo; // Consolas

            string label      = "Players  ";
            string shownStr   = shown.ToString();
            string sep        = " / ";
            string trackedStr = tracked.ToString();
            string listStr    = listCount.ToString();

            float wLabel   = font.MeasureText(label);
            float wShown   = font.MeasureText(shownStr);
            float wSep     = font.MeasureText(sep);
            float wTracked = font.MeasureText(trackedStr);
            float wList    = font.MeasureText(listStr);
            float totalW   = wLabel + wShown + wSep + wTracked + wSep + wList;

            float boxW = totalW + PadX * 2f;
            float boxH = font.Size + PadY * 2f;

            // Resolve position: use stored config or fall back to top-left anchor
            float panelX, panelY;
            if (Config.PlayerCounterPosX < 0f || Config.PlayerCounterPosY < 0f)
            {
                panelX = Margin;
                panelY = Margin;
            }
            else
            {
                panelX = Math.Clamp(Config.PlayerCounterPosX, 0f, Math.Max(0f, canvasSize.Width  - boxW));
                panelY = Math.Clamp(Config.PlayerCounterPosY, 0f, Math.Max(0f, canvasSize.Height - boxH));
            }

            // Publish bounds for drag hit-testing
            PlayerCounterBounds = new SKRect(panelX, panelY, panelX + boxW, panelY + boxH);

            var bgRect = new SKRoundRect(new SKRect(panelX, panelY, panelX + boxW, panelY + boxH), CornerR);
            canvas.DrawRoundRect(bgRect, SKPaints.PlayerCounterBackground);

            float baseline = panelY + PadY + font.Size - 1f;
            float cx = panelX + PadX;

            var normal = SKPaints.TextPlayerCounterNormal;
            var warn   = hasMissing ? SKPaints.TextPlayerCounterWarn : normal;

            canvas.DrawText(label,      cx, baseline, font, normal); cx += wLabel;
            canvas.DrawText(shownStr,   cx, baseline, font, normal); cx += wShown;
            canvas.DrawText(sep,        cx, baseline, font, normal); cx += wSep;
            canvas.DrawText(trackedStr, cx, baseline, font, normal); cx += wTracked;
            canvas.DrawText(sep,        cx, baseline, font, normal); cx += wSep;
            canvas.DrawText(listStr,    cx, baseline, font, warn);
        }
    }
}

// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Loot
{
    /// <summary>
    /// A single loose loot item on the ground with a map position.
    /// All filter/visibility decisions are delegated to <see cref="LootFilter"/>.
    /// </summary>
    internal sealed class LootItem(TarkovMarketItem item, Vector3 position)
    {
        private readonly TarkovMarketItem _item = item;
        private readonly bool _isMeds = item.IsMeds; // cached — HasCategory iterates strings, not free per-frame

        /// <summary>
        /// True when this is a loose quest item (ItemTemplate.QuestItem flag) such as
        /// pocket watches or Jaeger's letter. These don't appear in the market database
        /// and are visibility-controlled by <see cref="SilkConfig.LootShowQuestItems"/>.
        /// </summary>
        public bool IsQuestItem { get; init; }

        // Cached label to avoid per-frame string allocation
        private string? _cachedLabel;
        private int _cachedLabelKey = int.MinValue;

        // Cached importance flag — updated by LootManager after each loot refresh
        private bool _cachedImportant;

        public string Id { get; } = item.BsgId;
        public string Name => _item.Name;
        public string ShortName => _item.ShortName;
        public Vector3 Position { get; set; } = position;

        /// <summary>The underlying market item data.</summary>
        public TarkovMarketItem MarketItem => _item;

        /// <summary>Effective display price (respects price source + price-per-slot).</summary>
        public int DisplayPrice => LootFilter.GetDisplayPrice(_item);

        /// <summary>Full filter evaluation — visibility, importance, wishlist, category.</summary>
        public LootFilter.FilterResult Evaluate(int displayPrice)
        {
            if (IsQuestItem)
            {
                var config = SilkProgram.Config;
                if (!config.LootShowQuestItems)
                {
                    // Even if static quest items are globally hidden, show items that
                    // are required for an active quest when quest highlighting is on.
                    if (config.QuestHighlightLootItems)
                    {
                        var qm = Memory.QuestManager;
                        if (qm is not null && qm.IsItemRequired(_item.BsgId))
                        {
                            return new LootFilter.FilterResult
                            {
                                Visible = true,
                                Important = true,
                                QuestRequired = true,
                            };
                        }
                    }
                    return LootFilter.FilterResult.Hidden;
                }

                return new LootFilter.FilterResult
                {
                    Visible = true,
                    Important = true,
                    QuestRequired = true,
                };
            }
            return LootFilter.Evaluate(_item, displayPrice);
        }

        /// <summary>Whether the item passes current filter criteria.</summary>
        public bool ShouldDraw() => Evaluate(DisplayPrice).Visible;

        /// <summary>Whether the item is highlighted as important (cached — call <see cref="RefreshImportance"/> to update).</summary>
        public bool IsImportant => _cachedImportant;

        /// <summary>
        /// Refreshes the cached importance flag from the current price/config state.
        /// Called after loot list construction to avoid per-frame/per-door recomputation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RefreshImportance() => _cachedImportant = LootFilter.IsImportant(DisplayPrice);

        /// <summary>
        /// Draw this loot item on the radar canvas with full filter result.
        /// </summary>
        public void Draw(SKCanvas canvas, SKPoint screenPos, int price, LootFilter.FilterResult result, bool differentFloor = false, float heightDelta = 0f)
        {
            var cfg = SilkProgram.Config;
            var paint = GetPaint(result, differentFloor, price, cfg);
            float baseR = Math.Clamp(cfg.LootDotSize, 1.5f, 8f);
            // Tier-based size: base + smaller per-tier bump. Different-floor dots stay compact.
            float radius = differentFloor
                ? Math.Max(1.5f, baseR - 1f)
                : baseR + (result.Tier >= 1 ? (result.Tier - 1) * 0.4f + 0.3f : 0f);

            // Decide whether to draw a height arrow instead of the dot.
            // When the floor differs from the player, the marker itself
            // becomes the arrow (up/down) instead of a separate decoration.
            int heightDir = 0; // -1 = below player, 0 = same floor, +1 = above
            if (cfg.LootShowHeightArrows)
            {
                float thr = Math.Max(0.3f, cfg.LootHeightArrowThreshold);
                if (heightDelta > thr) heightDir = 1;
                else if (heightDelta < -thr) heightDir = -1;
            }

            // Halo ring for rare (tier 2) and top (tier 3) items — makes them easy to pick out.
            if (!differentFloor && result.Tier >= 2)
            {
                float ringR = radius + (result.Tier == 3 ? 3f : 2f);
                canvas.DrawCircle(screenPos, ringR, SKPaints.LootHaloRing);
            }

            if (heightDir != 0)
            {
                // Triangle marker — up for above, down for below. Slightly larger than the dot
                // so floor separation stands out at a glance.
                float size = radius + 1.5f;
                BuildArrowPath(_arrowPath, screenPos, size, heightDir > 0);
                canvas.DrawPath(_arrowPath, SKPaints.LootArrowOutline);
                canvas.DrawPath(_arrowPath, paint);
            }
            else
            {
                canvas.DrawCircle(screenPos, radius, paint);
            }

            // Cache label string — encode state into key. Includes the displayed integer
            // height bucket so the "+Nm" portion of the label refreshes when the player
            // moves up/down past a whole-meter boundary (previously stale).
            int heightBucket = cfg.LootShowHeightDelta && heightDir != 0
                ? (int)MathF.Round(heightDelta)
                : 0;
            int labelKey = HashCode.Combine(price, differentFloor, result.Wishlisted, result.CategoryMatch, result.Tier, heightDir, cfg.LootShowHeightDelta, heightBucket);
            if (labelKey != _cachedLabelKey || _cachedLabel is null)
            {
                _cachedLabelKey = labelKey;
                string prefix = differentFloor ? "[!] " : "";
                // Tier markers use ASCII (Neo Sans Std lacks most geometric glyphs).
                string suffix;
                if (result.Wishlisted)
                    suffix = " *";
                else if (result.Tier == 3)
                    suffix = " ++";
                else if (result.Tier == 2)
                    suffix = " +";
                else if (result.CategoryMatch)
                    suffix = " .";
                else
                    suffix = "";

                // Optional numeric height delta, e.g. "+4m" / "-2m".
                string heightTxt = "";
                if (heightDir != 0 && cfg.LootShowHeightDelta)
                    heightTxt = $" {(heightDelta >= 0 ? "+" : "")}{(int)MathF.Round(heightDelta)}m";

                string baseLabel = price > 0 ? $"{ShortName} ({LootFilter.FormatPrice(price)})" : ShortName;
                _cachedLabel = $"{prefix}{baseLabel}{suffix}{heightTxt}";
            }

            float lx = screenPos.X + 7;
            float ly = screenPos.Y + 4.5f;
            var font = SKPaints.GetFont(cfg.LootLabelFontSize);
            canvas.DrawText(_cachedLabel, lx + 1, ly + 1, font, SKPaints.LootShadow);
            canvas.DrawText(_cachedLabel, lx, ly, font, paint);
        }

        // Render-thread-only reusable path for height-direction arrow triangles.
        // Reset and repopulated each draw call — avoids per-item SKPath allocations.
        private static readonly SKPath _arrowPath = new();

        /// <summary>
        /// Populates a reusable <see cref="SKPath"/> with a filled triangle centered on <paramref name="p"/>.
        /// Up-triangle when <paramref name="up"/> is true, down-triangle otherwise.
        /// </summary>
        private static void BuildArrowPath(SKPath path, SKPoint p, float size, bool up)
        {
            path.Reset();
            if (up)
            {
                path.MoveTo(p.X, p.Y - size);
                path.LineTo(p.X - size, p.Y + size * 0.8f);
                path.LineTo(p.X + size, p.Y + size * 0.8f);
            }
            else
            {
                path.MoveTo(p.X, p.Y + size);
                path.LineTo(p.X - size, p.Y - size * 0.8f);
                path.LineTo(p.X + size, p.Y - size * 0.8f);
            }
            path.Close();
        }

        private SKPaint GetPaint(LootFilter.FilterResult result, bool differentFloor, int price, SilkConfig config)
        {
            bool isMed = config.LootMedsRedTint && result.CategoryMatch && _isMeds;

            // Price gradient: brightness scales with value (wishlisted items keep cyan)
            if (config.LootPriceGradient && !result.Wishlisted)
            {
                int idx = GetGradientIndex(price, config);
                if (isMed)
                    return differentFloor ? SKPaints.LootMedGradientDimmed[idx] : SKPaints.LootMedGradient[idx];
                return differentFloor ? SKPaints.LootGradientDimmed[idx] : SKPaints.LootGradient[idx];
            }

            // Solid med tint (gradient off)
            if (isMed)
                return differentFloor ? SKPaints.LootMedDimmed : SKPaints.LootMed;

            // Existing tier-based logic
            if (result.Wishlisted)
                return differentFloor ? SKPaints.LootWishlistDimmed : SKPaints.LootWishlist;
            if (result.Tier >= 3)
                return differentFloor ? SKPaints.LootTopDimmed : SKPaints.LootTop;
            if (result.Tier == 2)
                return differentFloor ? SKPaints.LootRareDimmed : SKPaints.LootRare;
            if (result.Important)
                return differentFloor ? SKPaints.LootImportantDimmed : SKPaints.LootImportant;
            return differentFloor ? SKPaints.LootNormalDimmed : SKPaints.LootNormal;
        }

        private static int GetGradientIndex(int price, SilkConfig config)
        {
            const int Steps = 20;
            long maxP = (long)config.LootImportantPrice * 5;
            long minP = config.LootMinPrice;
            if (maxP <= minP) maxP = minP + 1;
            float t = Math.Clamp((float)(price - minP) / (maxP - minP), 0f, 1f);
            return Math.Min(Steps - 1, (int)(t * Steps));
        }
    }
}

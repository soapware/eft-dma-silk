// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Frozen;
using VmmSharpEx.Options;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Player.Plugins
{
    /// <summary>
    /// Scans nearby AI bots' Backpack and Pockets slots for valuable loot.
    /// Results are filtered through the loot filter system and written to
    /// <see cref="Player.BotInventory"/> for display in the player mouseover tooltip.
    /// <para>
    /// All DMA reads are scatter-batched across ALL eligible bots in a single set
    /// of rounds (not one-bot-at-a-time), keeping DMA round-trips constant
    /// regardless of bot count.
    /// </para>
    /// Trigger cadence: count-change poll every 1 s, full scan every 15 s.
    /// </summary>
    internal sealed class BotInventoryScanner
    {
        private const int FullScanIntervalSec = 15;
        private const int QuickPollMs        = 1000;
        private const int MaxSlotsPerBot     = 25; // EFT equipment slots ≤ 20 typically
        private const int MaxItemsPerGrid    = 20;
        private const int StrBytes           = 128;

        private static readonly FrozenSet<string> TargetSlots =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Backpack", "Pockets" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private readonly Stopwatch _fullScanSw   = new();
        private readonly Stopwatch _quickPollSw  = new();

        // Per-bot count cache for quick-poll: bot.Base → (collPtrs, counts)
        private readonly Dictionary<ulong, (List<ulong> Ptrs, List<int> Counts)> _countCache = new();

        /// <summary>
        /// Call from the registration worker tick. Self-throttles internally.
        /// </summary>
        public void Update(IReadOnlyCollection<Player> players, LocalPlayer? localPlayer)
        {
            if (localPlayer is null) return;

            if (_quickPollSw.ElapsedMilliseconds < QuickPollMs) return;
            _quickPollSw.Restart();

            bool forceFullScan = !_fullScanSw.IsRunning
                || _fullScanSw.Elapsed.TotalSeconds >= FullScanIntervalSec;

            if (!forceFullScan && !AnyCountChanged(players))
                return;

            RunFullScan(players, localPlayer);
            _fullScanSw.Restart();
        }

        // ── Quick count-change poll ───────────────────────────────────────────────

        private bool AnyCountChanged(IReadOnlyCollection<Player> players)
        {
            if (_countCache.Count == 0) return true;

            try
            {
                using var r = Memory.GetScatter(VmmFlags.NOCACHE);
                foreach (var kv in _countCache)
                    for (int j = 0; j < kv.Value.Ptrs.Count; j++)
                        r.PrepareReadValue<int>(kv.Value.Ptrs[j] + 0x18);
                r.Execute();

                foreach (var kv in _countCache)
                {
                    for (int j = 0; j < kv.Value.Ptrs.Count; j++)
                    {
                        if (!r.ReadValue<int>(kv.Value.Ptrs[j] + 0x18, out var cnt)
                            || cnt != kv.Value.Counts[j])
                            return true;
                    }
                }
                return false;
            }
            catch { return true; }
        }

        // ── Full scan ────────────────────────────────────────────────────────────

        private void RunFullScan(IReadOnlyCollection<Player> players, LocalPlayer localPlayer)
        {
            try { FullScan(players, localPlayer); }
            catch { /* transient DMA error — stale BotInventory values are harmless */ }
        }

        private void FullScan(IReadOnlyCollection<Player> players, LocalPlayer localPlayer)
        {
            var config = SilkProgram.Config;
            float rangeM = config.BotInventoryScanRange;
            Vector3 localPos = localPlayer.Position;

            // Filter to AI bots within range that have a valid position
            var bots = new List<Player>(16);
            foreach (var p in players)
            {
                if (p is null || p.IsLocalPlayer || p.IsHuman || !p.IsAlive || !p.HasValidPosition)
                    continue;
                if (Vector3.DistanceSquared(localPos, p.Position) > rangeM * rangeM)
                    continue;
                bots.Add(p);
            }

            // Clear inventory for bots no longer in range or no longer valid
            var botBases = new HashSet<ulong>(bots.Count);
            foreach (var b in bots)
                botBases.Add(b.Base);
            List<ulong>? staleKeys = null;
            foreach (var k in _countCache.Keys)
                if (!botBases.Contains(k))
                    (staleKeys ??= new()).Add(k);
            if (staleKeys is not null)
                foreach (var k in staleKeys)
                    _countCache.Remove(k);

            if (bots.Count == 0) return;

            int n = bots.Count;

            // ── R1: ObservedPlayerController ptrs ────────────────────────────────
            var opcPtrs = new ulong[n];
            using (var r1 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < n; i++)
                    r1.PrepareReadValue<ulong>(bots[i].Base + Offsets.ObservedPlayerView.ObservedPlayerController);
                r1.Execute();
                for (int i = 0; i < n; i++)
                    r1.ReadValue<ulong>(bots[i].Base + Offsets.ObservedPlayerView.ObservedPlayerController, out opcPtrs[i]);
            }

            // ── R2: InventoryController ptrs ─────────────────────────────────────
            var icPtrs = new ulong[n];
            using (var r2 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < n; i++)
                    if (opcPtrs[i].IsValidVirtualAddress())
                        r2.PrepareReadValue<ulong>(opcPtrs[i] + Offsets.ObservedPlayerController.InventoryController);
                r2.Execute();
                for (int i = 0; i < n; i++)
                    if (opcPtrs[i].IsValidVirtualAddress())
                        r2.ReadValue<ulong>(opcPtrs[i] + Offsets.ObservedPlayerController.InventoryController, out icPtrs[i]);
            }

            // ── R3: Inventory ptrs ───────────────────────────────────────────────
            var invPtrs = new ulong[n];
            using (var r3 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < n; i++)
                    if (icPtrs[i].IsValidVirtualAddress())
                        r3.PrepareReadValue<ulong>(icPtrs[i] + Offsets.InventoryController.Inventory);
                r3.Execute();
                for (int i = 0; i < n; i++)
                    if (icPtrs[i].IsValidVirtualAddress())
                        r3.ReadValue<ulong>(icPtrs[i] + Offsets.InventoryController.Inventory, out invPtrs[i]);
            }

            // ── R4: Equipment ptrs ───────────────────────────────────────────────
            var equipPtrs = new ulong[n];
            using (var r4 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < n; i++)
                    if (invPtrs[i].IsValidVirtualAddress())
                        r4.PrepareReadValue<ulong>(invPtrs[i] + Offsets.Inventory.Equipment);
                r4.Execute();
                for (int i = 0; i < n; i++)
                    if (invPtrs[i].IsValidVirtualAddress())
                        r4.ReadValue<ulong>(invPtrs[i] + Offsets.Inventory.Equipment, out equipPtrs[i]);
            }

            // ── R5: Slots array ptrs ──────────────────────────────────────────────
            var slotsPtrs = new ulong[n];
            using (var r5 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < n; i++)
                    if (equipPtrs[i].IsValidVirtualAddress())
                        r5.PrepareReadValue<ulong>(equipPtrs[i] + Offsets.Equipment.Slots);
                r5.Execute();
                for (int i = 0; i < n; i++)
                    if (equipPtrs[i].IsValidVirtualAddress())
                        r5.ReadValue<ulong>(equipPtrs[i] + Offsets.Equipment.Slots, out slotsPtrs[i]);
            }

            // ── R6: Slot array counts ─────────────────────────────────────────────
            var slotCounts = new int[n];
            using (var r6 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < n; i++)
                    if (slotsPtrs[i].IsValidVirtualAddress())
                        r6.PrepareReadValue<int>(slotsPtrs[i] + 0x18);
                r6.Execute();
                for (int i = 0; i < n; i++)
                    if (slotsPtrs[i].IsValidVirtualAddress())
                        r6.ReadValue<int>(slotsPtrs[i] + 0x18, out slotCounts[i]);
            }

            // ── R7: Slot ptrs (all bots, capped at MaxSlotsPerBot) ───────────────
            // Track which (botIdx, slotIdx) each read corresponds to
            var slotReadList = new List<(int BotIdx, int SlotIdx, ulong ReadAddr)>(n * 15);
            using (var r7 = Memory.GetScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < n; i++)
                {
                    if (!slotsPtrs[i].IsValidVirtualAddress() || slotCounts[i] <= 0) continue;
                    int cnt = Math.Min(slotCounts[i], MaxSlotsPerBot);
                    for (int j = 0; j < cnt; j++)
                    {
                        ulong addr = slotsPtrs[i] + 0x20 + (ulong)(j * 8);
                        r7.PrepareReadValue<ulong>(addr);
                        slotReadList.Add((i, j, addr));
                    }
                }
                r7.Execute();

                // Store slot ptrs per bot: botIdx → list of (slotIdx, slotPtr)
                var slotPtrsByBot = new List<ulong>[n];
                for (int i = 0; i < n; i++)
                    slotPtrsByBot[i] = new List<ulong>(slotCounts[i] > 0 ? Math.Min(slotCounts[i], MaxSlotsPerBot) : 0);

                foreach (var (bi, si, addr) in slotReadList)
                {
                    r7.ReadValue<ulong>(addr, out var slotPtr);
                    slotPtrsByBot[bi].Add(slotPtr);
                }

                // ── R8: Slot.ID + Slot.ContainedItem for all slots ──────────────────
                var namePtrReadList  = new List<(int BotIdx, int SlotIdx, ulong SlotPtr)>(slotReadList.Count);
                var contItemPtrList  = new List<(int BotIdx, int SlotIdx, ulong ReadAddr)>(slotReadList.Count);

                using (var r8 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = 0; j < slotPtrsByBot[i].Count; j++)
                        {
                            ulong sp = slotPtrsByBot[i][j];
                            if (!sp.IsValidVirtualAddress()) continue;
                            r8.PrepareReadValue<ulong>(sp + Offsets.Slot.ID);
                            r8.PrepareReadValue<ulong>(sp + Offsets.Slot.ContainedItem);
                        }
                    }
                    r8.Execute();

                    var namePtrs = new ulong[n][];
                    var contPtrs = new ulong[n][];
                    for (int i = 0; i < n; i++)
                    {
                        int sc = slotPtrsByBot[i].Count;
                        namePtrs[i] = new ulong[sc];
                        contPtrs[i] = new ulong[sc];
                        for (int j = 0; j < sc; j++)
                        {
                            ulong sp = slotPtrsByBot[i][j];
                            if (!sp.IsValidVirtualAddress()) continue;
                            r8.ReadValue<ulong>(sp + Offsets.Slot.ID, out namePtrs[i][j]);
                            r8.ReadValue<ulong>(sp + Offsets.Slot.ContainedItem, out contPtrs[i][j]);
                        }
                    }

                    // ── R9: Slot name strings — filter to Backpack/Pockets ─────────────
                    // Collect all valid item ptrs for target slots
                    var targetItems = new List<(int BotIdx, ulong ContainedItem)>(n * 2);

                    using (var r9 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < n; i++)
                            for (int j = 0; j < namePtrs[i].Length; j++)
                                if (namePtrs[i][j].IsValidVirtualAddress())
                                    r9.PrepareRead(namePtrs[i][j] + 0x14, StrBytes);
                        r9.Execute();

                        for (int i = 0; i < n; i++)
                        {
                            for (int j = 0; j < namePtrs[i].Length; j++)
                            {
                                if (!namePtrs[i][j].IsValidVirtualAddress()
                                    || !contPtrs[i][j].IsValidVirtualAddress()) continue;
                                var name = r9.ReadString(namePtrs[i][j] + 0x14, StrBytes, Encoding.Unicode);
                                if (name is not null && TargetSlots.Contains(name))
                                    targetItems.Add((i, contPtrs[i][j]));
                            }
                        }
                    }

                    if (targetItems.Count == 0)
                    {
                        foreach (var b in bots) b.BotInventory ??= [];
                        return;
                    }

                    // ── R10: CompoundItem.Grids ptrs ──────────────────────────────────
                    var gridArrPtrs = new ulong[targetItems.Count];
                    using (var r10 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < targetItems.Count; i++)
                            r10.PrepareReadValue<ulong>(targetItems[i].ContainedItem + Offsets.CompoundItem.Grids);
                        r10.Execute();
                        for (int i = 0; i < targetItems.Count; i++)
                            r10.ReadValue<ulong>(targetItems[i].ContainedItem + Offsets.CompoundItem.Grids, out gridArrPtrs[i]);
                    }

                    // ── R11: Grid array counts ────────────────────────────────────────
                    var gridSlotAddrs = new List<(int BotIdx, ulong Addr)>(targetItems.Count * 4);
                    using (var r11 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < gridArrPtrs.Length; i++)
                            if (gridArrPtrs[i].IsValidVirtualAddress())
                                r11.PrepareReadValue<int>(gridArrPtrs[i] + 0x18);
                        r11.Execute();
                        for (int i = 0; i < gridArrPtrs.Length; i++)
                        {
                            if (!gridArrPtrs[i].IsValidVirtualAddress()) continue;
                            if (r11.ReadValue<int>(gridArrPtrs[i] + 0x18, out var cnt) && cnt > 0)
                                for (int g = 0; g < Math.Min(cnt, 8); g++)
                                    gridSlotAddrs.Add((targetItems[i].BotIdx, gridArrPtrs[i] + 0x20 + (ulong)(g * 8)));
                        }
                    }
                    if (gridSlotAddrs.Count == 0) { foreach (var b in bots) b.BotInventory ??= []; return; }

                    // ── R12: Grid ptrs ────────────────────────────────────────────────
                    var gridPtrs = new (int BotIdx, ulong Ptr)[gridSlotAddrs.Count];
                    using (var r12 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < gridSlotAddrs.Count; i++)
                            r12.PrepareReadValue<ulong>(gridSlotAddrs[i].Addr);
                        r12.Execute();
                        for (int i = 0; i < gridSlotAddrs.Count; i++)
                        {
                            r12.ReadValue<ulong>(gridSlotAddrs[i].Addr, out var gp);
                            gridPtrs[i] = (gridSlotAddrs[i].BotIdx, gp);
                        }
                    }

                    // ── R13: ItemCollection ptrs (Grid + ContainedItems = 0x48) ──────
                    var collPtrs = new (int BotIdx, ulong Ptr)[gridPtrs.Length];
                    using (var r13 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < gridPtrs.Length; i++)
                            if (gridPtrs[i].Ptr.IsValidVirtualAddress())
                                r13.PrepareReadValue<ulong>(gridPtrs[i].Ptr + Offsets.Grids.ContainedItems);
                        r13.Execute();
                        for (int i = 0; i < gridPtrs.Length; i++)
                        {
                            if (gridPtrs[i].Ptr.IsValidVirtualAddress())
                                r13.ReadValue<ulong>(gridPtrs[i].Ptr + Offsets.Grids.ContainedItems, out collPtrs[i].Ptr);
                            collPtrs[i].BotIdx = gridPtrs[i].BotIdx;
                        }
                    }

                    // ── R14: Item counts + backing-array ptrs ─────────────────────────
                    var itemCounts = new int[collPtrs.Length];
                    var arrayPtrs  = new ulong[collPtrs.Length];
                    using (var r14 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < collPtrs.Length; i++)
                        {
                            if (!collPtrs[i].Ptr.IsValidVirtualAddress()) continue;
                            r14.PrepareReadValue<int>(collPtrs[i].Ptr + 0x18);
                            r14.PrepareReadValue<ulong>(collPtrs[i].Ptr + 0x20);
                        }
                        r14.Execute();
                        for (int i = 0; i < collPtrs.Length; i++)
                        {
                            if (!collPtrs[i].Ptr.IsValidVirtualAddress()) continue;
                            r14.ReadValue<int>(collPtrs[i].Ptr + 0x18, out itemCounts[i]);
                            r14.ReadValue<ulong>(collPtrs[i].Ptr + 0x20, out arrayPtrs[i]);
                        }
                    }

                    // Update count cache for quick-poll
                    _countCache.Clear();
                    for (int i = 0; i < collPtrs.Length; i++)
                    {
                        if (!collPtrs[i].Ptr.IsValidVirtualAddress()) continue;
                        ulong botBase = bots[collPtrs[i].BotIdx].Base;
                        if (!_countCache.TryGetValue(botBase, out var cc))
                            _countCache[botBase] = cc = (new List<ulong>(), new List<int>());
                        cc.Ptrs.Add(collPtrs[i].Ptr);
                        cc.Counts.Add(itemCounts[i]);
                    }

                    // ── R15: InteractiveLootItem ptrs ─────────────────────────────────
                    var iLootReadList = new List<(int BotIdx, ulong Addr)>(collPtrs.Length * 8);
                    using (var r15 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < arrayPtrs.Length; i++)
                        {
                            if (!arrayPtrs[i].IsValidVirtualAddress() || itemCounts[i] <= 0) continue;
                            int cnt = Math.Min(itemCounts[i], MaxItemsPerGrid);
                            for (int j = 0; j < cnt; j++)
                            {
                                ulong addr = arrayPtrs[i] + 0x20 + (ulong)(j * 8);
                                r15.PrepareReadValue<ulong>(addr);
                                iLootReadList.Add((collPtrs[i].BotIdx, addr));
                            }
                        }
                        r15.Execute();

                        var iLootPtrs = new List<(int BotIdx, ulong Ptr)>(iLootReadList.Count);
                        foreach (var (bi, addr) in iLootReadList)
                        {
                            if (r15.ReadValue<ulong>(addr, out var p) && p.IsValidVirtualAddress())
                                iLootPtrs.Add((bi, p));
                        }
                        if (iLootPtrs.Count == 0) { foreach (var b in bots) b.BotInventory ??= []; return; }

                        // ── R16: LootItem ptrs via InteractiveLootItem.Item (+0xF0) ─────
                        var lootPtrs = new List<(int BotIdx, ulong Ptr)>(iLootPtrs.Count);
                        using (var r16 = Memory.GetScatter(VmmFlags.NOCACHE))
                        {
                            for (int i = 0; i < iLootPtrs.Count; i++)
                                r16.PrepareReadValue<ulong>(iLootPtrs[i].Ptr + Offsets.InteractiveLootItem.Item);
                            r16.Execute();
                            for (int i = 0; i < iLootPtrs.Count; i++)
                            {
                                if (r16.ReadValue<ulong>(iLootPtrs[i].Ptr + Offsets.InteractiveLootItem.Item, out var p) && p.IsValidVirtualAddress())
                                    lootPtrs.Add((iLootPtrs[i].BotIdx, p));
                            }
                        }
                        if (lootPtrs.Count == 0) { foreach (var b in bots) b.BotInventory ??= []; return; }

                        // ── R17: Template ptrs ────────────────────────────────────────────
                        var templatePtrs = new ulong[lootPtrs.Count];
                        using (var r17 = Memory.GetScatter(VmmFlags.NOCACHE))
                        {
                            for (int i = 0; i < lootPtrs.Count; i++)
                                r17.PrepareReadValue<ulong>(lootPtrs[i].Ptr + Offsets.LootItem.Template);
                            r17.Execute();
                            for (int i = 0; i < lootPtrs.Count; i++)
                                r17.ReadValue<ulong>(lootPtrs[i].Ptr + Offsets.LootItem.Template, out templatePtrs[i]);
                        }

                        // ── R18: MongoID structs ──────────────────────────────────────────
                        var mongoIds = new Types.MongoID[templatePtrs.Length];
                        using (var r18 = Memory.GetScatter(VmmFlags.NOCACHE))
                        {
                            for (int i = 0; i < templatePtrs.Length; i++)
                                if (templatePtrs[i].IsValidVirtualAddress())
                                    r18.PrepareReadValue<Types.MongoID>(templatePtrs[i] + Offsets.ItemTemplate._id);
                            r18.Execute();
                            for (int i = 0; i < templatePtrs.Length; i++)
                                if (templatePtrs[i].IsValidVirtualAddress())
                                    r18.ReadValue<Types.MongoID>(templatePtrs[i] + Offsets.ItemTemplate._id, out mongoIds[i]);
                        }

                        // ── R19: BSG ID strings → filter → write BotInventory ─────────────
                        var bsgIdAddrs = new ulong[mongoIds.Length];
                        for (int i = 0; i < mongoIds.Length; i++)
                            if (mongoIds[i].StringID.IsValidVirtualAddress())
                                bsgIdAddrs[i] = mongoIds[i].StringID;

                        var bsgIds = new string?[mongoIds.Length];
                        using (var r19 = Memory.GetScatter(VmmFlags.NOCACHE))
                        {
                            for (int i = 0; i < bsgIdAddrs.Length; i++)
                                if (bsgIdAddrs[i].IsValidVirtualAddress())
                                    r19.PrepareRead(bsgIdAddrs[i] + 0x14, StrBytes);
                            r19.Execute();
                            for (int i = 0; i < bsgIdAddrs.Length; i++)
                                if (bsgIdAddrs[i].IsValidVirtualAddress())
                                    bsgIds[i] = r19.ReadString(bsgIdAddrs[i] + 0x14, StrBytes, Encoding.Unicode);
                        }

                        // Build per-bot inventory lists filtered by loot filter
                        var inventories = new List<LootItem>[n];
                        for (int i = 0; i < n; i++)
                            inventories[i] = new List<LootItem>(8);

                        var filterData = LootFilter.FilterData;
                        for (int i = 0; i < lootPtrs.Count; i++)
                        {
                            var rawId = bsgIds[i];
                            if (string.IsNullOrEmpty(rawId)) continue;
                            int nt = rawId.IndexOf('\0');
                            var bsgId = nt >= 0 ? rawId[..nt] : rawId;
                            if (bsgId.Length == 0) continue;

                            if (!EftDataManager.AllItems.TryGetValue(bsgId, out var marketItem)) continue;

                            int displayPrice = LootFilter.GetDisplayPrice(marketItem);

                            // Apply loot filter: important price OR wishlisted
                            bool important = LootFilter.IsImportant(displayPrice);
                            bool wishlisted = filterData.IsWishlisted(bsgId);
                            if (!important && !wishlisted) continue;

                            var item = new LootItem(marketItem, bots[lootPtrs[i].BotIdx].Position);
                            item.RefreshImportance();
                            inventories[lootPtrs[i].BotIdx].Add(item);
                        }

                        // Write results back to players
                        for (int i = 0; i < n; i++)
                            bots[i].BotInventory = inventories[i].Count > 0
                                ? inventories[i].OrderByDescending(x => x.DisplayPrice).ToList()
                                : [];
                    }
                }
            }
        }
    }
}

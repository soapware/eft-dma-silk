// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Frozen;
using VmmSharpEx.Options;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Player
{
    /// <summary>
    /// Scans the local player's Pockets, Backpack, SecuredContainer, and TacticalVest
    /// for key items, then updates <see cref="Interactables.Door.IsKeyHeld"/> on every
    /// tracked locked door.
    /// <para>
    /// <b>Scan triggers:</b>
    /// <list type="bullet">
    ///   <item>Immediately on first call after raid entry.</item>
    ///   <item>When any grid item count changes (checked every 500 ms via a single cheap scatter).</item>
    ///   <item>Every 60 seconds as a safety fallback (catches key-swap: drop key A, pick up key B
    ///     in the same tick — counts stay equal but the key set changed).</item>
    /// </list>
    /// </para>
    /// Called from DoSecondaryWork (100 ms registration-worker tick). All DMA work is
    /// batched scatter operations; the 500 ms count poll is a single scatter round with
    /// 3–6 int reads.
    /// </summary>
    internal sealed class KeyInventoryScanner
    {
        private const int FallbackSec  = 60;  // safety full-scan interval
        private const int QuickCheckMs = 500; // item-count poll interval

        private const int StrBytes = 128; // 64 UTF-16 chars — covers BSG IDs and slot names

        private bool _initialized;
        private readonly Stopwatch _fallbackSw   = new();
        private readonly Stopwatch _quickCheckSw = new();

        // Populated by FullScan; used by CountsChanged()
        private readonly List<ulong> _cachedCollPtrs = new(8);
        private readonly List<int>   _cachedCounts   = new(8);

        private static readonly FrozenSet<string> TargetSlots =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Pockets", "SecuredContainer", "Backpack", "TacticalVest" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Call every registration-worker tick. Self-throttles internally.
        /// </summary>
        public void Update(ulong playerBase, IReadOnlyList<Interactables.Door> doors)
        {
            // First call after raid entry — scan immediately
            if (!_initialized)
            {
                RunFullScan(playerBase, doors);
                _initialized = true;
                _fallbackSw.Restart();
                _quickCheckSw.Restart();
                return;
            }

            // Rate-limit the count poll to every 500 ms
            if (_quickCheckSw.ElapsedMilliseconds < QuickCheckMs) return;
            _quickCheckSw.Restart();

            // Trigger full scan if counts changed OR fallback interval elapsed
            if (CountsChanged() || _fallbackSw.Elapsed.TotalSeconds >= FallbackSec)
            {
                RunFullScan(playerBase, doors);
                _fallbackSw.Restart();
            }
        }

        // ── Count poll — 1 scatter round, 3–6 int reads ──────────────────────────

        private bool CountsChanged()
        {
            if (_cachedCollPtrs.Count == 0) return true;

            try
            {
                using var r = Memory.GetScatter(VmmFlags.NOCACHE);
                for (int i = 0; i < _cachedCollPtrs.Count; i++)
                    r.PrepareReadValue<int>(_cachedCollPtrs[i] + 0x18);
                r.Execute();

                for (int i = 0; i < _cachedCollPtrs.Count; i++)
                {
                    if (!r.ReadValue<int>(_cachedCollPtrs[i] + 0x18, out var cnt) || cnt != _cachedCounts[i])
                        return true;
                }
                return false;
            }
            catch
            {
                return true; // DMA error — assume changed, trigger full scan
            }
        }

        // ── Full scan — 12 scatter rounds ────────────────────────────────────────

        private void RunFullScan(ulong playerBase, IReadOnlyList<Interactables.Door> doors)
        {
            try { FullScan(playerBase, doors); }
            catch { /* silently skip on transient DMA error — stale IsKeyHeld values are harmless */ }
        }

        private void FullScan(ulong playerBase, IReadOnlyList<Interactables.Door> doors)
        {
            // ── Walk inventory pointer chain ──────────────────────────────────────────
            if (!Memory.TryReadPtr(playerBase + Offsets.Player._inventoryController, out var invCtrl)
                || !invCtrl.IsValidVirtualAddress()) return;
            if (!Memory.TryReadPtr(invCtrl + Offsets.InventoryController.Inventory, out var inv)
                || !inv.IsValidVirtualAddress()) return;
            if (!Memory.TryReadPtr(inv + Offsets.Inventory.Equipment, out var equip)
                || !equip.IsValidVirtualAddress()) return;
            if (!Memory.TryReadPtr(equip + Offsets.Equipment.Slots, out var slotsPtr)
                || !slotsPtr.IsValidVirtualAddress()) return;

            MemArray<ulong> slots;
            try { slots = MemArray<ulong>.Get(slotsPtr, false); }
            catch { return; }

            using (slots)
            {
                int n = slots.Count;
                if (n == 0) return;

                var namePtrs = new ulong[n];
                var contPtrs = new ulong[n];

                // ── R1: Slot.ID + Slot.ContainedItem ptrs ────────────────────────────
                using (var r1 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < n; i++)
                    {
                        var sp = slots[i];
                        if (!sp.IsValidVirtualAddress()) continue;
                        r1.PrepareReadPtr(sp + Offsets.Slot.ID);
                        r1.PrepareReadPtr(sp + Offsets.Slot.ContainedItem);
                    }
                    r1.Execute();
                    for (int i = 0; i < n; i++)
                    {
                        var sp = slots[i];
                        if (!sp.IsValidVirtualAddress()) continue;
                        r1.ReadValue<ulong>(sp + Offsets.Slot.ID, out namePtrs[i]);
                        r1.ReadValue<ulong>(sp + Offsets.Slot.ContainedItem, out contPtrs[i]);
                    }
                }

                // ── R2: Slot name strings — filter to container slots ─────────────────
                var containerItems = new List<ulong>(4);
                using (var r2 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < n; i++)
                        if (namePtrs[i].IsValidVirtualAddress())
                            r2.PrepareRead(namePtrs[i] + 0x14, StrBytes);
                    r2.Execute();

                    for (int i = 0; i < n; i++)
                    {
                        if (!namePtrs[i].IsValidVirtualAddress() || !contPtrs[i].IsValidVirtualAddress()) continue;
                        var name = r2.ReadString(namePtrs[i] + 0x14, StrBytes, Encoding.Unicode);
                        if (name is not null && TargetSlots.Contains(name))
                            containerItems.Add(contPtrs[i]);
                    }
                }

                if (containerItems.Count == 0) return;

                // ── R3: CompoundItem.Grids ptrs ───────────────────────────────────────
                var gridsArrPtrs = new ulong[containerItems.Count];
                using (var r3 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < containerItems.Count; i++)
                        r3.PrepareReadPtr(containerItems[i] + Offsets.CompoundItem.Grids);
                    r3.Execute();
                    for (int i = 0; i < containerItems.Count; i++)
                        r3.ReadValue<ulong>(containerItems[i] + Offsets.CompoundItem.Grids, out gridsArrPtrs[i]);
                }

                // ── R4: Grid array counts ─────────────────────────────────────────────
                var gridSlotAddrs = new List<ulong>(32);
                using (var r4 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < gridsArrPtrs.Length; i++)
                        if (gridsArrPtrs[i].IsValidVirtualAddress())
                            r4.PrepareReadValue<int>(gridsArrPtrs[i] + 0x18);
                    r4.Execute();
                    for (int i = 0; i < gridsArrPtrs.Length; i++)
                    {
                        if (!gridsArrPtrs[i].IsValidVirtualAddress()) continue;
                        if (r4.ReadValue<int>(gridsArrPtrs[i] + 0x18, out var cnt) && cnt > 0)
                            for (int g = 0; g < Math.Min(cnt, 8); g++)
                                gridSlotAddrs.Add(gridsArrPtrs[i] + 0x20 + (ulong)(g * 8));
                    }
                }
                if (gridSlotAddrs.Count == 0) return;

                // ── R5: Grid ptrs ─────────────────────────────────────────────────────
                var gridPtrs = new ulong[gridSlotAddrs.Count];
                using (var r5 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < gridSlotAddrs.Count; i++)
                        r5.PrepareReadPtr(gridSlotAddrs[i]);
                    r5.Execute();
                    for (int i = 0; i < gridSlotAddrs.Count; i++)
                        r5.ReadValue<ulong>(gridSlotAddrs[i], out gridPtrs[i]);
                }

                // ── R6: ItemCollection ptrs (Grids.ContainedItems = 0x48) ────────────
                var collPtrs = new ulong[gridPtrs.Length];
                using (var r6 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < gridPtrs.Length; i++)
                        if (gridPtrs[i].IsValidVirtualAddress())
                            r6.PrepareReadPtr(gridPtrs[i] + Offsets.Grids.ContainedItems);
                    r6.Execute();
                    for (int i = 0; i < gridPtrs.Length; i++)
                        if (gridPtrs[i].IsValidVirtualAddress())
                            r6.ReadValue<ulong>(gridPtrs[i] + Offsets.Grids.ContainedItems, out collPtrs[i]);
                }

                // ── R7: item count + backing-array ptr ────────────────────────────────
                // ItemCollection layout: count at +0x18, array ptr at +0x20
                var itemCounts = new int[collPtrs.Length];
                var arrayPtrs  = new ulong[collPtrs.Length];
                using (var r7 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < collPtrs.Length; i++)
                    {
                        if (!collPtrs[i].IsValidVirtualAddress()) continue;
                        r7.PrepareReadValue<int>(collPtrs[i] + 0x18);
                        r7.PrepareReadValue<ulong>(collPtrs[i] + 0x20);
                    }
                    r7.Execute();
                    for (int i = 0; i < collPtrs.Length; i++)
                    {
                        if (!collPtrs[i].IsValidVirtualAddress()) continue;
                        r7.ReadValue<int>(collPtrs[i] + 0x18, out itemCounts[i]);
                        r7.ReadValue<ulong>(collPtrs[i] + 0x20, out arrayPtrs[i]);
                    }
                }

                // ── Update cache for count-change polling ─────────────────────────────
                _cachedCollPtrs.Clear();
                _cachedCounts.Clear();
                for (int i = 0; i < collPtrs.Length; i++)
                {
                    if (collPtrs[i].IsValidVirtualAddress())
                    {
                        _cachedCollPtrs.Add(collPtrs[i]);
                        _cachedCounts.Add(itemCounts[i]);
                    }
                }

                // ── R8: InteractiveLootItem ptrs ──────────────────────────────────────
                var iLootPtrs = new List<ulong>(64);
                using (var r8 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < arrayPtrs.Length; i++)
                    {
                        if (!arrayPtrs[i].IsValidVirtualAddress() || itemCounts[i] <= 0) continue;
                        int cnt = Math.Min(itemCounts[i], 20);
                        for (int j = 0; j < cnt; j++)
                            r8.PrepareReadPtr(arrayPtrs[i] + 0x20 + (ulong)(j * 8));
                    }
                    r8.Execute();
                    for (int i = 0; i < arrayPtrs.Length; i++)
                    {
                        if (!arrayPtrs[i].IsValidVirtualAddress() || itemCounts[i] <= 0) continue;
                        int cnt = Math.Min(itemCounts[i], 20);
                        for (int j = 0; j < cnt; j++)
                        {
                            if (r8.ReadValue<ulong>(arrayPtrs[i] + 0x20 + (ulong)(j * 8), out var p) && p.IsValidVirtualAddress())
                                iLootPtrs.Add(p);
                        }
                    }
                }
                if (iLootPtrs.Count == 0) { ClearDoorFlags(doors); return; }

                // ── R9: LootItem ptrs via InteractiveLootItem.Item (+0xF0) ────────────
                var lootPtrs = new List<ulong>(iLootPtrs.Count);
                using (var r9 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < iLootPtrs.Count; i++)
                        r9.PrepareReadPtr(iLootPtrs[i] + Offsets.InteractiveLootItem.Item);
                    r9.Execute();
                    for (int i = 0; i < iLootPtrs.Count; i++)
                    {
                        if (r9.ReadValue<ulong>(iLootPtrs[i] + Offsets.InteractiveLootItem.Item, out var p) && p.IsValidVirtualAddress())
                            lootPtrs.Add(p);
                    }
                }
                if (lootPtrs.Count == 0) { ClearDoorFlags(doors); return; }

                // ── Deep scan: items inside key holders / key tools / wallets ─────────
                // For each first-level item, try reading CompoundItem.Grids.
                // Valid grids → CompoundItem (key holder) → scan its sub-items.
                var allLootPtrs = new List<ulong>(lootPtrs);

                var subGridArrPtrs = new ulong[lootPtrs.Count];
                using (var rd1 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < lootPtrs.Count; i++)
                        rd1.PrepareReadPtr(lootPtrs[i] + Offsets.CompoundItem.Grids);
                    rd1.Execute();
                    for (int i = 0; i < lootPtrs.Count; i++)
                        rd1.ReadValue<ulong>(lootPtrs[i] + Offsets.CompoundItem.Grids, out subGridArrPtrs[i]);
                }

                var subGridSlotAddrs = new List<ulong>(16);
                using (var rd2 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < subGridArrPtrs.Length; i++)
                        if (subGridArrPtrs[i].IsValidVirtualAddress())
                            rd2.PrepareReadValue<int>(subGridArrPtrs[i] + 0x18);
                    rd2.Execute();
                    for (int i = 0; i < subGridArrPtrs.Length; i++)
                    {
                        if (!subGridArrPtrs[i].IsValidVirtualAddress()) continue;
                        if (rd2.ReadValue<int>(subGridArrPtrs[i] + 0x18, out var cnt) && cnt > 0)
                            for (int g = 0; g < Math.Min(cnt, 8); g++)
                                subGridSlotAddrs.Add(subGridArrPtrs[i] + 0x20 + (ulong)(g * 8));
                    }
                }

                if (subGridSlotAddrs.Count > 0)
                {
                    var subGridPtrs = new ulong[subGridSlotAddrs.Count];
                    using (var rd3 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < subGridSlotAddrs.Count; i++)
                            rd3.PrepareReadPtr(subGridSlotAddrs[i]);
                        rd3.Execute();
                        for (int i = 0; i < subGridSlotAddrs.Count; i++)
                            rd3.ReadValue<ulong>(subGridSlotAddrs[i], out subGridPtrs[i]);
                    }

                    var subCollPtrs = new ulong[subGridPtrs.Length];
                    using (var rd4 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < subGridPtrs.Length; i++)
                            if (subGridPtrs[i].IsValidVirtualAddress())
                                rd4.PrepareReadPtr(subGridPtrs[i] + Offsets.Grids.ContainedItems);
                        rd4.Execute();
                        for (int i = 0; i < subGridPtrs.Length; i++)
                            if (subGridPtrs[i].IsValidVirtualAddress())
                                rd4.ReadValue<ulong>(subGridPtrs[i] + Offsets.Grids.ContainedItems, out subCollPtrs[i]);
                    }

                    var subItemCounts = new int[subCollPtrs.Length];
                    var subArrayPtrs  = new ulong[subCollPtrs.Length];
                    using (var rd5 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < subCollPtrs.Length; i++)
                        {
                            if (!subCollPtrs[i].IsValidVirtualAddress()) continue;
                            rd5.PrepareReadValue<int>(subCollPtrs[i] + 0x18);
                            rd5.PrepareReadValue<ulong>(subCollPtrs[i] + 0x20);
                        }
                        rd5.Execute();
                        for (int i = 0; i < subCollPtrs.Length; i++)
                        {
                            if (!subCollPtrs[i].IsValidVirtualAddress()) continue;
                            rd5.ReadValue<int>(subCollPtrs[i] + 0x18, out subItemCounts[i]);
                            rd5.ReadValue<ulong>(subCollPtrs[i] + 0x20, out subArrayPtrs[i]);
                        }
                    }

                    var subILootPtrs = new List<ulong>(32);
                    using (var rd6 = Memory.GetScatter(VmmFlags.NOCACHE))
                    {
                        for (int i = 0; i < subArrayPtrs.Length; i++)
                        {
                            if (!subArrayPtrs[i].IsValidVirtualAddress() || subItemCounts[i] <= 0) continue;
                            int cnt = Math.Min(subItemCounts[i], 20);
                            for (int j = 0; j < cnt; j++)
                                rd6.PrepareReadPtr(subArrayPtrs[i] + 0x20 + (ulong)(j * 8));
                        }
                        rd6.Execute();
                        for (int i = 0; i < subArrayPtrs.Length; i++)
                        {
                            if (!subArrayPtrs[i].IsValidVirtualAddress() || subItemCounts[i] <= 0) continue;
                            int cnt = Math.Min(subItemCounts[i], 20);
                            for (int j = 0; j < cnt; j++)
                            {
                                if (rd6.ReadValue<ulong>(subArrayPtrs[i] + 0x20 + (ulong)(j * 8), out var p) && p.IsValidVirtualAddress())
                                    subILootPtrs.Add(p);
                            }
                        }
                    }

                    if (subILootPtrs.Count > 0)
                    {
                        using var rd7 = Memory.GetScatter(VmmFlags.NOCACHE);
                        for (int i = 0; i < subILootPtrs.Count; i++)
                            rd7.PrepareReadPtr(subILootPtrs[i] + Offsets.InteractiveLootItem.Item);
                        rd7.Execute();
                        for (int i = 0; i < subILootPtrs.Count; i++)
                        {
                            if (rd7.ReadValue<ulong>(subILootPtrs[i] + Offsets.InteractiveLootItem.Item, out var p) && p.IsValidVirtualAddress())
                                allLootPtrs.Add(p);
                        }
                    }
                }

                // ── R10: Template ptrs ────────────────────────────────────────────────
                var templatePtrs = new ulong[allLootPtrs.Count];
                using (var r10 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < allLootPtrs.Count; i++)
                        r10.PrepareReadPtr(allLootPtrs[i] + Offsets.LootItem.Template);
                    r10.Execute();
                    for (int i = 0; i < allLootPtrs.Count; i++)
                        r10.ReadValue<ulong>(allLootPtrs[i] + Offsets.LootItem.Template, out templatePtrs[i]);
                }

                // ── R11: MongoID structs ──────────────────────────────────────────────
                var mongoIds = new Types.MongoID[templatePtrs.Length];
                using (var r11 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < templatePtrs.Length; i++)
                        if (templatePtrs[i].IsValidVirtualAddress())
                            r11.PrepareReadValue<Types.MongoID>(templatePtrs[i] + Offsets.ItemTemplate._id);
                    r11.Execute();
                    for (int i = 0; i < templatePtrs.Length; i++)
                        if (templatePtrs[i].IsValidVirtualAddress())
                            r11.ReadValue<Types.MongoID>(templatePtrs[i] + Offsets.ItemTemplate._id, out mongoIds[i]);
                }

                // ── R12: BSG ID strings + mark doors ─────────────────────────────────
                using (var r12 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < mongoIds.Length; i++)
                        if (mongoIds[i].StringID.IsValidVirtualAddress())
                            r12.PrepareRead(mongoIds[i].StringID + 0x14, StrBytes);
                    r12.Execute();

                    var heldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < mongoIds.Length; i++)
                    {
                        if (!mongoIds[i].StringID.IsValidVirtualAddress()) continue;
                        var id = r12.ReadString(mongoIds[i].StringID + 0x14, StrBytes, Encoding.Unicode);
                        if (!string.IsNullOrEmpty(id))
                            heldIds.Add(id);
                    }

                    foreach (var door in doors)
                        door.IsKeyHeld = door.KeyId is { } kid && heldIds.Contains(kid);
                }
            }
        }

        private static void ClearDoorFlags(IReadOnlyList<Interactables.Door> doors)
        {
            foreach (var door in doors)
                door.IsKeyHeld = false;
        }
    }
}

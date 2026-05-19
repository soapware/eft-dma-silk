// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using System.Collections.Frozen;
using VmmSharpEx.Options;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Player
{
    /// <summary>
    /// Scans the local player's Pockets, Backpack, SecuredContainer, and TacticalVest
    /// for key items every <see cref="ScanIntervalSec"/> seconds, then updates
    /// <see cref="Interactables.Door.IsKeyHeld"/> on every tracked locked door.
    /// <para>
    /// Called from DoSecondaryWork — self-throttles via internal stopwatch so it is safe
    /// to call every 100ms. All DMA reads are batched scatter operations.
    /// </para>
    /// </summary>
    internal sealed class KeyInventoryScanner
    {
        private const int ScanIntervalSec = 30;
        private const int StrBytes = 128; // 64 UTF-16 chars — covers all BSG IDs and slot names

        private readonly Stopwatch _sw = new(); // not started — first Update() fires immediately

        private static readonly FrozenSet<string> TargetSlots =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Pockets", "SecuredContainer", "Backpack", "TacticalVest" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Check timer and run scan if due. Safe to call every registration-worker tick.
        /// </summary>
        public void Update(ulong playerBase, IReadOnlyList<Interactables.Door> doors)
        {
            if (_sw.IsRunning && _sw.Elapsed.TotalSeconds < ScanIntervalSec) return;
            _sw.Restart();
            try { ScanAndMark(playerBase, doors); }
            catch { /* silently skip on transient DMA error — stale IsKeyHeld values are harmless */ }
        }

        private static void ScanAndMark(ulong playerBase, IReadOnlyList<Interactables.Door> doors)
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

                var namePtrs  = new ulong[n];
                var contPtrs  = new ulong[n]; // ContainedItem (LootItem) per slot

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

                // ── R5: Grid ptrs from the array ─────────────────────────────────────
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

                // ── R7: item count + backing-array ptr from each ItemCollection ────────
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

                // ── R8: InteractiveLootItem ptrs from each grid's backing array ────────
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
                if (iLootPtrs.Count == 0) return;

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
                if (lootPtrs.Count == 0) return;

                // ── R10: Template ptrs (LootItem + 0x60) ─────────────────────────────
                var templatePtrs = new ulong[lootPtrs.Count];
                using (var r10 = Memory.GetScatter(VmmFlags.NOCACHE))
                {
                    for (int i = 0; i < lootPtrs.Count; i++)
                        r10.PrepareReadPtr(lootPtrs[i] + Offsets.LootItem.Template);
                    r10.Execute();
                    for (int i = 0; i < lootPtrs.Count; i++)
                        r10.ReadValue<ulong>(lootPtrs[i] + Offsets.LootItem.Template, out templatePtrs[i]);
                }

                // ── R11: MongoID structs (ItemTemplate + 0xE0) ───────────────────────
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
    }
}

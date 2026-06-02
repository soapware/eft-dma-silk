// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

using eft_dma_radar.Silk.Tarkov.Features.Ballistics;

namespace eft_dma_radar.Silk.Tarkov.GameWorld.Ballistics
{
    /// <summary>
    /// Reads <c>EFT.Ballistics.BallisticsCalculator.Shots</c> each tick and accumulates
    /// per-bullet trail histories. Also snapshots the game's G1 drag table the first
    /// time a valid <c>Shot.G1</c> list is observed (feeds <see cref="G1Table"/>).
    /// </summary>
    public sealed class LiveShotTracker
    {
        private const int MaxConcurrentShots = 256;
        private const int MaxTrailPoints = 64;
        private const float MinPointDistance = 0.10f; // meters
        private const float MinPointDistanceSq = MinPointDistance * MinPointDistance;

        private readonly Dictionary<ulong, LiveShot> _shots = new(MaxConcurrentShots);
        private readonly object _sync = new();

        /// <summary>Impact event — world position where a local-player bullet stopped.</summary>
        public readonly record struct ImpactEvent(Vector3 Position, DateTime Time);

        // Tracks which shot pointers were active in the previous Update() call.
        // Single-writer (worker thread) so no lock needed.
        private readonly HashSet<ulong> _activeLastTick = new();

        // Impact events produced when a local-player shot leaves the active Shots list.
        private readonly Queue<ImpactEvent> _impacts = new();

        /// <summary>
        /// Local player's <c>Player.Base</c> address. Set each tick by
        /// <see cref="eft_dma_radar.Silk.Tarkov.Features.Ballistics.BallisticsFeature"/>
        /// so the tracker can identify which shots belong to the local player.
        /// </summary>
        public ulong LocalPlayerBase { get; set; }

        private TimeSpan _lifetime = TimeSpan.FromSeconds(4.5);
        public TimeSpan Lifetime
        {
            get => _lifetime;
            set => _lifetime = value < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : value;
        }

        /// <summary>Pulled from BallisticsCalculator each tick — strictly increasing per shot fired.</summary>
        public int LastFireIndex { get; private set; }
        /// <summary>Count of currently tracked shots after the latest <see cref="Update"/>.</summary>
        public int TrackedCount { get; private set; }
        /// <summary>True once <see cref="G1Table.SetFromGame"/> has accepted a real read.</summary>
        public bool G1Captured { get; private set; }

        public void Clear()
        {
            lock (_sync)
            {
                _shots.Clear();
                _impacts.Clear();
                TrackedCount = 0;
                LastFireIndex = 0;
                G1Captured = false;
            }
            _activeLastTick.Clear();
        }

        /// <summary>
        /// Walk the current <c>Shots</c> list and refresh trail data for every bullet.
        /// Safe to call from a dedicated worker thread (single-writer); readers should
        /// call <see cref="GetSnapshot"/> instead of touching internal state.
        /// </summary>
        public void Update(ulong gameWorldBase)
        {
            if (!gameWorldBase.IsValidVirtualAddress()) return;

            ulong calcPtr = 0;
            if (!Memory.TryReadPtr(gameWorldBase + Offsets.ClientLocalGameWorld.SharedBallisticsCalculator, out calcPtr)
                || !calcPtr.IsValidVirtualAddress())
            {
                if (!Memory.TryReadPtr(gameWorldBase + Offsets.ClientLocalGameWorld.ClientBallisticCalculator, out calcPtr)
                    || !calcPtr.IsValidVirtualAddress())
                    return;
            }

            // Read FireIndex (debug HUD).
            if (Memory.TryReadValue<int>(calcPtr + Offsets.BallisticsCalculator.FireIndex, out var fi))
                LastFireIndex = fi;

            if (!Memory.TryReadPtr(calcPtr + Offsets.BallisticsCalculator.Shots, out var shotsListObj)
                || !shotsListObj.IsValidVirtualAddress())
                return;

            // Snapshot Shot pointers (List<Shot> stores class refs — read as ulong array).
            ulong[]? shotPtrs = null;
            int count = 0;
            try
            {
                using var list = MemList<ulong>.Get(shotsListObj, false);
                count = Math.Min(list.Count, MaxConcurrentShots);
                if (count > 0)
                {
                    shotPtrs = ArrayPool<ulong>.Shared.Rent(count);
                    list.Span[..count].CopyTo(shotPtrs.AsSpan(0, count));
                }
            }
            catch { /* empty / mid-write — skip frame */ }

            var now = DateTime.UtcNow;
            var seen = new HashSet<ulong>(count);

            if (shotPtrs is not null)
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        ulong sp = shotPtrs[i];
                        if (!sp.IsValidVirtualAddress()) continue;
                        if (!ReadShotInto(sp, now, out var trail)) continue;
                        seen.Add(sp);
                        AppendTrailPoint(trail);

                        if (!G1Captured) TryCaptureG1(sp);
                    }
                }
                finally
                {
                    Array.Clear(shotPtrs, 0, count);
                    ArrayPool<ulong>.Shared.Return(shotPtrs, false);
                }
            }

            // Detect impacts: shots that were active last tick but aren't this tick.
            if (LocalPlayerBase != 0)
            {
                foreach (var id in _activeLastTick)
                {
                    if (seen.Contains(id)) continue;
                    lock (_sync)
                    {
                        if (!_shots.TryGetValue(id, out var shot) || shot.OwnerPlayer != LocalPlayerBase) continue;
                        if (shot.HitTime == DateTime.MinValue) // record only on first disappearance
                        {
                            shot.HitTime = now;
                            _impacts.Enqueue(new ImpactEvent(shot.CurrentPosition, now));
                        }
                    }
                }
            }
            _activeLastTick.Clear();
            _activeLastTick.UnionWith(seen);

            // GC stale shots: not seen this tick AND older than Lifetime.
            lock (_sync)
            {
                if (_shots.Count > 0)
                {
                    var stale = new List<ulong>();
                    foreach (var (id, shot) in _shots)
                    {
                        if (seen.Contains(id)) continue;
                        if (now - shot.LastSeen > _lifetime) stale.Add(id);
                    }
                    foreach (var id in stale) _shots.Remove(id);
                }
                TrackedCount = _shots.Count;
            }
        }

        /// <summary>
        /// Returns impact events for the local player's bullets that recently stopped.
        /// Prunes events older than 1.5 s before returning. Thread-safe.
        /// </summary>
        public ImpactEvent[] GetImpactSnapshot()
        {
            lock (_sync)
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(1.5);
                while (_impacts.Count > 0 && _impacts.Peek().Time < cutoff)
                    _impacts.Dequeue();
                return _impacts.Count == 0 ? Array.Empty<ImpactEvent>() : _impacts.ToArray();
            }
        }

        private bool ReadShotInto(ulong shotPtr, DateTime now, out LiveShot trail)
        {
            // Single-batch read of the hot Shot fields.
            if (!Memory.TryReadValue<Vector3>(shotPtr + Offsets.Shot.CurrentPosition, out var curPos)) { trail = null!; return false; }
            if (!Memory.TryReadValue<Vector3>(shotPtr + Offsets.Shot.Velocity,         out var vel))     vel = Vector3.Zero;
            if (!Memory.TryReadValue<float>(shotPtr + Offsets.Shot.TimeSinceShot,      out var age))     age = 0f;
            ulong owner = 0;
            Memory.TryReadPtr(shotPtr + Offsets.Shot.Player, out owner);

            lock (_sync)
            {
                if (!_shots.TryGetValue(shotPtr, out trail!))
                {
                    if (_shots.Count >= MaxConcurrentShots)
                    {
                        trail = null!;
                        return false;
                    }
                    trail = new LiveShot { Id = shotPtr };
                    if (Memory.TryReadValue<Vector3>(shotPtr + Offsets.Shot.StartPosition, out var startPos))
                        trail.StartPosition = startPos;
                    else
                        trail.StartPosition = curPos;
                    trail.Trail.Add(trail.StartPosition);
                    _shots[shotPtr] = trail;
                }
                trail.CurrentPosition = curPos;
                trail.Velocity = vel;
                trail.TimeSinceShot = age;
                trail.OwnerPlayer = owner;
                trail.LastSeen = now;
            }
            return true;
        }

        private void AppendTrailPoint(LiveShot trail)
        {
            lock (_sync)
            {
                var t = trail.Trail;
                if (t.Count >= MaxTrailPoints)
                {
                    // Drop oldest middle point to preserve start and current endpoints.
                    t.RemoveAt(t.Count / 2);
                }
                if (t.Count == 0 || (trail.CurrentPosition - t[^1]).LengthSquared() >= MinPointDistanceSq)
                    t.Add(trail.CurrentPosition);
            }
        }

        private void TryCaptureG1(ulong shotPtr)
        {
            // Respect the user's "Use Live G1 Table" toggle.
            if (!(SilkProgram.Config?.Ballistics?.UseGameG1Table ?? true))
                return;

            try
            {
                if (!Memory.TryReadPtr(shotPtr + Offsets.Shot.G1, out var g1ListObj)
                    || !g1ListObj.IsValidVirtualAddress())
                    return;
                using var list = MemList<G1DragModel>.Get(g1ListObj, false);
                if (list.Count < 40) return; // bad read
                G1Table.SetFromGame(list.Span);
                G1Captured = true;
                Log.WriteLine($"[Ballistics] Captured live G1 table from Shot 0x{shotPtr:X} ({list.Count} entries)");
            }
            catch { /* try again next shot */ }
        }

        /// <summary>
        /// Returns a stable snapshot of current trails for rendering. Each entry is an
        /// independent <see cref="LiveShot"/> instance with a copied Trail list.
        /// </summary>
        public LiveShot[] GetSnapshot()
        {
            lock (_sync)
            {
                if (_shots.Count == 0) return Array.Empty<LiveShot>();
                var arr = new LiveShot[_shots.Count];
                int i = 0;
                foreach (var s in _shots.Values)
                {
                    var copy = new LiveShot
                    {
                        Id = s.Id,
                        OwnerPlayer = s.OwnerPlayer,
                        LastSeen = s.LastSeen,
                        TimeSinceShot = s.TimeSinceShot,
                        Velocity = s.Velocity,
                        CurrentPosition = s.CurrentPosition,
                        StartPosition = s.StartPosition,
                        HitTime = s.HitTime,
                    };
                    copy.Trail.AddRange(s.Trail);
                    arr[i++] = copy;
                }
                return arr;
            }
        }

        /// <summary>
        /// Executes a callback for every active shot trail under the internal sync lock.
        /// Completely zero-allocation alternative to GetSnapshot().
        /// </summary>
        public void DrawActiveShots<TState>(TState state, Action<LiveShot, TState> drawAction)
        {
            lock (_sync)
            {
                foreach (var shot in _shots.Values)
                {
                    drawAction(shot, state);
                }
            }
        }

        /// <summary>
        /// Processes and executes a callback for every active bullet impact marker under the lock.
        /// Removes old events older than 1.5 seconds and passes age to the drawing delegate.
        /// Completely zero-allocation alternative to GetImpactSnapshot().
        /// </summary>
        public void DrawImpactMarkers<TState>(TState state, Action<ImpactEvent, float, TState> drawAction)
        {
            lock (_sync)
            {
                var now = DateTime.UtcNow;
                var cutoff = now - TimeSpan.FromSeconds(1.5);
                while (_impacts.Count > 0 && _impacts.Peek().Time < cutoff)
                    _impacts.Dequeue();

                foreach (var impact in _impacts)
                {
                    float age = (float)(now - impact.Time).TotalSeconds;
                    drawAction(impact, age, state);
                }
            }
        }
    }
}

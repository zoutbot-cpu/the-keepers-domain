using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Grid
{
    /// A worker BuilderJobBoard can assign jobs to. Deliberately minimal and
    /// grid-only, so the board never needs to know about impling-specific
    /// concerns (state machines, lair positions, ...) — just "where is it"
    /// and, for troubleshooting logs, "what do I call it."
    public interface IJobWorker
    {
        Vector3 Position { get; }
        string Name { get; }
    }

    /// The kind of job a worker was just assigned by TryClaimNearestJob, so
    /// it knows which action to perform once it arrives.
    public enum JobKind
    {
        Dig,
        Claim,
        Reinforce,
        Build
    }

    public readonly struct JobInfo
    {
        public readonly Vector2Int Coord;
        public readonly int ClaimCount;
        public readonly int MaxWorkers;
        public readonly bool IsPending;
        public readonly float PendingSecondsRemaining;

        public JobInfo(Vector2Int coord, int claimCount, int maxWorkers, bool isPending, float pendingSecondsRemaining)
        {
            Coord = coord;
            ClaimCount = claimCount;
            MaxWorkers = maxWorkers;
            IsPending = isPending;
            PendingSecondsRemaining = pendingSecondsRemaining;
        }
    }

    /// Queues and assigns worker jobs so ImplingAgent never has to know about
    /// DungeonGrid directly — it only ever talks to this board. Handles four
    /// job kinds (dig, claim, reinforce, build) and is named generically
    /// since it's expected to grow more (hauling, ...) and to sit alongside
    /// other, separate job boards later. Up to MaxWorkersPerJob implings can
    /// share a dig tile. Freshly-queued dig tiles sit in a short grace period
    /// before implings can claim them, so a mis-tap can still be canceled.
    public class BuilderJobBoard : MonoBehaviour
    {
        private const int MaxWorkersPerJob = 2;
        private const float ClaimDelaySeconds = 1f;

        [SerializeField] private DungeonGrid _grid;

        private readonly List<Vector2Int> _openJobs = new List<Vector2Int>();
        private readonly Dictionary<Vector2Int, float> _pendingJobs = new Dictionary<Vector2Int, float>();
        private readonly Dictionary<Vector2Int, int> _claimCounts = new Dictionary<Vector2Int, int>();
        private readonly List<Vector2Int> _readyScratch = new List<Vector2Int>();
        private readonly List<IJobWorker> _availableWorkers = new List<IJobWorker>();

        // Claim jobs (queued whenever a dig finishes as unclaimed floor)
        // are single-worker and have no grace period — there's no tap to
        // mis-cancel, they're generated automatically. Value = is a worker
        // already on their way to claim it.
        private readonly Dictionary<Vector2Int, bool> _claimJobs = new Dictionary<Vector2Int, bool>();
        private readonly List<Vector2Int> _claimScratch = new List<Vector2Int>();

        // Reinforce jobs are player-tap-initiated like dig jobs (same grace
        // period so a mis-tap can still be canceled) but single-worker like
        // claim jobs (reinforcing is a timed action, not a shared HP pool).
        private readonly List<Vector2Int> _openReinforceJobs = new List<Vector2Int>();
        private readonly Dictionary<Vector2Int, float> _pendingReinforceJobs = new Dictionary<Vector2Int, float>();
        private readonly HashSet<Vector2Int> _assignedReinforceJobs = new HashSet<Vector2Int>();

        // Build jobs (walling off a Claimed Floor tile) are shaped exactly
        // like reinforce jobs — player-tap-initiated with the same grace
        // period, single-worker timed action.
        private readonly List<Vector2Int> _openBuildJobs = new List<Vector2Int>();
        private readonly Dictionary<Vector2Int, float> _pendingBuildJobs = new Dictionary<Vector2Int, float>();
        private readonly HashSet<Vector2Int> _assignedBuildJobs = new HashSet<Vector2Int>();

        // Jobs are tried in this order — whichever kind sorts first is fully
        // depleted (nearest job within that kind) before the next kind is
        // even searched. Defaults to Dig, then Reinforce and Build (all
        // three player-tap-initiated, so presumably wanted sooner), then
        // Claim (automatic background chore). Exposed as a plain setter so
        // a future priority UI has something to call without touching the
        // search logic itself (same placeholder pattern as
        // TileInteractionController.SetSquareModeToggle).
        private JobKind[] _jobPriorityOrder = { JobKind.Dig, JobKind.Reinforce, JobKind.Build, JobKind.Claim };

        // Gates only the Dig case in TryClaimFromKind — implings already
        // mid-dig keep going, and Reinforce/Build/Claim are unaffected, so
        // "pause the digging queue" doesn't stop the whole board.
        private bool _areDigJobsPaused;

        // Auto-reinforce: off by default (see BottomMenuBar's Impling menu
        // toggle). While on, ScanForAutoReinforceCandidates periodically
        // queues a reinforce job on every un-reinforced Rock tile bordering
        // already-Claimed territory — the same "dungeon wall" candidacy
        // TryClaimClaimJob already uses for claim jobs — so building up a
        // perimeter doesn't require tapping every wall tile by hand. Scans
        // on an interval rather than every frame (fine at prototype scale);
        // RequestReinforce itself no-ops on anything already queued or
        // ineligible, so re-scanning a mostly-settled dungeon is cheap.
        private const float AutoReinforceScanInterval = 1f;
        private bool _isAutoReinforceEnabled;
        private float _nextAutoReinforceScanTime;

        public void Initialize(DungeonGrid grid)
        {
            _grid = grid;
            _grid.DigRequested += OnDigRequested;
            _grid.DigCanceled += OnDigCanceled;
            _grid.ReinforceRequested += OnReinforceRequested;
            _grid.ReinforceCanceled += OnReinforceCanceled;
            _grid.BuildRequested += OnBuildRequested;
            _grid.BuildCanceled += OnBuildCanceled;
            _grid.FloorNeedsClaim += OnFloorNeedsClaim;
        }

        public void SetJobPriorityOrder(params JobKind[] order)
        {
            _jobPriorityOrder = order;
        }

        /// A copy, not the live array — callers (e.g. BottomMenuBar seeding
        /// its reorderable list from the board's actual current order,
        /// rather than duplicating its own hardcoded default that could
        /// silently drift out of sync with this one) shouldn't be able to
        /// mutate priority ordering except through SetJobPriorityOrder.
        public JobKind[] GetJobPriorityOrder()
        {
            return (JobKind[])_jobPriorityOrder.Clone();
        }

        public void SetDigJobsPaused(bool isPaused)
        {
            _areDigJobsPaused = isPaused;
        }

        public void SetAutoReinforceEnabled(bool isEnabled)
        {
            _isAutoReinforceEnabled = isEnabled;
            _nextAutoReinforceScanTime = Time.time;
        }

        private void Update()
        {
            PromoteReadyPendingJobs();

            if (_isAutoReinforceEnabled && Time.time >= _nextAutoReinforceScanTime)
            {
                _nextAutoReinforceScanTime = Time.time + AutoReinforceScanInterval;
                ScanForAutoReinforceCandidates();
            }
        }

        /// Queues a reinforce job (via DungeonGrid.RequestReinforce, which
        /// safely no-ops on anything not an eligible bare Rock tile) on
        /// every Rock tile bordering already-Claimed floor — see the field
        /// comment on _isAutoReinforceEnabled for why "borders Claimed" is
        /// the candidacy rule.
        private void ScanForAutoReinforceCandidates()
        {
            for (int x = 0; x < _grid.Width; x++)
            {
                for (int y = 0; y < _grid.Height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    if (_grid.GetTile(coord).Type == TileType.Rock && _grid.BordersClaimedTile(coord))
                    {
                        _grid.RequestReinforce(coord);
                    }
                }
            }
        }

        /// Moves any pending job whose grace period has elapsed into its open
        /// list. Called from Update() for the steady-state case, but also
        /// from the start of TryClaimNearestJob so a job search always sees
        /// up-to-date results regardless of MonoBehaviour Update() order —
        /// BuilderJobBoard and ImplingAgent are separate components with no
        /// guaranteed execution order, so without this an impling's search
        /// could run in the same frame a tile's grace period expired but
        /// before this board's own Update() had promoted it yet, missing a
        /// job that was actually already available.
        private void PromoteReadyPendingJobs()
        {
            PromotePending(_pendingJobs, _openJobs, "Dig");
            PromotePending(_pendingReinforceJobs, _openReinforceJobs, "Reinforce");
            PromotePending(_pendingBuildJobs, _openBuildJobs, "Build");
        }

        private void PromotePending(Dictionary<Vector2Int, float> pending, List<Vector2Int> open, string jobLabel)
        {
            if (pending.Count == 0)
            {
                return;
            }

            _readyScratch.Clear();
            foreach (var entry in pending)
            {
                if (Time.time >= entry.Value)
                {
                    _readyScratch.Add(entry.Key);
                }
            }

            foreach (var coord in _readyScratch)
            {
                pending.Remove(coord);
                if (!open.Contains(coord))
                {
                    open.Add(coord);
                }

                GameplayLog.Write($"{jobLabel} job promoted pending->open: {Coord(coord)}");
            }
        }

        private void OnDigRequested(Vector2Int coord)
        {
            if (!_pendingJobs.ContainsKey(coord) && !_openJobs.Contains(coord) && GetClaimCount(coord) < MaxWorkersPerJob)
            {
                _pendingJobs[coord] = Time.time + ClaimDelaySeconds;
                GameplayLog.Write($"Job queued (pending {ClaimDelaySeconds}s): {Coord(coord)}");
            }
        }

        private void OnDigCanceled(Vector2Int coord)
        {
            _pendingJobs.Remove(coord);
            _openJobs.Remove(coord);
        }

        private void OnReinforceRequested(Vector2Int coord)
        {
            if (!_pendingReinforceJobs.ContainsKey(coord) && !_openReinforceJobs.Contains(coord))
            {
                _pendingReinforceJobs[coord] = Time.time + ClaimDelaySeconds;
                GameplayLog.Write($"Reinforce job queued (pending {ClaimDelaySeconds}s): {Coord(coord)}");
            }
        }

        private void OnReinforceCanceled(Vector2Int coord)
        {
            _pendingReinforceJobs.Remove(coord);
            _openReinforceJobs.Remove(coord);
        }

        private void OnBuildRequested(Vector2Int coord)
        {
            if (!_pendingBuildJobs.ContainsKey(coord) && !_openBuildJobs.Contains(coord))
            {
                _pendingBuildJobs[coord] = Time.time + ClaimDelaySeconds;
                GameplayLog.Write($"Build job queued (pending {ClaimDelaySeconds}s): {Coord(coord)}");
            }
        }

        private void OnBuildCanceled(Vector2Int coord)
        {
            _pendingBuildJobs.Remove(coord);
            _openBuildJobs.Remove(coord);
        }

        private void OnFloorNeedsClaim(Vector2Int coord)
        {
            if (!_claimJobs.ContainsKey(coord))
            {
                _claimJobs[coord] = false;
                GameplayLog.Write($"Claim job queued: {Coord(coord)}");
            }
        }

        /// Whether a tap could still cancel this tile's queue — true while it's
        /// waiting out its grace period, or open but nobody has claimed it yet.
        public bool CanCancel(Vector2Int coord)
        {
            return _pendingJobs.ContainsKey(coord) || (GetClaimCount(coord) == 0 && _openJobs.Contains(coord));
        }

        /// Cancels a job before any implings have committed to it. Returns
        /// false (no-op) once someone has already claimed the tile.
        public bool CancelJob(Vector2Int coord)
        {
            if (!CanCancel(coord))
            {
                return false;
            }

            _pendingJobs.Remove(coord);
            _openJobs.Remove(coord);
            GameplayLog.Write($"Job canceled: {Coord(coord)}");
            return true;
        }

        /// Whether a tap could still cancel this tile's reinforce queue —
        /// true while it's waiting out its grace period, or open but no
        /// impling has been assigned to it yet (assigned reinforce jobs are
        /// removed from _openReinforceJobs, so this doubles as the "not yet
        /// assigned" check with no separate count needed).
        public bool CanCancelReinforce(Vector2Int coord)
        {
            return _pendingReinforceJobs.ContainsKey(coord) || _openReinforceJobs.Contains(coord);
        }

        /// Cancels a reinforce job before any impling has committed to it.
        /// Returns false (no-op) once someone has already been assigned.
        public bool CancelReinforceJob(Vector2Int coord)
        {
            if (!CanCancelReinforce(coord))
            {
                return false;
            }

            _pendingReinforceJobs.Remove(coord);
            _openReinforceJobs.Remove(coord);
            GameplayLog.Write($"Reinforce job canceled: {Coord(coord)}");
            return true;
        }

        /// Whether a tap could still cancel this tile's build queue — same
        /// pending-or-open-and-unassigned rule as CanCancelReinforce.
        public bool CanCancelBuild(Vector2Int coord)
        {
            return _pendingBuildJobs.ContainsKey(coord) || _openBuildJobs.Contains(coord);
        }

        /// Cancels a build job before any impling has committed to it.
        /// Returns false (no-op) once someone has already been assigned.
        public bool CancelBuildJob(Vector2Int coord)
        {
            if (!CanCancelBuild(coord))
            {
                return false;
            }

            _pendingBuildJobs.Remove(coord);
            _openBuildJobs.Remove(coord);
            GameplayLog.Write($"Build job canceled: {Coord(coord)}");
            return true;
        }

        /// Marks a worker as idle and eligible to be considered "the nearest
        /// available worker" for job assignment, or removes it once it's busy.
        /// Workers are expected to call this themselves as they enter/leave
        /// their idle states — the board never inspects a worker's state.
        public void SetWorkerAvailable(IJobWorker worker, bool isAvailable)
        {
            if (isAvailable)
            {
                if (!_availableWorkers.Contains(worker))
                {
                    _availableWorkers.Add(worker);
                }
            }
            else
            {
                _availableWorkers.Remove(worker);
            }
        }

        /// Tries each kind in _jobPriorityOrder in turn, only moving on to
        /// the next kind once the current one comes up empty — so the
        /// highest-priority kind with any candidate is always fully
        /// depleted (nearest-job-first within it) before a lower-priority
        /// kind is even considered. See TryClaimDigJob/TryClaimClaimJob/
        /// TryClaimReinforceJob for how each kind actually ranks and assigns
        /// its candidates.
        public bool TryClaimNearestJob(IJobWorker requester, out Vector2Int coord, out int slotIndex, out Vector2Int approachCoord, out JobKind kind)
        {
            PromoteReadyPendingJobs();

            var requesterDistances = _grid.GetReachableFloorDistances(_grid.WorldToGrid(requester.Position));
            var otherWorkerDistances = GetOtherAvailableWorkersDistances(requester);

            foreach (var candidateKind in _jobPriorityOrder)
            {
                if (TryClaimFromKind(candidateKind, requester, requesterDistances, otherWorkerDistances, out coord, out slotIndex, out approachCoord))
                {
                    kind = candidateKind;
                    return true;
                }
            }

            coord = default;
            slotIndex = -1;
            approachCoord = default;
            kind = default;
            return false;
        }

        private bool TryClaimFromKind(JobKind kind, IJobWorker requester, Dictionary<Vector2Int, int> requesterDistances,
            List<(IJobWorker Worker, Dictionary<Vector2Int, int> Distances)> otherWorkerDistances,
            out Vector2Int coord, out int slotIndex, out Vector2Int approachCoord)
        {
            switch (kind)
            {
                case JobKind.Dig:
                    if (_areDigJobsPaused)
                    {
                        coord = default;
                        slotIndex = -1;
                        approachCoord = default;
                        return false;
                    }

                    return TryClaimDigJob(requester, requesterDistances, otherWorkerDistances, out coord, out slotIndex, out approachCoord);
                case JobKind.Reinforce:
                    return TryClaimReinforceJob(requester, requesterDistances, otherWorkerDistances, out coord, out slotIndex, out approachCoord);
                case JobKind.Build:
                    return TryClaimBuildJob(requester, requesterDistances, otherWorkerDistances, out coord, out slotIndex, out approachCoord);
                default:
                    return TryClaimClaimJob(requester, requesterDistances, otherWorkerDistances, out coord, out slotIndex, out approachCoord);
            }
        }

        /// Picks the nearest (by actual walking distance, not straight line —
        /// see DungeonGrid.GetReachableFloorDistances) open dig job that's
        /// reachable from requester AND for which requester wouldn't be
        /// bumped out by closer idle workers filling every remaining slot
        /// first (jobs go to whichever imps are nearest, not whichever imps
        /// happen to ask first — but a job with room for 2 still lets a
        /// second, farther-away imp in rather than blocking it outright).
        /// Unreachable candidates are skipped (and flagged on the grid for
        /// the player to see) rather than assigned to a worker that would
        /// have nowhere to stand. approachCoord is the specific
        /// verified-connected neighbor to walk to, so the caller doesn't have
        /// to re-derive it (and risk picking a floor tile from a different,
        /// unconnected pocket).
        private bool TryClaimDigJob(IJobWorker requester, Dictionary<Vector2Int, int> requesterDistances,
            List<(IJobWorker Worker, Dictionary<Vector2Int, int> Distances)> otherWorkerDistances,
            out Vector2Int coord, out int slotIndex, out Vector2Int approachCoord)
        {
            int bestIndex = -1;
            int bestTravelDistance = int.MaxValue;
            var bestApproach = default(Vector2Int);

            for (int i = 0; i < _openJobs.Count; i++)
            {
                var candidate = _openJobs[i];
                var isReachable = TryGetTravelDistanceToJob(candidate, requesterDistances, out var approach, out var travelDistance);
                _grid.SetUnreachable(candidate, !isReachable);

                if (!isReachable)
                {
                    continue;
                }

                var remainingSlots = MaxWorkersPerJob - GetClaimCount(candidate);
                if (HasEnoughCloserWorkersToFillSlots(candidate, otherWorkerDistances, travelDistance, remainingSlots))
                {
                    continue;
                }

                if (travelDistance < bestTravelDistance)
                {
                    bestTravelDistance = travelDistance;
                    bestIndex = i;
                    bestApproach = approach;
                }
            }

            if (bestIndex < 0)
            {
                coord = default;
                slotIndex = -1;
                approachCoord = default;
                return false;
            }

            coord = _openJobs[bestIndex];
            approachCoord = bestApproach;
            slotIndex = GetClaimCount(coord);
            _claimCounts[coord] = slotIndex + 1;

            if (slotIndex + 1 >= MaxWorkersPerJob)
            {
                _openJobs.RemoveAt(bestIndex);
            }

            GameplayLog.Write($"Dig job claimed: {Coord(coord)} slot {slotIndex} by {requester.Name} (travel dist {bestTravelDistance}, approach {Coord(approachCoord)})");
            return true;
        }

        /// Same nearest-by-travel-distance idea as TryClaimDigJob, but for
        /// single-worker claim jobs: no slot counting needed since one
        /// worker fills the only slot, so a job is just removed from
        /// candidacy the instant it's assigned rather than tracked by count.
        /// Also only offers jobs that border an already-Claimed tile
        /// (DungeonGrid.BordersClaimedTile) — territory grows outward one
        /// ring at a time from what's already claimed, rather than jumping
        /// to claim any reachable dug-out tile regardless of adjacency. A
        /// job with no claimed neighbor yet just stays uncandidate until one
        /// of its neighbors gets claimed, at which point this same filter
        /// picks it up on the next search with no extra bookkeeping needed.
        private bool TryClaimClaimJob(IJobWorker requester, Dictionary<Vector2Int, int> requesterDistances,
            List<(IJobWorker Worker, Dictionary<Vector2Int, int> Distances)> otherWorkerDistances,
            out Vector2Int coord, out int slotIndex, out Vector2Int approachCoord)
        {
            _claimScratch.Clear();
            foreach (var entry in _claimJobs)
            {
                if (!entry.Value && _grid.BordersClaimedTile(entry.Key))
                {
                    _claimScratch.Add(entry.Key);
                }
            }

            var found = false;
            var bestTravelDistance = int.MaxValue;
            var bestCoord = default(Vector2Int);
            var bestApproach = default(Vector2Int);

            foreach (var candidate in _claimScratch)
            {
                if (!TryGetTravelDistanceToJob(candidate, requesterDistances, out var approach, out var travelDistance))
                {
                    continue;
                }

                if (HasEnoughCloserWorkersToFillSlots(candidate, otherWorkerDistances, travelDistance, remainingSlots: 1))
                {
                    continue;
                }

                if (travelDistance < bestTravelDistance)
                {
                    bestTravelDistance = travelDistance;
                    bestCoord = candidate;
                    bestApproach = approach;
                    found = true;
                }
            }

            if (!found)
            {
                coord = default;
                slotIndex = -1;
                approachCoord = default;
                return false;
            }

            coord = bestCoord;
            approachCoord = bestApproach;
            slotIndex = 0;
            _claimJobs[coord] = true;

            GameplayLog.Write($"Claim job claimed: {Coord(coord)} by {requester.Name} (travel dist {bestTravelDistance}, approach {Coord(approachCoord)})");
            return true;
        }

        /// Same nearest-by-travel-distance idea as TryClaimDigJob (a Rock
        /// tile, approached from an adjacent floor tile) but single-worker
        /// like TryClaimClaimJob — reinforcing is a timed action, not a
        /// shared HP pool, so there's nothing for a second worker to
        /// meaningfully add.
        private bool TryClaimReinforceJob(IJobWorker requester, Dictionary<Vector2Int, int> requesterDistances,
            List<(IJobWorker Worker, Dictionary<Vector2Int, int> Distances)> otherWorkerDistances,
            out Vector2Int coord, out int slotIndex, out Vector2Int approachCoord)
        {
            int bestIndex = -1;
            int bestTravelDistance = int.MaxValue;
            var bestApproach = default(Vector2Int);

            for (int i = 0; i < _openReinforceJobs.Count; i++)
            {
                var candidate = _openReinforceJobs[i];
                var isReachable = TryGetTravelDistanceToJob(candidate, requesterDistances, out var approach, out var travelDistance);
                _grid.SetUnreachable(candidate, !isReachable);

                if (!isReachable)
                {
                    continue;
                }

                if (HasEnoughCloserWorkersToFillSlots(candidate, otherWorkerDistances, travelDistance, remainingSlots: 1))
                {
                    continue;
                }

                if (travelDistance < bestTravelDistance)
                {
                    bestTravelDistance = travelDistance;
                    bestIndex = i;
                    bestApproach = approach;
                }
            }

            if (bestIndex < 0)
            {
                coord = default;
                slotIndex = -1;
                approachCoord = default;
                return false;
            }

            coord = _openReinforceJobs[bestIndex];
            approachCoord = bestApproach;
            slotIndex = 0;
            _openReinforceJobs.RemoveAt(bestIndex);
            _assignedReinforceJobs.Add(coord);

            GameplayLog.Write($"Reinforce job claimed: {Coord(coord)} by {requester.Name} (travel dist {bestTravelDistance}, approach {Coord(approachCoord)})");
            return true;
        }

        /// Same shape as TryClaimReinforceJob, but the candidate tile is
        /// already-walkable Floor (about to become Rock) rather than Rock —
        /// same TryGetTravelDistanceToJob neighbor-approach logic applies
        /// either way, since it only cares whether some neighbor of the job
        /// coord is in the requester's reachable set, not what the job
        /// coord's own tile type is.
        private bool TryClaimBuildJob(IJobWorker requester, Dictionary<Vector2Int, int> requesterDistances,
            List<(IJobWorker Worker, Dictionary<Vector2Int, int> Distances)> otherWorkerDistances,
            out Vector2Int coord, out int slotIndex, out Vector2Int approachCoord)
        {
            int bestIndex = -1;
            int bestTravelDistance = int.MaxValue;
            var bestApproach = default(Vector2Int);

            for (int i = 0; i < _openBuildJobs.Count; i++)
            {
                var candidate = _openBuildJobs[i];
                var isReachable = TryGetTravelDistanceToJob(candidate, requesterDistances, out var approach, out var travelDistance);
                _grid.SetUnreachable(candidate, !isReachable);

                if (!isReachable)
                {
                    continue;
                }

                if (HasEnoughCloserWorkersToFillSlots(candidate, otherWorkerDistances, travelDistance, remainingSlots: 1))
                {
                    continue;
                }

                if (travelDistance < bestTravelDistance)
                {
                    bestTravelDistance = travelDistance;
                    bestIndex = i;
                    bestApproach = approach;
                }
            }

            if (bestIndex < 0)
            {
                coord = default;
                slotIndex = -1;
                approachCoord = default;
                return false;
            }

            coord = _openBuildJobs[bestIndex];
            approachCoord = bestApproach;
            slotIndex = 0;
            _openBuildJobs.RemoveAt(bestIndex);
            _assignedBuildJobs.Add(coord);

            GameplayLog.Write($"Build job claimed: {Coord(coord)} by {requester.Name} (travel dist {bestTravelDistance}, approach {Coord(approachCoord)})");
            return true;
        }

        /// One flood-fill per currently-available worker (other than
        /// requester), computed once per TryClaimNearestJob call and reused
        /// across every job candidate in that call — cheap at prototype scale
        /// (a handful of implings, a grid tens of tiles across); would need
        /// caching or a cheaper heuristic if either grows a lot.
        private List<(IJobWorker Worker, Dictionary<Vector2Int, int> Distances)> GetOtherAvailableWorkersDistances(IJobWorker requester)
        {
            var result = new List<(IJobWorker, Dictionary<Vector2Int, int>)>(_availableWorkers.Count);
            foreach (var worker in _availableWorkers)
            {
                if (worker == requester)
                {
                    continue;
                }

                result.Add((worker, _grid.GetReachableFloorDistances(_grid.WorldToGrid(worker.Position))));
            }

            return result;
        }

        /// A job can hold multiple workers (see MaxWorkersPerJob), so being
        /// beaten by a closer worker shouldn't exclude requester outright —
        /// only once enough closer workers exist to fill every remaining
        /// slot first. Without this, "nearest imp wins" degenerated into
        /// "only the single nearest imp may ever take this job," starving
        /// the second slot the two-workers-per-tile feature relies on.
        private bool HasEnoughCloserWorkersToFillSlots(Vector2Int jobCoord, List<(IJobWorker Worker, Dictionary<Vector2Int, int> Distances)> otherWorkerDistances, int requesterTravelDistance, int remainingSlots)
        {
            if (remainingSlots <= 0)
            {
                return true;
            }

            var closerWorkerCount = 0;

            foreach (var entry in otherWorkerDistances)
            {
                if (TryGetTravelDistanceToJob(jobCoord, entry.Distances, out _, out var otherDistance) && otherDistance < requesterTravelDistance)
                {
                    closerWorkerCount++;
                    if (closerWorkerCount >= remainingSlots)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetTravelDistanceToJob(Vector2Int jobCoord, Dictionary<Vector2Int, int> distances, out Vector2Int approach, out int travelDistance)
        {
            var found = false;
            approach = default;
            travelDistance = int.MaxValue;

            foreach (var offset in GridDirections.Cardinal)
            {
                var neighbor = jobCoord + offset;
                if (distances.TryGetValue(neighbor, out var distance) && (!found || distance < travelDistance))
                {
                    found = true;
                    travelDistance = distance;
                    approach = neighbor;
                }
            }

            return found;
        }

        /// Whether coord is still an active Rock tile worth digging. Lets a
        /// worker sharing a job with another notice the tile is already gone
        /// — dug out by the other worker's hit — without waiting for its own
        /// next hit tick, which could otherwise lag up to a full hit interval
        /// behind reality.
        public bool IsStillDiggable(Vector2Int coord)
        {
            return _grid.GetTile(coord).Type == TileType.Rock;
        }

        /// Applies one hit of dig damage. Returns true once the tile is fully
        /// dug out — by this hit or, if another worker shares the job, by theirs.
        /// resourceType/resourceAmount pass straight through from
        /// DungeonGrid.ApplyDigDamage — None/0 for a plain wall, whatever a
        /// resource wall yielded from this hit otherwise. A plain-Digging
        /// impling can just discard them; a Mining one credits them to its
        /// inventory (see ImplingAgent.TickMining).
        public bool ApplyHit(Vector2Int coord, int damage, out ResourceType resourceType, out int resourceAmount)
        {
            var destroyed = _grid.ApplyDigDamage(coord, damage, out resourceType, out resourceAmount);
            if (destroyed)
            {
                _claimCounts.Remove(coord);
                _openJobs.Remove(coord);
                GameplayLog.Write($"Job destroyed by hit: {Coord(coord)}");
            }

            return destroyed;
        }

        /// Whether coord is still an unclaimed Floor tile worth claiming —
        /// mirrors IsStillDiggable's role for dig jobs, though in practice a
        /// claim job's single worker slot means there's nothing else that
        /// could have claimed it out from under them.
        public bool IsStillClaimable(Vector2Int coord)
        {
            return _grid.GetTile(coord) is { Type: TileType.Floor, Ownership: TileOwnership.Unclaimed };
        }

        /// Finalizes a claim job once an impling has spent its claim
        /// duration standing there — marks the tile Claimed on the grid and
        /// drops it from tracking, mirroring ApplyHit's role for dig jobs.
        public void ApplyClaim(Vector2Int coord)
        {
            _grid.ClaimTile(coord);
            _claimJobs.Remove(coord);
            GameplayLog.Write($"Claim job completed: {Coord(coord)}");
        }

        /// Snapshot of every tracked claim job (open or already assigned),
        /// sorted by coordinate — for debug/inspection UI only.
        public List<Vector2Int> GetClaimJobs()
        {
            var coords = new List<Vector2Int>(_claimJobs.Keys);
            coords.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            return coords;
        }

        public bool IsClaimJobAssigned(Vector2Int coord)
        {
            return _claimJobs.TryGetValue(coord, out var isAssigned) && isAssigned;
        }

        /// Whether coord is still an un-reinforced Rock tile with its
        /// reinforce job still active — lets a worker walking over notice
        /// the job was canceled mid-walk (e.g. a mis-tap the grace period
        /// didn't catch in time) instead of reinforcing a tile nobody
        /// wanted reinforced anymore.
        public bool IsStillReinforceable(Vector2Int coord)
        {
            var tile = _grid.GetTile(coord);
            return tile.Type == TileType.Rock && tile.IsQueuedForReinforce && !tile.IsReinforced;
        }

        /// Finalizes a reinforce job once an impling has spent its reinforce
        /// duration standing there — marks the tile reinforced on the grid
        /// and drops it from tracking, mirroring ApplyHit's role for dig jobs.
        public void ApplyReinforce(Vector2Int coord)
        {
            _grid.CompleteReinforce(coord);
            _assignedReinforceJobs.Remove(coord);
            GameplayLog.Write($"Reinforce job completed: {Coord(coord)}");
        }

        /// Snapshot of every tracked reinforce job (pending, open, or
        /// already assigned), sorted by coordinate — for debug/inspection
        /// UI only.
        public List<JobInfo> GetReinforceJobs()
        {
            var coords = new HashSet<Vector2Int>(_pendingReinforceJobs.Keys);
            coords.UnionWith(_openReinforceJobs);
            coords.UnionWith(_assignedReinforceJobs);

            var jobs = new List<JobInfo>(coords.Count);
            foreach (var coord in coords)
            {
                var isPending = _pendingReinforceJobs.TryGetValue(coord, out var readyAtTime);
                var secondsRemaining = isPending ? Mathf.Max(0f, readyAtTime - Time.time) : 0f;
                var claimCount = _assignedReinforceJobs.Contains(coord) ? 1 : 0;
                jobs.Add(new JobInfo(coord, claimCount, maxWorkers: 1, isPending, secondsRemaining));
            }

            jobs.Sort((a, b) => a.Coord.x != b.Coord.x ? a.Coord.x.CompareTo(b.Coord.x) : a.Coord.y.CompareTo(b.Coord.y));
            return jobs;
        }

        /// Whether coord is still a Claimed Floor tile with its build job
        /// still active — lets a worker walking over notice the job was
        /// canceled mid-walk instead of walling off a tile nobody wanted
        /// walled anymore. Named distinctly from DungeonGrid.IsBuildable
        /// (Lair-placement eligibility) to avoid confusion — this is about
        /// whether a queued build job is still valid, not about the Lair
        /// footprint rule.
        public bool IsStillConstructable(Vector2Int coord)
        {
            var tile = _grid.GetTile(coord);
            return tile.Type == TileType.Floor && tile.IsQueuedForBuild;
        }

        /// Finalizes a build job once an impling has spent its build
        /// duration standing there — turns the tile back into Rock on the
        /// grid and drops it from tracking, mirroring ApplyHit's role for
        /// dig jobs.
        public void ApplyBuild(Vector2Int coord)
        {
            _grid.CompleteBuild(coord);
            _assignedBuildJobs.Remove(coord);
            GameplayLog.Write($"Build job completed: {Coord(coord)}");
        }

        /// Snapshot of every tracked build job (pending, open, or already
        /// assigned), sorted by coordinate — for debug/inspection UI only.
        public List<JobInfo> GetBuildJobs()
        {
            var coords = new HashSet<Vector2Int>(_pendingBuildJobs.Keys);
            coords.UnionWith(_openBuildJobs);
            coords.UnionWith(_assignedBuildJobs);

            var jobs = new List<JobInfo>(coords.Count);
            foreach (var coord in coords)
            {
                var isPending = _pendingBuildJobs.TryGetValue(coord, out var readyAtTime);
                var secondsRemaining = isPending ? Mathf.Max(0f, readyAtTime - Time.time) : 0f;
                var claimCount = _assignedBuildJobs.Contains(coord) ? 1 : 0;
                jobs.Add(new JobInfo(coord, claimCount, maxWorkers: 1, isPending, secondsRemaining));
            }

            jobs.Sort((a, b) => a.Coord.x != b.Coord.x ? a.Coord.x.CompareTo(b.Coord.x) : a.Coord.y.CompareTo(b.Coord.y));
            return jobs;
        }

        public void ReleaseJob(Vector2Int coord)
        {
            if (_claimCounts.TryGetValue(coord, out var count) && count > 0)
            {
                count -= 1;
                if (count <= 0)
                {
                    _claimCounts.Remove(coord);
                }
                else
                {
                    _claimCounts[coord] = count;
                }
            }

            if (!_openJobs.Contains(coord))
            {
                _openJobs.Add(coord);
            }
        }

        /// Snapshot of every tracked job (pending, open, or already claimed),
        /// sorted by coordinate — for debug/inspection UI only.
        public List<JobInfo> GetJobs()
        {
            var coords = new HashSet<Vector2Int>(_pendingJobs.Keys);
            coords.UnionWith(_openJobs);
            coords.UnionWith(_claimCounts.Keys);

            var jobs = new List<JobInfo>(coords.Count);
            foreach (var coord in coords)
            {
                var isPending = _pendingJobs.TryGetValue(coord, out var readyAtTime);
                var secondsRemaining = isPending ? Mathf.Max(0f, readyAtTime - Time.time) : 0f;
                jobs.Add(new JobInfo(coord, GetClaimCount(coord), MaxWorkersPerJob, isPending, secondsRemaining));
            }

            jobs.Sort((a, b) => a.Coord.x != b.Coord.x ? a.Coord.x.CompareTo(b.Coord.x) : a.Coord.y.CompareTo(b.Coord.y));
            return jobs;
        }

        private int GetClaimCount(Vector2Int coord) => _claimCounts.TryGetValue(coord, out var count) ? count : 0;

        private static string Coord(Vector2Int coord) => $"({coord.x},{coord.y})";
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.DebugUI;
using KeepersDomain.Creatures;

namespace KeepersDomain.Implings
{
    public enum ImplingState
    {
        SeekingJob,
        MovingToJob,
        Digging,
        Claiming,
        Reinforcing,
        Building,
        Mining,
        MovingToDeposit,
        Depositing,
        MovingToSlimePickup,
        CollectingSlime,
        ReturningToLair,
        IdleInLair
    }

    /// Where an impling carrying cargo is headed to unload it — Treasury for
    /// gold, the Chaos Core for mana crystals, a BaconBeacon storage tile
    /// for hauled-in slimes. Purely internal bookkeeping for
    /// TickDepositing; nothing outside ImplingAgent needs to know which one
    /// is in progress.
    internal enum DepositKind
    {
        Treasury,
        ChaosCore,
        BaconBeacon
    }

    /// Walks a real A*-planned route (AStarPathfinder) instead of a straight
    /// line, so it can no longer cut through undug rock. Paths only need
    /// planning once per trip — tiles only ever go Rock -> Floor, never back,
    /// so a route found now stays valid for the rest of that walk.
    public class ImplingAgent : MonoBehaviour, IJobWorker
    {
        private static int _nextId;
        private static readonly List<ImplingAgent> _all = new List<ImplingAgent>();

        /// Every currently-alive impling — for debug/inspection UI only.
        public static IReadOnlyList<ImplingAgent> All => _all;

        public int Id { get; private set; }
        public ImplingState State => _state;
        public Vector3 Position => transform.position;

        /// A random name from CreatureNames.ImpNames, picked once at spawn
        /// (see Awake) and kept for life — plus the numeric Id, in case two
        /// Imps roll the same name out of the 50-name pool.
        public string Name => _name;
        private string _name;

        /// Read-only from the outside (Creatures menu / F2 debug panel) —
        /// mutated only by this agent itself via TickMining/TickDepositing.
        public ImplingInventory Inventory => _inventory;

        /// Level/stats/skill slots, per design-doc.md's Creatures section.
        /// Read-only from the outside; ticked and leveled internally.
        public Creature Creature => _creature;

        // Level-1 values match the Imp's old hardcoded defaults exactly
        // (Strength 20 == old _hitDamage, Attackspeed 1 == 1 / old
        // _hitInterval, Movespeed 3 == old _moveSpeed) so wiring this in
        // doesn't change level-1 behavior. Growth-per-level numbers below
        // are placeholder tunables — see design-doc.md's "TBD" scaling
        // curve note.
        [SerializeField]
        private CreatureStatBlock _baseStats = new CreatureStatBlock
        {
            MaxHP = 50f,
            HPRegen = 1f,
            MaxMana = 0f,
            ManaRegen = 0f,
            Strength = 20f,
            Movespeed = 3f,
            Attackspeed = 1f,
            Intelligence = 0f,
            Craftmanship = 0f,
            Armor = 0f,
            Lifesteal = 0f
        };

        [SerializeField]
        private CreatureStatBlock _growthPerLevel = new CreatureStatBlock
        {
            MaxHP = 5f,
            HPRegen = 0.2f,
            Strength = 2f,
            Movespeed = 0.15f,
            Attackspeed = 0.075f,
            // 1/9 so 9 level-ups (level 1 -> 10) land Armor at exactly +1.
            Armor = 1f / 9f
        };

        private Creature _creature;

        // Exp granted per landed "Mine" hit (Digging or Mining), regardless
        // of wall type or whether that hit destroys the wall — see
        // design-doc.md's Creatures section for the worked example this
        // number came from (5 exp/hit -> 25 exp for a solo rock wall, 50
        // for a solo gold wall).
        [SerializeField] private int _mineHitExp = 5;

        // Exp needed per level is Level * _expPerLevelStep (see
        // Creature.ExpToNextLevel) — this is the Imp's own value, not a
        // shared constant, so other creature types can be tuned to level up
        // slower/faster independent of the Imp's curve (e.g. a rare, strong
        // unit trains slower via a higher step here).
        [SerializeField] private int _expPerLevelStep = 100;

        [SerializeField] private float _claimDuration = 1.5f;
        [SerializeField] private float _reinforceDuration = 2f;
        [SerializeField] private float _buildDuration = 2f;
        [SerializeField] private float _depositDuration = 1f;
        [SerializeField] private float _slimePickupDuration = 1f;
        [SerializeField] private float _sameTileStandOffset = 0.18f;

        private BuilderJobBoard _jobBoard;
        private DungeonGrid _grid;
        private TreasuryManager _treasuryManager;
        private ChaosCore _chaosCore;
        private SlimeHatcheryManager _slimeHatchery;
        private BaconBeaconManager _baconBeacon;
        private Vector3 _lairPosition;
        private int _manaReserved;
        private readonly ImplingInventory _inventory = new ImplingInventory();

        private ImplingState _state;
        private Vector2Int _currentJobCoord;
        private JobKind _currentJobKind;
        private Vector2Int _depositCoord;
        private DepositKind _depositKind;
        private Vector2Int _slimePickupCoord;
        private float _hitTimer;
        private float _claimTimer;
        private float _reinforceTimer;
        private float _buildTimer;
        private float _depositTimer;
        private float _slimePickupTimer;

        private readonly List<Vector2Int> _gridPathBuffer = new List<Vector2Int>();
        private readonly List<Vector3> _waypoints = new List<Vector3>();
        private int _waypointIndex;

        private void Awake()
        {
            Id = _nextId++;
            _all.Add(this);
            _name = $"{CreatureNames.GetRandom(CreatureNames.ImpNames)} #{Id}";

            _creature = new Creature(_baseStats, _growthPerLevel, _expPerLevelStep);
            _creature.Skills.Set(CreatureSkillSlots.BasicAttackSlot, "Mine");
        }

        public void Initialize(BuilderJobBoard jobBoard, DungeonGrid grid, Vector3 lairPosition, TreasuryManager treasuryManager, ChaosCore chaosCore, SlimeHatcheryManager slimeHatchery, BaconBeaconManager baconBeacon, int manaReserved)
        {
            _jobBoard = jobBoard;
            _grid = grid;
            _lairPosition = lairPosition;
            _treasuryManager = treasuryManager;
            _chaosCore = chaosCore;
            _slimeHatchery = slimeHatchery;
            _baconBeacon = baconBeacon;
            // The Chaos Core reservation was already taken by ImplingSpawner
            // (before this agent even existed) — just remember how much to
            // hand back in OnDestroy.
            _manaReserved = manaReserved;
            // Not SetState(SeekingJob) — _state already defaults to SeekingJob
            // (enum value 0), so that call would no-op against its own guard
            // and skip registering availability. Register it directly instead.
            _state = ImplingState.SeekingJob;
            _jobBoard.SetWorkerAvailable(this, true);
        }

        public void SetLairPosition(Vector3 lairPosition)
        {
            _lairPosition = lairPosition;
        }

        private void Update()
        {
            _creature.Tick(Time.deltaTime);

            switch (_state)
            {
                case ImplingState.SeekingJob:
                    TrySeekJob();
                    break;
                case ImplingState.MovingToJob:
                    MoveAlongPathThen(StartJobAction);
                    break;
                case ImplingState.Digging:
                    TickDigging();
                    break;
                case ImplingState.Claiming:
                    TickClaiming();
                    break;
                case ImplingState.Reinforcing:
                    TickReinforcing();
                    break;
                case ImplingState.Building:
                    TickBuilding();
                    break;
                case ImplingState.Mining:
                    TickMining();
                    break;
                case ImplingState.MovingToDeposit:
                    MoveAlongPathThen(StartDepositing);
                    break;
                case ImplingState.Depositing:
                    TickDepositing();
                    break;
                case ImplingState.MovingToSlimePickup:
                    MoveAlongPathThen(StartCollectingSlime);
                    break;
                case ImplingState.CollectingSlime:
                    TickCollectingSlime();
                    break;
                case ImplingState.ReturningToLair:
                    MoveAlongPathThen(() => SetState(ImplingState.IdleInLair));
                    break;
                case ImplingState.IdleInLair:
                    TrySeekJob();
                    break;
            }
        }

        private void TrySeekJob()
        {
            // A full inventory takes priority over picking up a new job —
            // there'd be nowhere to put anything more mined anyway.
            if (_inventory.IsFull && TryFindDepositTarget(out var fullCoord, out var fullKind))
            {
                GoToDeposit(fullCoord, fullKind);
                return;
            }

            if (_jobBoard.TryClaimNearestJob(this, out var coord, out var slotIndex, out var approachCoord, out var kind))
            {
                var standWorldPos = GetStandWorldPos(approachCoord, coord, slotIndex);
                if (PlanPathTo(approachCoord, standWorldPos))
                {
                    _currentJobCoord = coord;
                    _currentJobKind = kind;
                    SetState(ImplingState.MovingToJob);
                }
                else
                {
                    // Shouldn't happen — BuilderJobBoard just verified this
                    // exact approach tile is reachable — but if it somehow
                    // isn't, hand the job back rather than getting stuck
                    // holding a claim slot it can never walk to.
                    _jobBoard.ReleaseJob(coord);
                }

                return;
            }

            // No job available but still carrying something (e.g. a
            // not-quite-full load left over after TickMining stopped for
            // some other reason) — bank it instead of idling with it.
            if (_inventory.HasCargo && TryFindDepositTarget(out var idleCoord, out var idleKind))
            {
                GoToDeposit(idleCoord, idleKind);
                return;
            }

            // Truly nothing else to do — opportunistically haul a bred
            // slime from a Hatchery out to a Beacon rather than just
            // idling in the Lair. Lowest priority of all: construction
            // jobs and banking cargo already in hand both come first.
            if (!_inventory.HasCargo && TryFindHaulTask(out var hatcheryCoord))
            {
                GoToPickupSlime(hatcheryCoord);
                return;
            }

            if (_state == ImplingState.SeekingJob && PlanPathTo(_grid.WorldToGrid(_lairPosition), _lairPosition))
            {
                SetState(ImplingState.ReturningToLair);
            }

            // If PlanPathTo failed (no route home right now — e.g. walled
            // off), just stand still; TrySeekJob runs again next frame and
            // will retry on its own once the world reconnects.
        }

        /// Gold goes to the nearest Treasury tile with room; mana crystals
        /// go to the (single, capacity-less) Chaos Core. Gold is checked
        /// first — an impling carrying both only needs one deposit trip's
        /// worth of "what's my target" resolved at a time, since finishing
        /// a Treasury deposit sends it back through TrySeekJob, which will
        /// pick up the mana-crystal leg next if any remain.
        private bool TryFindDepositTarget(out Vector2Int coord, out DepositKind kind)
        {
            if (_inventory.Gold > 0 && _treasuryManager != null && _treasuryManager.TryFindNearestTileWithRoom(_grid.WorldToGrid(transform.position), out var treasuryCoord))
            {
                coord = treasuryCoord;
                kind = DepositKind.Treasury;
                return true;
            }

            if (_inventory.ManaCrystals > 0 && _chaosCore != null)
            {
                coord = GetChaosCoreDepositCoord();
                kind = DepositKind.ChaosCore;
                return true;
            }

            if (_inventory.Slimes > 0 && _baconBeacon != null && _baconBeacon.TryFindNearestTileWithRoom(_grid.WorldToGrid(transform.position), out var beaconCoord))
            {
                coord = beaconCoord;
                kind = DepositKind.BaconBeacon;
                return true;
            }

            coord = default;
            kind = default;
            return false;
        }

        /// The Chaos Core's center tile is blocked to pathfinding (it's the
        /// raised orb pedestal now, not walkable Floor — see
        /// DungeonGrid.IsWalkable and ChaosCore.Initialize's SetBlocked
        /// call), so depositing targets the nearest walkable tile on the
        /// Core's 3x3 platform instead of the center itself.
        private Vector2Int GetChaosCoreDepositCoord()
        {
            var coreCoord = _chaosCore.Coord;
            var implingCoord = _grid.WorldToGrid(transform.position);

            var best = coreCoord + GridDirections.Cardinal[0];
            var bestDist = int.MaxValue;
            foreach (var offset in GridDirections.Cardinal)
            {
                var candidate = coreCoord + offset;
                var dist = Mathf.Abs(candidate.x - implingCoord.x) + Mathf.Abs(candidate.y - implingCoord.y);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }

            return best;
        }

        /// A ready-to-collect Hatchery is only worth walking to if there's
        /// also somewhere to unload it afterward — otherwise the impling
        /// would just wind up carrying an un-deliverable slime forever
        /// (TryFindDepositTarget's Beacon check would keep failing).
        private bool TryFindHaulTask(out Vector2Int hatcheryCoord)
        {
            hatcheryCoord = default;
            if (_slimeHatchery == null || _baconBeacon == null)
            {
                return false;
            }

            var currentCoord = _grid.WorldToGrid(transform.position);
            return _slimeHatchery.TryFindReadyHatchery(currentCoord, out hatcheryCoord)
                && _baconBeacon.TryFindNearestTileWithRoom(currentCoord, out _);
        }

        private void GoToPickupSlime(Vector2Int hatcheryCoord)
        {
            // The coop tile is itself walkable Floor, same as a Treasury/
            // Chaos Core deposit target — no adjacent-approach step needed.
            if (!PlanPathTo(hatcheryCoord, _grid.GridToWorld(hatcheryCoord)))
            {
                return;
            }

            _slimePickupCoord = hatcheryCoord;
            SetState(ImplingState.MovingToSlimePickup);
        }

        private void StartCollectingSlime()
        {
            SetState(ImplingState.CollectingSlime);
            _slimePickupTimer = 0f;
        }

        private void TickCollectingSlime()
        {
            _slimePickupTimer += Time.deltaTime;
            if (_slimePickupTimer < _slimePickupDuration)
            {
                return;
            }

            var remainingCapacity = ImplingInventory.MaxWeight - _inventory.CarriedWeight;
            var collected = _slimeHatchery.CollectSlime(_slimePickupCoord, remainingCapacity);
            _inventory.AddSlimes(collected);
            SetState(ImplingState.SeekingJob);
        }

        private void GoToDeposit(Vector2Int coord, DepositKind kind)
        {
            // Treasury/BaconBeacon targets are themselves walkable Floor
            // tiles, and the Chaos Core target is already resolved to an
            // adjacent walkable tile by GetChaosCoreDepositCoord (its
            // actual center is blocked — see that method) — so in every
            // case the impling paths directly onto coord. Treasury targets
            // are reachability-checked by TreasuryManager already; the
            // Chaos Core isn't, and neither is immune to the world changing
            // after the target was picked (e.g. a wall built across the
            // route) — if planning fails here, just don't move; TrySeekJob
            // retries next frame.
            if (!PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return;
            }

            _depositCoord = coord;
            _depositKind = kind;
            SetState(ImplingState.MovingToDeposit);
        }

        private void SetState(ImplingState newState)
        {
            if (newState == _state)
            {
                return;
            }

            var coord = _grid.WorldToGrid(transform.position);
            GameplayLog.Write($"{Name} {_state} -> {newState} at ({coord.x},{coord.y})");
            _state = newState;

            // Set the instant the state changes, not deferred to this
            // impling's own next Update() — otherwise a worker that just
            // freed up (e.g. Digging -> SeekingJob) stays invisible to OTHER
            // implings' "is someone closer" check for the rest of this frame,
            // letting a far-away idle worker snap up a job that should have
            // waited for the closer one. This was a real bug: two implings
            // finishing a shared tile would sometimes see a newly-reachable
            // job get grabbed by implings sitting idle in the Lair instead,
            // simply because those implings' Update() already runs a
            // TryClaimNearestJob check every frame while the just-freed ones
            // hadn't gotten to register as available yet.
            var isAvailable = newState is ImplingState.SeekingJob or ImplingState.ReturningToLair or ImplingState.IdleInLair;
            _jobBoard.SetWorkerAvailable(this, isAvailable);
        }

        private void OnDestroy()
        {
            _all.Remove(this);

            if (_jobBoard != null)
            {
                _jobBoard.SetWorkerAvailable(this, false);
            }

            _chaosCore?.ReleaseMana(_manaReserved);
        }

        /// approachCoord is the walkable tile BuilderJobBoard verified is
        /// actually connected to this impling — no need to re-scan neighbors.
        private Vector3 GetStandWorldPos(Vector2Int approachCoord, Vector2Int jobCoord, int slotIndex)
        {
            var standWorldPos = _grid.GridToWorld(approachCoord);

            // A second impling on the same tile stands a little to one side so the
            // two don't sit exactly on top of each other (up to 2 share a job).
            if (slotIndex == 0)
            {
                return standWorldPos;
            }

            var towardJob = (_grid.GridToWorld(jobCoord) - standWorldPos).normalized;
            var lateral = new Vector3(-towardJob.z, 0f, towardJob.x);
            return standWorldPos + lateral * _sameTileStandOffset;
        }

        /// Builds the world-space waypoint list to walk: every A*-planned grid
        /// cell up to (but not including) the last, then finalWorldPos exactly
        /// — which may differ slightly from that last cell's center (e.g. the
        /// slot-1 lateral offset, or the Lair's true center point). Returns
        /// false, with _waypoints left empty, if no path exists — the caller
        /// is expected to not transition into a moving state in that case
        /// (see TrySeekJob/GoToDeposit) rather than falling back to a
        /// straight line through whatever's blocking the way. Dig/reinforce/
        /// build/claim job targets are pre-verified reachable by
        /// BuilderJobBoard before being assigned, so this should always
        /// succeed for those; the Lair and deposit targets (Treasury, Chaos
        /// Core) have no such guarantee — the world can change (e.g. a wall
        /// built across the only route home) between an impling picking a
        /// destination and getting there.
        private bool PlanPathTo(Vector2Int goalCoord, Vector3 finalWorldPos)
        {
            var startCoord = _grid.WorldToGrid(transform.position);
            var found = AStarPathfinder.TryFindPath(_grid, startCoord, goalCoord, _gridPathBuffer);

            _waypoints.Clear();
            _waypointIndex = 0;

            if (!found)
            {
                return false;
            }

            for (int i = 0; i < _gridPathBuffer.Count - 1; i++)
            {
                _waypoints.Add(_grid.GridToWorld(_gridPathBuffer[i]));
            }

            _waypoints.Add(finalWorldPos);
            return true;
        }

        private void MoveAlongPathThen(Action onArrive)
        {
            if (_waypointIndex >= _waypoints.Count)
            {
                onArrive();
                return;
            }

            var target = _waypoints[_waypointIndex];
            var flatTarget = new Vector3(target.x, transform.position.y, target.z);
            transform.position = Vector3.MoveTowards(transform.position, flatTarget, _creature.Stats.Movespeed * Time.deltaTime);
            if (Vector3.Distance(transform.position, flatTarget) < 0.05f)
            {
                _waypointIndex++;
                if (_waypointIndex >= _waypoints.Count)
                {
                    onArrive();
                }
            }
        }

        /// Called once MoveAlongPathThen reaches a job's approach tile —
        /// branches on which kind of job TrySeekJob assigned, since digging
        /// and claiming otherwise share the exact same travel logic. A Dig
        /// job on a resource-wall tile (WallResourceType != None) becomes
        /// Mining instead of Digging — mining reuses the exact same job
        /// queue/assignment pipeline as plain digging (see BuilderJobBoard),
        /// it's only the impling-side action and payoff that differ.
        private void StartJobAction()
        {
            switch (_currentJobKind)
            {
                case JobKind.Dig:
                    if (_grid.GetTile(_currentJobCoord).WallResourceType != WallResourceType.None)
                    {
                        StartMining();
                    }
                    else
                    {
                        StartDigging();
                    }
                    break;
                case JobKind.Reinforce:
                    StartReinforcing();
                    break;
                case JobKind.Build:
                    StartBuilding();
                    break;
                default:
                    StartClaiming();
                    break;
            }
        }

        private void StartDigging()
        {
            SetState(ImplingState.Digging);
            _hitTimer = 0f;
        }

        private void StartClaiming()
        {
            SetState(ImplingState.Claiming);
            _claimTimer = 0f;
        }

        private void StartReinforcing()
        {
            SetState(ImplingState.Reinforcing);
            _reinforceTimer = 0f;
        }

        private void StartBuilding()
        {
            SetState(ImplingState.Building);
            _buildTimer = 0f;
        }

        private void StartMining()
        {
            SetState(ImplingState.Mining);
            _hitTimer = 0f;
        }

        private void StartDepositing()
        {
            SetState(ImplingState.Depositing);
            _depositTimer = 0f;
        }

        /// "Mine" — the Imp's basic attack (skill slot 0), driven by its
        /// Attackspeed/Strength stats. See design-doc.md's Creatures section.
        private float MineHitInterval => 1f / _creature.Stats.Attackspeed;
        private int MineHitDamage => Mathf.RoundToInt(_creature.Stats.Strength);

        private void TickDigging()
        {
            // Checked every frame, not just on this impling's own hit cadence —
            // a second worker sharing the tile could dig it out from under this
            // one between hits, and waiting for its own next tick to notice
            // could lag up to a full MineHitInterval behind reality.
            if (!_jobBoard.IsStillDiggable(_currentJobCoord))
            {
                SetState(ImplingState.SeekingJob);
                return;
            }

            _hitTimer += Time.deltaTime;
            if (_hitTimer < MineHitInterval)
            {
                return;
            }

            _hitTimer -= MineHitInterval;
            var wasDestroyed = _jobBoard.ApplyHit(_currentJobCoord, MineHitDamage, out _, out _);
            _creature.AddExp(_mineHitExp);
            if (wasDestroyed)
            {
                SetState(ImplingState.SeekingJob);
            }
        }

        private void TickClaiming()
        {
            if (!_jobBoard.IsStillClaimable(_currentJobCoord))
            {
                SetState(ImplingState.SeekingJob);
                return;
            }

            _claimTimer += Time.deltaTime;
            if (_claimTimer < _claimDuration)
            {
                return;
            }

            _jobBoard.ApplyClaim(_currentJobCoord);
            SetState(ImplingState.SeekingJob);
        }

        private void TickReinforcing()
        {
            if (!_jobBoard.IsStillReinforceable(_currentJobCoord))
            {
                SetState(ImplingState.SeekingJob);
                return;
            }

            _reinforceTimer += Time.deltaTime;
            if (_reinforceTimer < _reinforceDuration)
            {
                return;
            }

            _jobBoard.ApplyReinforce(_currentJobCoord);
            SetState(ImplingState.SeekingJob);
        }

        private void TickBuilding()
        {
            if (!_jobBoard.IsStillConstructable(_currentJobCoord))
            {
                SetState(ImplingState.SeekingJob);
                return;
            }

            _buildTimer += Time.deltaTime;
            if (_buildTimer < _buildDuration)
            {
                return;
            }

            _jobBoard.ApplyBuild(_currentJobCoord);
            SetState(ImplingState.SeekingJob);
        }

        /// Same hit-cadence/still-diggable checks as TickDigging (mining a
        /// resource wall is still a Dig job as far as BuilderJobBoard is
        /// concerned), but credits whatever the hit yields to Inventory and
        /// — the one real behavioral difference — abandons the job the
        /// instant the inventory fills up, releasing it back to the board
        /// (BuilderJobBoard.ReleaseJob) so another impling (or this one,
        /// after depositing) can pick it back up rather than leaving it
        /// permanently claimed by a worker who can't carry any more.
        private void TickMining()
        {
            if (!_jobBoard.IsStillDiggable(_currentJobCoord))
            {
                SetState(ImplingState.SeekingJob);
                return;
            }

            _hitTimer += Time.deltaTime;
            if (_hitTimer < MineHitInterval)
            {
                return;
            }

            _hitTimer -= MineHitInterval;
            var destroyed = _jobBoard.ApplyHit(_currentJobCoord, MineHitDamage, out var resourceType, out var resourceAmount);
            _inventory.Add(resourceType, resourceAmount);
            _creature.AddExp(_mineHitExp);

            if (destroyed)
            {
                SetState(ImplingState.SeekingJob);
                return;
            }

            if (_inventory.IsFull)
            {
                _jobBoard.ReleaseJob(_currentJobCoord);
                SetState(ImplingState.SeekingJob);
            }
        }

        private void TickDepositing()
        {
            _depositTimer += Time.deltaTime;
            if (_depositTimer < _depositDuration)
            {
                return;
            }

            switch (_depositKind)
            {
                case DepositKind.Treasury:
                    var deposited = _treasuryManager.Deposit(_depositCoord, _inventory.Gold);
                    _inventory.RemoveGold(deposited);
                    break;
                case DepositKind.ChaosCore:
                    var used = _chaosCore.DepositManaCrystals(_inventory.ManaCrystals);
                    _inventory.RemoveManaCrystals(used);
                    break;
                case DepositKind.BaconBeacon:
                    var converted = _baconBeacon.ConvertSlimes(_depositCoord, _inventory.Slimes);
                    _inventory.RemoveSlimes(converted);
                    break;
            }

            SetState(ImplingState.SeekingJob);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// What a Maze Rattler is currently doing — decided every frame by
    /// priority (see EvaluateAndAct). Happiness gates everything: Leaving
    /// overrides every other tier (see TickLeaving), Unhappy/Angry refuse
    /// productive tasks and periodically lash out instead (see TickHostile),
    /// GettingUnhappy just refuses tasks. Otherwise, highest priority wins:
    /// 100 no personal Lair -> claim/create one, 80 hungry -> eat Bacon, 40
    /// Training Room available -> train, below that a placed Jail -> haunt
    /// the prisoners (wander its pit tiles), otherwise -> roam. Same
    /// MovingTo.../pausing pair shape as Gremlin's own Training/Roam.
    public enum MazeRattlerTask
    {
        Idle,
        MovingToLairSpot,
        MovingToFood,
        MovingToTraining,
        Training,
        MovingToHaunt,
        HauntPausing,
        MovingToRoam,
        RoamPausing,
        MovingToAttackTarget,
        Attacking,
        MovingToPortal
    }

    /// Copied from GremlinAgent (see its own header for the shared shape) —
    /// same stats/Hunger/Pay/Happiness/attack behavior, differing only in
    /// its join requirement (a placed Jail rather than Hatchery/Training
    /// Room population caps — see MazeRattlerSpawner) and its idle-tier
    /// behavior: below Training, a Maze Rattler with a placed Jail wanders
    /// its pit tiles ("haunt the prisoners") instead of going straight to
    /// Gremlin's generic floor-tile roam. Visual is a placeholder brown
    /// capsule until a real model exists — see MazeRattlerSpawner.
    public class MazeRattlerAgent : MonoBehaviour, ICombatant
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool (see Portal.SeedPool/TryTakeFromPool and
        /// MazeRattlerSpawner.TryRecruitMazeRattler).
        public const string CreatureKind = "MazeRattler";

        private static int _nextId;
        private static readonly List<MazeRattlerAgent> _all = new List<MazeRattlerAgent>();

        /// Every currently-alive Maze Rattler — for debug/inspection UI
        /// only, same convention ImplingAgent.All/GremlinAgent.All use.
        public static IReadOnlyList<MazeRattlerAgent> All => _all;

        /// How many currently-alive Maze Rattlers belong to ownerId —
        /// spawner population caps are per-keeper now (see
        /// MazeRattlerSpawner.MeetsJoinRequirements).
        public static int CountForOwner(int ownerId)
        {
            var count = 0;
            foreach (var agent in _all)
            {
                if (agent.Creature.OwnerId == ownerId)
                {
                    count++;
                }
            }
            return count;
        }

        public int Id { get; private set; }
        public Vector3 Position => transform.position;

        /// A random name from CreatureNames.MazeRattlerNames, picked once at
        /// spawn (see Awake) and kept for life — plus the numeric Id, in
        /// case two Maze Rattlers roll the same name out of the 50-name
        /// pool.
        public string Name => _name;
        private string _name;

        public MazeRattlerTask Task => _task;

        /// Level/stats/skill slots, per design-doc.md's Creatures section.
        /// Read-only from the outside; ticked internally.
        public Creature Creature => _creature;

        /// Read-only from the outside — ticked internally, same convention
        /// Creature uses.
        public Hunger Hunger => _hunger;

        /// Read-only from the outside — ticked internally, same convention
        /// Hunger uses.
        public Pay Pay => _pay;

        /// Read-only from the outside — ticked internally, driven by Hunger
        /// and Pay (see Happiness's own header).
        public Happiness Happiness => _happiness;

        /// Creature-vs-creature combat — see design-doc.md's Combat section
        /// and GremlinAgent for the shared wiring.
        public Combatant Combat => _combat;
        public bool IsImp => false;
        public string Species => CreatureKind;
        private readonly Combatant _combat = new Combatant();

        // Same placeholder stat block as GremlinAgent — no design-brief
        // values exist yet for Maze Rattler specifically, and "copy the
        // Gremlin" per the brief means reusing these rather than inventing
        // new numbers.
        [SerializeField]
        private CreatureStatBlock _baseStats = new CreatureStatBlock
        {
            MaxHP = 80f,
            HPRegen = 1f,
            Movespeed = 3.5f,
            Strength = 15f,
            Attackspeed = 0.8f
        };

        // Same growth ratios as Gremlin's own block ("copy the Gremlin"
        // applies to growth too, same base stats) — +10% Strength, +7.5%
        // Attackspeed, +5% Movespeed per level, +1 Armor by level 10.
        [SerializeField]
        private CreatureStatBlock _growthPerLevel = new CreatureStatBlock
        {
            MaxHP = 8f,
            HPRegen = 0.2f,
            Strength = 1.5f,
            Movespeed = 0.175f,
            Attackspeed = 0.06f,
            Armor = 1f / 9f
        };

        [SerializeField] private int _expPerLevelStep = 100;

        [SerializeField] private float _roamPauseDuration = 2f;

        // How long a haunting Maze Rattler lingers at one pit tile before
        // wandering to another — same idea as Gremlin's roam pause, just
        // its own tunable rather than sharing _roamPauseDuration, since
        // haunting is thematically its own behavior even though the code
        // shape is identical.
        [SerializeField] private float _hauntPauseDuration = 2f;

        [SerializeField] private float _minTrainPauseSeconds = 3f;
        [SerializeField] private float _maxTrainPauseSeconds = 5f;

        [SerializeField] private float _attackCheckIntervalSeconds = 8f;
        [SerializeField] private float _unhappyAttackChance = 0.25f;
        [SerializeField] private float _angryAttackChance = 0.6f;

        private Creature _creature;
        private readonly Hunger _hunger = new Hunger();
        private readonly Pay _pay = new Pay();
        private readonly Happiness _happiness = new Happiness();

        private DungeonGrid _grid;
        private LairManager _lairManager;
        private TavernManager _tavernManager;
        private TrainingRoomManager _trainingRoomManager;
        private JailManager _jailManager;
        private TreasuryManager _treasuryManager;
        private Portal _portal;

        private MazeRattlerTask _task = MazeRattlerTask.Idle;
        private string _myLairRoomId;
        private Vector2Int _myLairCoord;
        private Vector2Int _lairTargetCoord;
        private Vector2Int _foodTargetCoord;
        private Vector2Int _trainingTargetCoord;
        private Vector2Int _hauntTargetCoord;
        private Vector2Int _roamTargetCoord;
        private Vector2Int _attackTargetCoord;
        private bool _attackTargetIsRoom;
        private float _trainTimer;
        private float _trainPauseTimer;
        private float _trainPauseDuration;
        private float _hauntPauseTimer;
        private float _roamPauseTimer;
        private float _attackCheckTimer;
        private float _attackHitTimer;

        private readonly List<Vector2Int> _gridPathBuffer = new List<Vector2Int>();
        private readonly List<Vector3> _waypoints = new List<Vector3>();
        private int _waypointIndex;

        // The goal last handed to PlanPathTo — cached so
        // ReplanPathFromCurrentPosition can re-run the exact same call
        // after this Maze Rattler's position changes out from under it
        // (see MinionGrabController), without needing to know which task
        // kind that goal belonged to.
        private Vector2Int _lastGoalCoord;
        private Vector3 _lastGoalWorldPos;

        private void Awake()
        {
            Id = _nextId++;
            _all.Add(this);
            _name = $"{CreatureNames.GetRandom(CreatureNames.MazeRattlerNames)} #{Id}";

            _creature = new Creature(_baseStats, _growthPerLevel, _expPerLevelStep);
        }

        public void Initialize(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, TrainingRoomManager trainingRoomManager, JailManager jailManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            _grid = grid;
            _lairManager = lairManager;
            _tavernManager = tavernManager;
            _trainingRoomManager = trainingRoomManager;
            _jailManager = jailManager;
            _treasuryManager = treasuryManager;
            _portal = portal;
            _creature.SetOwner(ownerId);
            CreatureHealthRing.Attach(gameObject, _creature, grid);
            _lairManager.RoomSold += OnLairSold;

            _combat.Initialize(this, this, grid, _creature, _hunger, _happiness,
                KeepersDomain.Core.KeeperContext.ForOwner(ownerId)?.ThroneCoord ?? grid.WorldToGrid(transform.position),
                () => _myLairRoomId != null ? _myLairCoord : (Vector2Int?)null,
                () => SetTask(MazeRattlerTask.Idle),
                isImp: false);
        }

        private void Update()
        {
            _creature.Tick(Time.deltaTime);
            _hunger.Tick(Time.deltaTime);
            _happiness.Tick(Time.deltaTime, _hunger.IsHungry, _task == MazeRattlerTask.Training && !_combat.InCombat);
            if (_pay.Tick(Time.deltaTime))
            {
                TryGetPaid();
            }

            if (_grid == null)
            {
                return;
            }

            // Combat overrides the normal priority list while engaged —
            // see GremlinAgent / design-doc.md's Combat section.
            if (_combat.Tick(Time.deltaTime))
            {
                return;
            }

            EvaluateAndAct();
        }

        private void TryGetPaid()
        {
            var wage = Pay.WageFor(_creature.Level);
            if (_treasuryManager != null && _treasuryManager.TrySpendGold(wage))
            {
                _pay.MarkPaid();
                _happiness.ApplyPaidBonus();
                GameplayLog.Write(_creature.OwnerId, $"{Name} was paid {wage} gold (Lv{_creature.Level})");
            }
            else
            {
                _pay.MarkUnpaid();
                _happiness.ApplyUnpaidPenalty();
                GameplayLog.Write(_creature.OwnerId, $"{Name} went unpaid ({wage} gold owed) — unhappy");
            }
        }

        private void OnDestroy()
        {
            _all.Remove(this);
            _combat.Dispose();

            if (_lairManager != null)
            {
                _lairManager.RoomSold -= OnLairSold;

                if (_myLairRoomId != null)
                {
                    _lairManager.ReleaseLairTile(_myLairCoord);
                }
            }
        }

        private void OnLairSold(string roomId)
        {
            if (roomId == _myLairRoomId)
            {
                _myLairRoomId = null;
            }
        }

        private void EvaluateAndAct()
        {
            if (TickInProgressAttack())
            {
                return;
            }

            var tier = _happiness.Tier;
            if (tier == HappinessTier.Leaving)
            {
                TickLeaving();
                return;
            }

            if (_task == MazeRattlerTask.MovingToPortal)
            {
                SetTask(MazeRattlerTask.Idle);
            }

            // Tier 100: no personal Lair claimed yet.
            if (_myLairRoomId == null && _task != MazeRattlerTask.MovingToLairSpot)
            {
                if (TryBeginPursueLair())
                {
                    return;
                }
            }

            if (_task == MazeRattlerTask.MovingToLairSpot)
            {
                MoveAlongPathThen(ArriveAtLairSpot);
                return;
            }

            // Tier 80: hungry.
            if (_hunger.IsHungry && _task != MazeRattlerTask.MovingToFood)
            {
                if (TryBeginPursueFood())
                {
                    return;
                }
            }

            if (_task == MazeRattlerTask.MovingToFood)
            {
                MoveAlongPathThen(ArriveAtFood);
                return;
            }

            if (Happiness.RefusesTasks(tier))
            {
                if (Happiness.IsHostile(tier))
                {
                    TickHostile(forced: false, tier);
                }
                else
                {
                    SetTask(MazeRattlerTask.Idle);
                }
                return;
            }

            // Tier 40 (Training), then a placed Jail's "haunt the
            // prisoners," then plain roam as the last fallback.
            if (_task == MazeRattlerTask.Idle)
            {
                TryBeginTrainOrHauntOrRoam();
            }

            switch (_task)
            {
                case MazeRattlerTask.MovingToTraining:
                    MoveAlongPathThen(ArriveAtTraining);
                    break;
                case MazeRattlerTask.Training:
                    TickTraining();
                    break;
                case MazeRattlerTask.MovingToHaunt:
                    MoveAlongPathThen(ArriveAtHaunt);
                    break;
                case MazeRattlerTask.HauntPausing:
                    TickHauntPause();
                    break;
                case MazeRattlerTask.MovingToRoam:
                    MoveAlongPathThen(ArriveAtRoam);
                    break;
                case MazeRattlerTask.RoamPausing:
                    TickRoamPause();
                    break;
            }
        }

        private void TickLeaving()
        {
            if (_task == MazeRattlerTask.MovingToPortal)
            {
                MoveAlongPathThen(ArriveAtPortal);
                return;
            }

            if (TryBeginPursuePortal())
            {
                return;
            }

            TickHostile(forced: true, HappinessTier.Angry);
        }

        private bool TryBeginPursuePortal()
        {
            if (_portal == null || !PlanPathTo(_portal.Coord, _grid.GridToWorld(_portal.Coord)))
            {
                return false;
            }

            SetTask(MazeRattlerTask.MovingToPortal);
            return true;
        }

        private void ArriveAtPortal()
        {
            GameplayLog.Write(_creature.OwnerId, $"{Name} walked up the Portal stairs and left the domain for good");
            Destroy(gameObject);
        }

        private bool TickInProgressAttack()
        {
            if (_task == MazeRattlerTask.MovingToAttackTarget)
            {
                MoveAlongPathThen(ArriveAtAttackTarget);
                return true;
            }

            if (_task == MazeRattlerTask.Attacking)
            {
                TickAttacking();
                return true;
            }

            return false;
        }

        private void TickHostile(bool forced, HappinessTier tier)
        {
            _attackCheckTimer += Time.deltaTime;
            if (_attackCheckTimer < _attackCheckIntervalSeconds)
            {
                return;
            }

            _attackCheckTimer = 0f;

            if (!forced && UnityEngine.Random.value > AttackChanceFor(tier))
            {
                return;
            }

            TryBeginAttack();
        }

        private float AttackChanceFor(HappinessTier tier)
        {
            return tier == HappinessTier.Angry ? _angryAttackChance : _unhappyAttackChance;
        }

        private bool TryBeginAttack()
        {
            var fromCoord = _grid.WorldToGrid(transform.position);
            var tryWallFirst = UnityEngine.Random.value < 0.5f;

            if (tryWallFirst)
            {
                return TryBeginAttackWall(fromCoord) || TryBeginAttackRoom(fromCoord);
            }

            return TryBeginAttackRoom(fromCoord) || TryBeginAttackWall(fromCoord);
        }

        private bool TryBeginAttackWall(Vector2Int fromCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var candidates = new List<Vector2Int>();
            foreach (var floorCoord in distances.Keys)
            {
                foreach (var direction in GridDirections.Cardinal)
                {
                    var neighbor = floorCoord + direction;
                    if (_grid.InBounds(neighbor) && _grid.GetTile(neighbor).Type == TileType.Rock)
                    {
                        candidates.Add(neighbor);
                    }
                }
            }

            if (!TryPickRandomCoord(candidates, out var wallCoord) || !TryFindApproachCoord(wallCoord, distances, out var approachCoord) || !PlanPathTo(approachCoord, _grid.GridToWorld(approachCoord)))
            {
                return false;
            }

            _attackTargetCoord = wallCoord;
            _attackTargetIsRoom = false;
            SetTask(MazeRattlerTask.MovingToAttackTarget);
            return true;
        }

        private bool TryBeginAttackRoom(Vector2Int fromCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var candidates = new List<Vector2Int>();
            foreach (var coord in distances.Keys)
            {
                if (_grid.GetTile(coord).HasRoom)
                {
                    candidates.Add(coord);
                }
            }

            if (!TryPickRandomCoord(candidates, out var roomCoord) || !PlanPathTo(roomCoord, _grid.GridToWorld(roomCoord)))
            {
                return false;
            }

            _attackTargetCoord = roomCoord;
            _attackTargetIsRoom = true;
            SetTask(MazeRattlerTask.MovingToAttackTarget);
            return true;
        }

        private static bool TryFindApproachCoord(Vector2Int wallCoord, Dictionary<Vector2Int, int> reachableFloor, out Vector2Int approachCoord)
        {
            foreach (var direction in GridDirections.Cardinal)
            {
                var neighbor = wallCoord + direction;
                if (reachableFloor.ContainsKey(neighbor))
                {
                    approachCoord = neighbor;
                    return true;
                }
            }

            approachCoord = default;
            return false;
        }

        private void ArriveAtAttackTarget()
        {
            SetTask(MazeRattlerTask.Attacking);
            _attackHitTimer = 0f;
        }

        private float AttackHitInterval => 1f / _creature.Stats.Attackspeed;
        private int AttackHitDamage => Mathf.RoundToInt(_creature.Stats.Strength);

        private void TickAttacking()
        {
            if (_attackTargetIsRoom)
            {
                TickAttackingRoom();
            }
            else
            {
                TickAttackingWall();
            }
        }

        private void TickAttackingRoom()
        {
            if (!_grid.GetTile(_attackTargetCoord).HasRoom)
            {
                SetTask(MazeRattlerTask.Idle);
                return;
            }

            _attackHitTimer += Time.deltaTime;
            if (_attackHitTimer < AttackHitInterval)
            {
                return;
            }

            _attackHitTimer -= AttackHitInterval;
            var destroyed = _grid.ApplyRoomDamage(_attackTargetCoord, AttackHitDamage);
            if (destroyed)
            {
                KeepersDomain.Core.KeeperContext.TrySellRoomAt(_grid, _attackTargetCoord);
                GameplayLog.Write(_creature.OwnerId, $"{Name} ({_happiness.Tier}) destroyed a room at ({_attackTargetCoord.x},{_attackTargetCoord.y})");
                SetTask(MazeRattlerTask.Idle);
            }
        }

        private void TickAttackingWall()
        {
            if (_grid.GetTile(_attackTargetCoord).Type != TileType.Rock)
            {
                SetTask(MazeRattlerTask.Idle);
                return;
            }

            _attackHitTimer += Time.deltaTime;
            if (_attackHitTimer < AttackHitInterval)
            {
                return;
            }

            _attackHitTimer -= AttackHitInterval;
            var destroyed = _grid.ApplyDigDamage(_attackTargetCoord, AttackHitDamage, out _, out _, _creature.OwnerId);
            if (destroyed)
            {
                GameplayLog.Write(_creature.OwnerId, $"{Name} ({_happiness.Tier}) smashed a wall at ({_attackTargetCoord.x},{_attackTargetCoord.y})");
                SetTask(MazeRattlerTask.Idle);
            }
        }

        private bool TryBeginPursueLair()
        {
            var fromCoord = _grid.WorldToGrid(transform.position);

            if (_lairManager.TryFindNearestUnclaimedLairTile(fromCoord, out var existingCoord) && PlanPathTo(existingCoord, _grid.GridToWorld(existingCoord)))
            {
                _lairTargetCoord = existingCoord;
                SetTask(MazeRattlerTask.MovingToLairSpot);
                return true;
            }

            if (TryFindRandomLairSpot(out var newCoord) && PlanPathTo(newCoord, _grid.GridToWorld(newCoord)))
            {
                _lairTargetCoord = newCoord;
                SetTask(MazeRattlerTask.MovingToLairSpot);
                return true;
            }

            return false;
        }

        private void ArriveAtLairSpot()
        {
            if (!_grid.GetTile(_lairTargetCoord).HasRoom)
            {
                _lairManager.TryPlaceLair(_lairTargetCoord, _lairTargetCoord);
            }

            if (_lairManager.TryClaimLairTile(_lairTargetCoord))
            {
                _myLairRoomId = _grid.GetTile(_lairTargetCoord).RoomId;
                _myLairCoord = _lairTargetCoord;
                GameplayLog.Write(_creature.OwnerId, $"{Name} claimed a Lair tile at ({_lairTargetCoord.x},{_lairTargetCoord.y})");
            }

            SetTask(MazeRattlerTask.Idle);
        }

        private bool TryBeginPursueFood()
        {
            if (_tavernManager == null || !_tavernManager.TryFindNearestTileWithBacon(_grid.WorldToGrid(transform.position), out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return false;
            }

            _foodTargetCoord = coord;
            SetTask(MazeRattlerTask.MovingToFood);
            return true;
        }

        private void ArriveAtFood()
        {
            if (_tavernManager.TryEatBacon(_foodTargetCoord, TavernManager.MealBaconAmount))
            {
                _hunger.Eat();
            }

            SetTask(MazeRattlerTask.Idle);
        }

        /// Training Room first (if any dummy is reachable), then a placed
        /// Jail's pit ("haunt the prisoners"), then plain roam as the last
        /// fallback — the fallback chain "below training" the brief asked
        /// for.
        private void TryBeginTrainOrHauntOrRoam()
        {
            if (_trainingRoomManager != null
                && _trainingRoomManager.TryFindNearestDummyTile(_grid.WorldToGrid(transform.position), out var trainingCoord)
                && PlanPathTo(trainingCoord, _grid.GridToWorld(trainingCoord)))
            {
                _trainingTargetCoord = trainingCoord;
                SetTask(MazeRattlerTask.MovingToTraining);
                return;
            }

            if (TryBeginHaunt())
            {
                return;
            }

            TryBeginRoam();
        }

        private void ArriveAtTraining()
        {
            SetTask(MazeRattlerTask.Training);
            _trainTimer = 0f;
            _trainPauseTimer = 0f;
            _trainPauseDuration = UnityEngine.Random.Range(_minTrainPauseSeconds, _maxTrainPauseSeconds);
        }

        private void TickTraining()
        {
            _trainTimer += Time.deltaTime;
            if (_trainTimer >= TrainingRoomManager.TrainingTickSeconds)
            {
                _trainTimer -= TrainingRoomManager.TrainingTickSeconds;
                _creature.AddExp(TrainingRoomManager.TrainingExpPerTick);
            }

            _trainPauseTimer += Time.deltaTime;
            if (_trainPauseTimer >= _trainPauseDuration)
            {
                TryMoveToNextDummy();
            }
        }

        private void TryMoveToNextDummy()
        {
            if (_trainingRoomManager != null
                && _trainingRoomManager.TryFindRandomDummyTile(_grid.WorldToGrid(transform.position), _trainingTargetCoord, out var coord)
                && PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                _trainingTargetCoord = coord;
                SetTask(MazeRattlerTask.MovingToTraining);
                return;
            }

            SetTask(MazeRattlerTask.Idle);
        }

        /// "Haunt the prisoners in the jail" — walks to a random reachable
        /// pit tile of any placed Jail (JailManager.TryFindRandomPitTile)
        /// and pauses there a while (see ArriveAtHaunt/TickHauntPause),
        /// same "walk somewhere, pause, re-evaluate" shape TryBeginRoam
        /// uses below it — re-evaluating back through Idle each time (
        /// rather than explicitly picking a different pit tile mid-pause
        /// the way TryMoveToNextDummy does for training) naturally drifts
        /// this Maze Rattler between different pit tiles over time, no
        /// dedicated "move to a different one" step needed. Grants no exp
        /// — there's no prisoner/capture mechanic yet for this to interact
        /// with, purely flavor movement.
        private bool TryBeginHaunt()
        {
            if (_jailManager == null || !_jailManager.TryFindRandomPitTile(_grid.WorldToGrid(transform.position), out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return false;
            }

            _hauntTargetCoord = coord;
            SetTask(MazeRattlerTask.MovingToHaunt);
            return true;
        }

        private void ArriveAtHaunt()
        {
            SetTask(MazeRattlerTask.HauntPausing);
            _hauntPauseTimer = 0f;
        }

        private void TickHauntPause()
        {
            _hauntPauseTimer += Time.deltaTime;
            if (_hauntPauseTimer >= _hauntPauseDuration)
            {
                SetTask(MazeRattlerTask.Idle);
            }
        }

        private void TryBeginRoam()
        {
            var fromCoord = _grid.WorldToGrid(transform.position);
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            if (!TryPickRandomCoord(distances.Keys, out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return;
            }

            _roamTargetCoord = coord;
            SetTask(MazeRattlerTask.MovingToRoam);
        }

        private void ArriveAtRoam()
        {
            SetTask(MazeRattlerTask.RoamPausing);
            _roamPauseTimer = 0f;
        }

        private void TickRoamPause()
        {
            _roamPauseTimer += Time.deltaTime;
            if (_roamPauseTimer >= _roamPauseDuration)
            {
                SetTask(MazeRattlerTask.Idle);
            }
        }

        private bool TryFindRandomLairSpot(out Vector2Int coord)
        {
            var fromCoord = _grid.WorldToGrid(transform.position);
            var distances = _grid.GetReachableFloorDistances(fromCoord);

            var candidates = new List<Vector2Int>();
            foreach (var candidate in distances.Keys)
            {
                if (_grid.CanBuildRoomOn(candidate, _creature.OwnerId))
                {
                    candidates.Add(candidate);
                }
            }

            return TryPickRandomCoord(candidates, out coord);
        }

        private static bool TryPickRandomCoord(ICollection<Vector2Int> candidates, out Vector2Int coord)
        {
            if (candidates.Count == 0)
            {
                coord = default;
                return false;
            }

            var index = UnityEngine.Random.Range(0, candidates.Count);
            var i = 0;
            foreach (var candidate in candidates)
            {
                if (i == index)
                {
                    coord = candidate;
                    return true;
                }
                i++;
            }

            coord = default;
            return false;
        }

        private void SetTask(MazeRattlerTask newTask)
        {
            _task = newTask;
        }

        /// Re-plans this Maze Rattler's route to whatever it was last
        /// walking toward, from wherever it is right now — called by
        /// MinionGrabController after the player's Grab hand drops it
        /// somewhere else mid-walk, so it heads straight for its actual
        /// objective instead of resuming stale waypoints computed from the
        /// tile it used to stand on (which could path straight through a
        /// wall placed/discovered in the meantime). No-ops if it wasn't
        /// actually walking anywhere when grabbed. Falls back to Idle if
        /// the objective just isn't reachable any more from the new spot —
        /// EvaluateAndAct re-derives a fresh objective from there next
        /// frame the same way it already does after any other task finishes.
        public void ReplanPathFromCurrentPosition()
        {
            _combat.OnExternalReposition();

            if (!IsMovingTask(_task))
            {
                return;
            }

            if (PlanPathTo(_lastGoalCoord, _lastGoalWorldPos))
            {
                return;
            }

            SetTask(MazeRattlerTask.Idle);
        }

        private static bool IsMovingTask(MazeRattlerTask task)
        {
            return task is MazeRattlerTask.MovingToLairSpot or MazeRattlerTask.MovingToFood or MazeRattlerTask.MovingToTraining
                or MazeRattlerTask.MovingToHaunt or MazeRattlerTask.MovingToRoam
                or MazeRattlerTask.MovingToAttackTarget or MazeRattlerTask.MovingToPortal;
        }

        private bool PlanPathTo(Vector2Int goalCoord, Vector3 finalWorldPos)
        {
            _lastGoalCoord = goalCoord;
            _lastGoalWorldPos = finalWorldPos;

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
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// What an Elf is currently doing — decided every frame by priority (see
    /// EvaluateAndAct). Same Happiness-gated shape every other creature
    /// uses, but with no preferred-room job tier at all (see this class's
    /// own header) — an Elf only ever claims a Lair, eats, or roams.
    public enum ElfTask
    {
        Idle,
        MovingToLairSpot,
        MovingToFood,
        MovingToRoam,
        RoamPausing,
        MovingToAttackTarget,
        Attacking,
        MovingToPortal
    }

    /// The "weak and worthless (meat shield)" failure outcome of Conversion
    /// Class's torment (see ConversionClassManager.TryTormentRandomPrisoner)
    /// — never recruited via the Portal's pool the way every other creature
    /// is (see ElfSpawner.SpawnElf, called directly rather than through a
    /// MeetsJoinRequirements/pool gate). Copied from MazeRattlerAgent's own
    /// shape (Creature/Hunger/Pay/Happiness composition, attack behavior,
    /// movement) but deliberately weaker stats and no preferred-room job
    /// tier — an Elf just claims a Lair, eats, and otherwise roams; it has
    /// nothing it's good at. Visual is a placeholder pale-green capsule,
    /// smaller than every other creature's, until a real model exists.
    public class ElfAgent : MonoBehaviour
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool — Elf is never actually seeded into one (see ElfSpawner),
        /// but the const still exists for symmetry with every other
        /// creature type and so ConversionClassManager can match on it by
        /// name the same way it matches Gremlin/Warlock/MazeRattler.
        public const string CreatureKind = "Elf";

        private static int _nextId;
        private static readonly List<ElfAgent> _all = new List<ElfAgent>();

        /// Every currently-alive Elf — for debug/inspection UI only, same
        /// convention every other creature type's All uses.
        public static IReadOnlyList<ElfAgent> All => _all;

        public int Id { get; private set; }
        public Vector3 Position => transform.position;

        /// A random name from CreatureNames.ElfNames, picked once at spawn
        /// (see Awake) and kept for life.
        public string Name => _name;
        private string _name;

        public ElfTask Task => _task;

        public Creature Creature => _creature;
        public Hunger Hunger => _hunger;
        public Pay Pay => _pay;
        public Happiness Happiness => _happiness;

        // Deliberately weak — "weak and worthless" per the brief. Well
        // below Gremlin's own placeholder stats in every dimension.
        [SerializeField]
        private CreatureStatBlock _baseStats = new CreatureStatBlock
        {
            MaxHP = 20f,
            HPRegen = 0.5f,
            Movespeed = 3f,
            Strength = 4f,
            Attackspeed = 0.5f
        };

        [SerializeField]
        private CreatureStatBlock _growthPerLevel = new CreatureStatBlock
        {
            MaxHP = 3f,
            HPRegen = 0.1f
        };

        [SerializeField] private int _expPerLevelStep = 100;

        [SerializeField] private float _roamPauseDuration = 2f;

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
        private TreasuryManager _treasuryManager;
        private Portal _portal;

        private ElfTask _task = ElfTask.Idle;
        private string _myLairRoomId;
        private Vector2Int _myLairCoord;
        private Vector2Int _lairTargetCoord;
        private Vector2Int _foodTargetCoord;
        private Vector2Int _roamTargetCoord;
        private Vector2Int _attackTargetCoord;
        private bool _attackTargetIsRoom;
        private float _roamPauseTimer;
        private float _attackCheckTimer;
        private float _attackHitTimer;

        private readonly List<Vector2Int> _gridPathBuffer = new List<Vector2Int>();
        private readonly List<Vector3> _waypoints = new List<Vector3>();
        private int _waypointIndex;

        private Vector2Int _lastGoalCoord;
        private Vector3 _lastGoalWorldPos;

        private void Awake()
        {
            Id = _nextId++;
            _all.Add(this);
            _name = $"{CreatureNames.GetRandom(CreatureNames.ElfNames)} #{Id}";

            _creature = new Creature(_baseStats, _growthPerLevel, _expPerLevelStep);
        }

        /// Elf doesn't need a Portal reference for pool interaction (it's
        /// never recruited that way), but keeps one anyway, same as every
        /// other creature's Initialize, purely for TickLeaving's "walk to
        /// the Portal and leave" behavior — an unhappy Elf should still be
        /// able to leave the domain the normal way.
        public void Initialize(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            _grid = grid;
            _lairManager = lairManager;
            _tavernManager = tavernManager;
            _treasuryManager = treasuryManager;
            _portal = portal;
            _creature.SetOwner(ownerId);
            CreatureHealthRing.Attach(gameObject, _creature, grid);
            _lairManager.RoomSold += OnLairSold;
        }

        private void Update()
        {
            _creature.Tick(Time.deltaTime);
            _hunger.Tick(Time.deltaTime);
            _happiness.Tick(Time.deltaTime, _hunger.IsHungry, isDoingPreferredRoomJob: false);
            if (_pay.Tick(Time.deltaTime))
            {
                TryGetPaid();
            }

            if (_grid == null)
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
                GameplayLog.Write($"{Name} was paid {wage} gold (Lv{_creature.Level})");
            }
            else
            {
                _pay.MarkUnpaid();
                _happiness.ApplyUnpaidPenalty();
                GameplayLog.Write($"{Name} went unpaid ({wage} gold owed) — unhappy");
            }
        }

        private void OnDestroy()
        {
            _all.Remove(this);

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

            if (_task == ElfTask.MovingToPortal)
            {
                SetTask(ElfTask.Idle);
            }

            // Tier 100: no personal Lair claimed yet.
            if (_myLairRoomId == null && _task != ElfTask.MovingToLairSpot)
            {
                if (TryBeginPursueLair())
                {
                    return;
                }
            }

            if (_task == ElfTask.MovingToLairSpot)
            {
                MoveAlongPathThen(ArriveAtLairSpot);
                return;
            }

            // Tier 80: hungry.
            if (_hunger.IsHungry && _task != ElfTask.MovingToFood)
            {
                if (TryBeginPursueFood())
                {
                    return;
                }
            }

            if (_task == ElfTask.MovingToFood)
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
                    SetTask(ElfTask.Idle);
                }
                return;
            }

            // No preferred-room job tier at all — an Elf is "worthless," so
            // the only tier below Hunger is plain roam.
            if (_task == ElfTask.Idle)
            {
                TryBeginRoam();
            }

            switch (_task)
            {
                case ElfTask.MovingToRoam:
                    MoveAlongPathThen(ArriveAtRoam);
                    break;
                case ElfTask.RoamPausing:
                    TickRoamPause();
                    break;
            }
        }

        private void TickLeaving()
        {
            if (_task == ElfTask.MovingToPortal)
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

            SetTask(ElfTask.MovingToPortal);
            return true;
        }

        private void ArriveAtPortal()
        {
            GameplayLog.Write($"{Name} walked up the Portal stairs and left the domain for good");
            Destroy(gameObject);
        }

        private bool TickInProgressAttack()
        {
            if (_task == ElfTask.MovingToAttackTarget)
            {
                MoveAlongPathThen(ArriveAtAttackTarget);
                return true;
            }

            if (_task == ElfTask.Attacking)
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
            SetTask(ElfTask.MovingToAttackTarget);
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
            SetTask(ElfTask.MovingToAttackTarget);
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
            SetTask(ElfTask.Attacking);
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
                SetTask(ElfTask.Idle);
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
                GameplayLog.Write($"{Name} ({_happiness.Tier}) destroyed a room at ({_attackTargetCoord.x},{_attackTargetCoord.y})");
                SetTask(ElfTask.Idle);
            }
        }

        private void TickAttackingWall()
        {
            if (_grid.GetTile(_attackTargetCoord).Type != TileType.Rock)
            {
                SetTask(ElfTask.Idle);
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
                GameplayLog.Write($"{Name} ({_happiness.Tier}) smashed a wall at ({_attackTargetCoord.x},{_attackTargetCoord.y})");
                SetTask(ElfTask.Idle);
            }
        }

        private bool TryBeginPursueLair()
        {
            var fromCoord = _grid.WorldToGrid(transform.position);

            if (_lairManager.TryFindNearestUnclaimedLairTile(fromCoord, out var existingCoord) && PlanPathTo(existingCoord, _grid.GridToWorld(existingCoord)))
            {
                _lairTargetCoord = existingCoord;
                SetTask(ElfTask.MovingToLairSpot);
                return true;
            }

            if (TryFindRandomLairSpot(out var newCoord) && PlanPathTo(newCoord, _grid.GridToWorld(newCoord)))
            {
                _lairTargetCoord = newCoord;
                SetTask(ElfTask.MovingToLairSpot);
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
                GameplayLog.Write($"{Name} claimed a Lair tile at ({_lairTargetCoord.x},{_lairTargetCoord.y})");
            }

            SetTask(ElfTask.Idle);
        }

        private bool TryBeginPursueFood()
        {
            if (_tavernManager == null || !_tavernManager.TryFindNearestTileWithBacon(_grid.WorldToGrid(transform.position), out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return false;
            }

            _foodTargetCoord = coord;
            SetTask(ElfTask.MovingToFood);
            return true;
        }

        private void ArriveAtFood()
        {
            if (_tavernManager.TryEatBacon(_foodTargetCoord, TavernManager.MealBaconAmount))
            {
                _hunger.Eat();
            }

            SetTask(ElfTask.Idle);
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
            SetTask(ElfTask.MovingToRoam);
        }

        private void ArriveAtRoam()
        {
            SetTask(ElfTask.RoamPausing);
            _roamPauseTimer = 0f;
        }

        private void TickRoamPause()
        {
            _roamPauseTimer += Time.deltaTime;
            if (_roamPauseTimer >= _roamPauseDuration)
            {
                SetTask(ElfTask.Idle);
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

        private void SetTask(ElfTask newTask)
        {
            _task = newTask;
        }

        /// Re-plans this Elf's route to whatever it was last walking toward,
        /// from wherever it is right now — called by MinionGrabController
        /// after the player's Grab hand drops it somewhere else mid-walk.
        /// Same shape as every other creature type's own
        /// ReplanPathFromCurrentPosition.
        public void ReplanPathFromCurrentPosition()
        {
            if (!IsMovingTask(_task))
            {
                return;
            }

            if (PlanPathTo(_lastGoalCoord, _lastGoalWorldPos))
            {
                return;
            }

            SetTask(ElfTask.Idle);
        }

        private static bool IsMovingTask(ElfTask task)
        {
            return task is ElfTask.MovingToLairSpot or ElfTask.MovingToFood
                or ElfTask.MovingToRoam or ElfTask.MovingToAttackTarget or ElfTask.MovingToPortal;
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

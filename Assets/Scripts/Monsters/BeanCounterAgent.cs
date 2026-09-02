using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// What a Bean Counter is currently doing — decided every frame by
    /// priority (see EvaluateAndAct). Same Happiness-gated shape every
    /// other creature uses: 100 no personal Lair -> claim/create one, 80
    /// hungry -> eat Bacon, 40 a Conversion Class exists -> "teach" (walk
    /// to a bench-adjacent tile, lecture, and — if any Jail is currently
    /// holding a prisoner — process one), otherwise -> roam.
    public enum BeanCounterTask
    {
        Idle,
        MovingToLairSpot,
        MovingToFood,
        MovingToTeaching,
        Teaching,
        MovingToRoam,
        RoamPausing,
        MovingToAttackTarget,
        Attacking,
        MovingToPortal
    }

    /// Copied from MazeRattlerAgent (see its own header for the shared
    /// shape) — same stats/Hunger/Pay/Happiness/attack behavior, differing
    /// only in its join requirement (a placed Conversion Class rather than
    /// a placed Jail) and its idle-tier behavior: below Hunger, a Bean
    /// Counter with a placed Conversion Class walks to a bench-adjacent
    /// tile and lectures there, periodically pulling a random prisoner out
    /// of whichever Jail is holding one and tormenting it (see
    /// ConversionClassManager.TryTormentRandomPrisoner) instead of Maze
    /// Rattler's purely-flavor pit-tile haunting. Visual is a placeholder
    /// sickly yellow-green capsule until a real model exists — see
    /// BeanCounterSpawner.
    public class BeanCounterAgent : MonoBehaviour
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool (see Portal.SeedPool/TryTakeFromPool and
        /// BeanCounterSpawner.TryRecruitBeanCounter).
        public const string CreatureKind = "BeanCounter";

        private static int _nextId;
        private static readonly List<BeanCounterAgent> _all = new List<BeanCounterAgent>();

        /// Every currently-alive Bean Counter — for debug/inspection UI
        /// only, same convention every other creature type's All uses.
        public static IReadOnlyList<BeanCounterAgent> All => _all;

        /// How many currently-alive Bean Counters belong to ownerId —
        /// spawner population caps are per-keeper now (see
        /// BeanCounterSpawner.MeetsJoinRequirements).
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

        /// A random name from CreatureNames.BeanCounterNames, picked once at
        /// spawn (see Awake) and kept for life.
        public string Name => _name;
        private string _name;

        public BeanCounterTask Task => _task;

        public Creature Creature => _creature;
        public Hunger Hunger => _hunger;
        public Pay Pay => _pay;
        public Happiness Happiness => _happiness;

        // A preacher, not a brawler — low HP/Strength/Attackspeed, no
        // design-brief values exist yet, same placeholder-numbers spirit
        // every other creature's stat block uses.
        [SerializeField]
        private CreatureStatBlock _baseStats = new CreatureStatBlock
        {
            MaxHP = 50f,
            HPRegen = 0.5f,
            Movespeed = 2.2f,
            Strength = 6f,
            Attackspeed = 0.5f
        };

        [SerializeField]
        private CreatureStatBlock _growthPerLevel = new CreatureStatBlock
        {
            MaxHP = 5f,
            HPRegen = 0.15f
        };

        [SerializeField] private int _expPerLevelStep = 100;

        [SerializeField] private float _roamPauseDuration = 2f;

        [SerializeField] private float _minTeachPauseSeconds = 3f;
        [SerializeField] private float _maxTeachPauseSeconds = 5f;

        // Exp granted for lecturing, on its own tick timer — same
        // ExpPerTick/TickSeconds pair shape TrainingRoomManager/
        // LibraryManager use for their own on-site jobs, placeholder
        // numbers sitting between the two.
        private const int TeachExpPerTick = 10;
        private const float TeachTickSeconds = 2f;

        // How long into a lecture session before this Bean Counter attempts
        // a torment — long enough to read as "delivering the sermon"
        // rather than instantly draining the whole Jail the moment it
        // arrives.
        [SerializeField] private float _tormentDelaySeconds = 4f;

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
        private ConversionClassManager _conversionClassManager;
        private JailManager _jailManager;
        private TreasuryManager _treasuryManager;
        private Portal _portal;

        private BeanCounterTask _task = BeanCounterTask.Idle;
        private string _myLairRoomId;
        private Vector2Int _myLairCoord;
        private Vector2Int _lairTargetCoord;
        private Vector2Int _foodTargetCoord;
        private Vector2Int _teachTargetCoord;
        private Vector2Int _roamTargetCoord;
        private Vector2Int _attackTargetCoord;
        private bool _attackTargetIsRoom;
        private float _teachTimer;
        private float _teachPauseTimer;
        private float _teachPauseDuration;
        private bool _hasTormentedThisSession;
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
            _name = $"{CreatureNames.GetRandom(CreatureNames.BeanCounterNames)} #{Id}";

            _creature = new Creature(_baseStats, _growthPerLevel, _expPerLevelStep);
        }

        public void Initialize(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, ConversionClassManager conversionClassManager, JailManager jailManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            _grid = grid;
            _lairManager = lairManager;
            _tavernManager = tavernManager;
            _conversionClassManager = conversionClassManager;
            _jailManager = jailManager;
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
            _happiness.Tick(Time.deltaTime, _hunger.IsHungry, _task == BeanCounterTask.Teaching);
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

            if (_task == BeanCounterTask.MovingToPortal)
            {
                SetTask(BeanCounterTask.Idle);
            }

            // Tier 100: no personal Lair claimed yet.
            if (_myLairRoomId == null && _task != BeanCounterTask.MovingToLairSpot)
            {
                if (TryBeginPursueLair())
                {
                    return;
                }
            }

            if (_task == BeanCounterTask.MovingToLairSpot)
            {
                MoveAlongPathThen(ArriveAtLairSpot);
                return;
            }

            // Tier 80: hungry.
            if (_hunger.IsHungry && _task != BeanCounterTask.MovingToFood)
            {
                if (TryBeginPursueFood())
                {
                    return;
                }
            }

            if (_task == BeanCounterTask.MovingToFood)
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
                    SetTask(BeanCounterTask.Idle);
                }
                return;
            }

            // Tier 40 (Teach), then plain roam as the last fallback.
            if (_task == BeanCounterTask.Idle)
            {
                TryBeginTeachOrRoam();
            }

            switch (_task)
            {
                case BeanCounterTask.MovingToTeaching:
                    MoveAlongPathThen(ArriveAtTeaching);
                    break;
                case BeanCounterTask.Teaching:
                    TickTeaching();
                    break;
                case BeanCounterTask.MovingToRoam:
                    MoveAlongPathThen(ArriveAtRoam);
                    break;
                case BeanCounterTask.RoamPausing:
                    TickRoamPause();
                    break;
            }
        }

        private void TickLeaving()
        {
            if (_task == BeanCounterTask.MovingToPortal)
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

            SetTask(BeanCounterTask.MovingToPortal);
            return true;
        }

        private void ArriveAtPortal()
        {
            GameplayLog.Write($"{Name} walked up the Portal stairs and left the domain for good");
            Destroy(gameObject);
        }

        private bool TickInProgressAttack()
        {
            if (_task == BeanCounterTask.MovingToAttackTarget)
            {
                MoveAlongPathThen(ArriveAtAttackTarget);
                return true;
            }

            if (_task == BeanCounterTask.Attacking)
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
            SetTask(BeanCounterTask.MovingToAttackTarget);
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
            SetTask(BeanCounterTask.MovingToAttackTarget);
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
            SetTask(BeanCounterTask.Attacking);
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
                SetTask(BeanCounterTask.Idle);
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
                SetTask(BeanCounterTask.Idle);
            }
        }

        private void TickAttackingWall()
        {
            if (_grid.GetTile(_attackTargetCoord).Type != TileType.Rock)
            {
                SetTask(BeanCounterTask.Idle);
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
                SetTask(BeanCounterTask.Idle);
            }
        }

        private bool TryBeginPursueLair()
        {
            var fromCoord = _grid.WorldToGrid(transform.position);

            if (_lairManager.TryFindNearestUnclaimedLairTile(fromCoord, out var existingCoord) && PlanPathTo(existingCoord, _grid.GridToWorld(existingCoord)))
            {
                _lairTargetCoord = existingCoord;
                SetTask(BeanCounterTask.MovingToLairSpot);
                return true;
            }

            if (TryFindRandomLairSpot(out var newCoord) && PlanPathTo(newCoord, _grid.GridToWorld(newCoord)))
            {
                _lairTargetCoord = newCoord;
                SetTask(BeanCounterTask.MovingToLairSpot);
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

            SetTask(BeanCounterTask.Idle);
        }

        private bool TryBeginPursueFood()
        {
            if (_tavernManager == null || !_tavernManager.TryFindNearestTileWithBacon(_grid.WorldToGrid(transform.position), out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return false;
            }

            _foodTargetCoord = coord;
            SetTask(BeanCounterTask.MovingToFood);
            return true;
        }

        private void ArriveAtFood()
        {
            if (_tavernManager.TryEatBacon(_foodTargetCoord, TavernManager.MealBaconAmount))
            {
                _hunger.Eat();
            }

            SetTask(BeanCounterTask.Idle);
        }

        private void TryBeginTeachOrRoam()
        {
            if (_conversionClassManager != null
                && _conversionClassManager.TryFindNearestBenchTile(_grid.WorldToGrid(transform.position), out var benchCoord)
                && PlanPathTo(benchCoord, _grid.GridToWorld(benchCoord)))
            {
                _teachTargetCoord = benchCoord;
                SetTask(BeanCounterTask.MovingToTeaching);
                return;
            }

            TryBeginRoam();
        }

        private void ArriveAtTeaching()
        {
            SetTask(BeanCounterTask.Teaching);
            _teachTimer = 0f;
            _teachPauseTimer = 0f;
            _teachPauseDuration = UnityEngine.Random.Range(_minTeachPauseSeconds, _maxTeachPauseSeconds);
            _hasTormentedThisSession = false;
        }

        private void TickTeaching()
        {
            _teachTimer += Time.deltaTime;
            if (_teachTimer >= TeachTickSeconds)
            {
                _teachTimer -= TeachTickSeconds;
                _creature.AddExp(TeachExpPerTick);
            }

            // One torment attempt per lecture session, fired partway
            // through the pause rather than the instant it arrives — reads
            // as "delivering the sermon" instead of instantly processing
            // the whole Jail. No-ops (silently) if nobody's currently held.
            if (!_hasTormentedThisSession && _teachPauseTimer + Time.deltaTime >= _tormentDelaySeconds
                && _jailManager != null && _jailManager.PrisonerCount > 0 && _conversionClassManager != null)
            {
                _hasTormentedThisSession = true;
                _conversionClassManager.TryTormentRandomPrisoner();
            }

            _teachPauseTimer += Time.deltaTime;
            if (_teachPauseTimer >= _teachPauseDuration)
            {
                TryMoveToNextBench();
            }
        }

        private void TryMoveToNextBench()
        {
            if (_conversionClassManager != null
                && _conversionClassManager.TryFindRandomBenchTile(_grid.WorldToGrid(transform.position), _teachTargetCoord, out var coord)
                && PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                _teachTargetCoord = coord;
                SetTask(BeanCounterTask.MovingToTeaching);
                return;
            }

            SetTask(BeanCounterTask.Idle);
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
            SetTask(BeanCounterTask.MovingToRoam);
        }

        private void ArriveAtRoam()
        {
            SetTask(BeanCounterTask.RoamPausing);
            _roamPauseTimer = 0f;
        }

        private void TickRoamPause()
        {
            _roamPauseTimer += Time.deltaTime;
            if (_roamPauseTimer >= _roamPauseDuration)
            {
                SetTask(BeanCounterTask.Idle);
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

        private void SetTask(BeanCounterTask newTask)
        {
            _task = newTask;
        }

        /// Re-plans this Bean Counter's route to whatever it was last
        /// walking toward, from wherever it is right now — called by
        /// MinionGrabController after the player's Grab hand drops it
        /// somewhere else mid-walk. Same shape as every other creature
        /// type's own ReplanPathFromCurrentPosition.
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

            SetTask(BeanCounterTask.Idle);
        }

        private static bool IsMovingTask(BeanCounterTask task)
        {
            return task is BeanCounterTask.MovingToLairSpot or BeanCounterTask.MovingToFood or BeanCounterTask.MovingToTeaching
                or BeanCounterTask.MovingToRoam or BeanCounterTask.MovingToAttackTarget or BeanCounterTask.MovingToPortal;
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

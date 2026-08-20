using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// What a Gremlin is currently doing — decided every frame by priority
    /// (see EvaluateAndAct). Happiness gates everything: Leaving overrides
    /// every other tier (see TickLeaving), Unhappy/Angry refuse productive
    /// tasks and periodically lash out instead (see TickHostile),
    /// GettingUnhappy just refuses tasks. Otherwise, highest priority wins:
    /// 100 no personal Lair -> claim/create one, 80 hungry -> eat Bacon,
    /// 40 Training Room available -> train, 30 otherwise -> roam. Training
    /// isn't a single stationary state — see TickTraining/TryMoveToNextDummy
    /// — it alternates between MovingToTraining (walking to the next
    /// dummy) and pausing in place for a few seconds, the same
    /// MovingToTraining/Training pair just repeating with a new target each
    /// cycle (same shape as Warlock's Researching).
    public enum GremlinTask
    {
        Idle,
        MovingToLairSpot,
        MovingToFood,
        MovingToTraining,
        Training,
        MovingToRoam,
        RoamPausing,
        MovingToAttackTarget,
        Attacking,
        MovingToPortal
    }

    /// The first non-Imp creature — "a small, thin, green-blue-ish humanoid"
    /// per the brief. Visual is a placeholder capsule ("a green pill") until
    /// a real model exists. Behavior is a priority list (see GremlinTask):
    /// claim/build a Lair to rest in, eat when hungry, otherwise train (or
    /// roam if no Training Room exists) — see EvaluateAndAct. Happiness
    /// (see design-doc.md's Happiness section) can override all of that.
    public class GremlinAgent : MonoBehaviour
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool (see Portal.SeedPool/TryTakeFromPool and
        /// GremlinSpawner.TryRecruitGremlin).
        public const string CreatureKind = "Gremlin";

        private static int _nextId;
        private static readonly List<GremlinAgent> _all = new List<GremlinAgent>();

        /// Every currently-alive Gremlin — for debug/inspection UI only,
        /// same convention ImplingAgent.All uses.
        public static IReadOnlyList<GremlinAgent> All => _all;

        public int Id { get; private set; }
        public Vector3 Position => transform.position;

        /// A random name from CreatureNames.GremlinNames, picked once at
        /// spawn (see Awake) and kept for life — plus the numeric Id, in
        /// case two Gremlins roll the same name out of the 50-name pool.
        public string Name => _name;
        private string _name;

        public GremlinTask Task => _task;

        /// Level/stats/skill slots, per design-doc.md's Creatures section.
        /// Read-only from the outside; ticked internally.
        public Creature Creature => _creature;

        /// Read-only from the outside — ticked internally, same convention
        /// Creature uses. Imps don't have this (see Hunger's own header).
        public Hunger Hunger => _hunger;

        /// Read-only from the outside — ticked internally, same convention
        /// Hunger uses. Imps don't have this either (see Pay's own header).
        public Pay Pay => _pay;

        /// Read-only from the outside — ticked internally, driven by Hunger
        /// and Pay (see Happiness's own header). Imps don't have this.
        public Happiness Happiness => _happiness;

        // 80 starting HP per the brief. Movespeed/Strength/Attackspeed have
        // no design-brief values yet — placeholders just so movement and
        // the Unhappy/Angry attack behavior (see TryBeginAttackWall) work
        // at all. Every other stat sits at 0.
        [SerializeField]
        private CreatureStatBlock _baseStats = new CreatureStatBlock
        {
            MaxHP = 80f,
            HPRegen = 1f,
            Movespeed = 3.5f,
            Strength = 15f,
            Attackspeed = 0.8f
        };

        [SerializeField]
        private CreatureStatBlock _growthPerLevel = new CreatureStatBlock
        {
            MaxHP = 8f,
            HPRegen = 0.2f
        };

        // Exp needed per level is Level * _expPerLevelStep (see
        // Creature.ExpToNextLevel) — same default as the Imp's for now,
        // until Gremlin's actual leveling pace is designed (e.g. a
        // rarer/stronger creature could be given a higher step here to
        // level up slower).
        [SerializeField] private int _expPerLevelStep = 100;

        [SerializeField] private float _roamPauseDuration = 2f;

        // How long a training Gremlin lingers at one dummy before wandering
        // to another — randomized per stop within this range so the room
        // doesn't read as a metronome, same idea Warlock's bookcase pause
        // uses for research. Exp still ticks on its own fixed
        // TrainingRoomManager.TrainingTickSeconds cadence throughout,
        // independent of this pause length.
        [SerializeField] private float _minTrainPauseSeconds = 3f;
        [SerializeField] private float _maxTrainPauseSeconds = 5f;

        // How often a refusing (Unhappy/Angry) Gremlin re-rolls whether to
        // lash out, and the odds each time — "occasionally" vs "often" per
        // the design brief. All placeholder tuning.
        [SerializeField] private float _attackCheckIntervalSeconds = 8f;
        [SerializeField] private float _unhappyAttackChance = 0.25f;
        [SerializeField] private float _angryAttackChance = 0.6f;

        private Creature _creature;
        private readonly Hunger _hunger = new Hunger();
        private readonly Pay _pay = new Pay();
        private readonly Happiness _happiness = new Happiness();

        private DungeonGrid _grid;
        private LairManager _lairManager;
        private BaconBeaconManager _baconBeaconManager;
        private TrainingRoomManager _trainingRoomManager;
        private TreasuryManager _treasuryManager;
        private Portal _portal;

        private GremlinTask _task = GremlinTask.Idle;
        private string _myLairRoomId;
        private Vector2Int _myLairCoord;
        private Vector2Int _lairTargetCoord;
        private Vector2Int _foodTargetCoord;
        private Vector2Int _trainingTargetCoord;
        private Vector2Int _roamTargetCoord;
        private Vector2Int _attackTargetCoord;
        private bool _attackTargetIsRoom;
        private float _trainTimer;
        private float _trainPauseTimer;
        private float _trainPauseDuration;
        private float _roamPauseTimer;
        private float _attackCheckTimer;
        private float _attackHitTimer;

        private readonly List<Vector2Int> _gridPathBuffer = new List<Vector2Int>();
        private readonly List<Vector3> _waypoints = new List<Vector3>();
        private int _waypointIndex;

        private void Awake()
        {
            Id = _nextId++;
            _all.Add(this);
            _name = $"{CreatureNames.GetRandom(CreatureNames.GremlinNames)} #{Id}";

            _creature = new Creature(_baseStats, _growthPerLevel, _expPerLevelStep);
        }

        public void Initialize(DungeonGrid grid, LairManager lairManager, BaconBeaconManager baconBeaconManager, TrainingRoomManager trainingRoomManager, TreasuryManager treasuryManager, Portal portal)
        {
            _grid = grid;
            _lairManager = lairManager;
            _baconBeaconManager = baconBeaconManager;
            _trainingRoomManager = trainingRoomManager;
            _treasuryManager = treasuryManager;
            _portal = portal;
            _lairManager.RoomSold += OnLairSold;
        }

        private void Update()
        {
            _creature.Tick(Time.deltaTime);
            _hunger.Tick(Time.deltaTime);
            _happiness.Tick(Time.deltaTime, _hunger.IsHungry);
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

        /// Payday — draws this Gremlin's wage (see Pay.WageFor) straight
        /// out of the Treasury, no walking/task involved (unlike eating,
        /// which needs a Bacon Beacon trip). Going unpaid marks it unhappy
        /// (Pay.IsUnhappy) and now also actually dents Happiness (see
        /// Happiness.ApplyUnpaidPenalty), unlike before.
        private void TryGetPaid()
        {
            var wage = Pay.WageFor(_creature.Level);
            if (_treasuryManager != null && _treasuryManager.TrySpendGold(wage))
            {
                _pay.MarkPaid();
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

                // Whatever Lair tile this Gremlin had claimed frees up when
                // it stops existing, whatever the reason (left through the
                // Portal, or any future death path) — otherwise the tile
                // would stay permanently claimed by nothing.
                if (_myLairRoomId != null)
                {
                    _lairManager.ReleaseLairTile(_myLairCoord);
                }
            }
        }

        /// A Lair sold out from under this Gremlin (whether or not it was
        /// the one it had claimed) — cheap to just clear unconditionally
        /// and let priority 100 re-check next frame rather than tracking
        /// which roomId this was.
        private void OnLairSold(string roomId)
        {
            if (roomId == _myLairRoomId)
            {
                _myLairRoomId = null;
            }
        }

        /// Priority ladder, highest first. Happiness gates everything else:
        /// Leaving (0-10) overrides every other tier outright — see
        /// TickLeaving. Unhappy/Angry (10-40) refuse the productive tiers
        /// below (40/30) and periodically attack instead — see TickHostile
        /// — but still eat/claim a Lair, since those aren't "tasks."
        /// GettingUnhappy (40-50) just refuses the productive tiers. Happy
        /// and above behaves exactly as before.
        private void EvaluateAndAct()
        {
            // An attack already in progress always finishes, regardless of
            // which tier the creature is in *now* — mood can recover
            // mid-swing, and the tier-40/30 switch below has no case for
            // these two states, so without this check a recovered Gremlin
            // would orphan mid-attack instead of resuming normal behavior.
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

            // Mood recovered mid-walk to the Portal — call off leaving
            // (unlike an attack, a walk in progress is fine to interrupt;
            // nothing's happened yet). Falls through to re-evaluate fresh
            // below in the same frame.
            if (_task == GremlinTask.MovingToPortal)
            {
                SetTask(GremlinTask.Idle);
            }

            // Tier 100: no personal Lair claimed yet.
            if (_myLairRoomId == null && _task != GremlinTask.MovingToLairSpot)
            {
                if (TryBeginPursueLair())
                {
                    return;
                }
            }

            if (_task == GremlinTask.MovingToLairSpot)
            {
                MoveAlongPathThen(ArriveAtLairSpot);
                return;
            }

            // Tier 80: hungry.
            if (_hunger.IsHungry && _task != GremlinTask.MovingToFood)
            {
                if (TryBeginPursueFood())
                {
                    return;
                }
            }

            if (_task == GremlinTask.MovingToFood)
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
                    SetTask(GremlinTask.Idle);
                }
                return;
            }

            // Tier 40/30: train if a Training Room exists, otherwise roam.
            if (_task == GremlinTask.Idle)
            {
                TryBeginTrainOrRoam();
            }

            switch (_task)
            {
                case GremlinTask.MovingToTraining:
                    MoveAlongPathThen(ArriveAtTraining);
                    break;
                case GremlinTask.Training:
                    TickTraining();
                    break;
                case GremlinTask.MovingToRoam:
                    MoveAlongPathThen(ArriveAtRoam);
                    break;
                case GremlinTask.RoamPausing:
                    TickRoamPause();
                    break;
            }
        }

        /// Happiness 0-10 — heads for the Portal to leave for good,
        /// overriding every other concern (even hunger/Lair). If it can't
        /// find a route there at all, "begins destroying the domain" —
        /// falls back to the same attack loop Unhappy/Angry use, but
        /// unconditionally (forced: true) rather than an occasional roll.
        private void TickLeaving()
        {
            if (_task == GremlinTask.MovingToPortal)
            {
                MoveAlongPathThen(ArriveAtPortal);
                return;
            }

            if (TryBeginPursuePortal())
            {
                return;
            }

            // No route to the Portal at all — "begins destroying the
            // domain." TickInProgressAttack (called at the top of
            // EvaluateAndAct) already carries any attack this kicks off
            // through to completion on later frames; this only needs to
            // roll/start a new one.
            TickHostile(forced: true, HappinessTier.Angry);
        }

        private bool TryBeginPursuePortal()
        {
            if (_portal == null || !PlanPathTo(_portal.Coord, _grid.GridToWorld(_portal.Coord)))
            {
                return false;
            }

            SetTask(GremlinTask.MovingToPortal);
            return true;
        }

        private void ArriveAtPortal()
        {
            GameplayLog.Write($"{Name} walked up the Portal stairs and left the domain for good");
            Destroy(gameObject);
        }

        /// Whether an attack that's already under way (walking to the
        /// target, or mid-hits on a wall) continues this frame — called
        /// unconditionally at the top of EvaluateAndAct so an attack always
        /// runs to completion even if the creature's mood changes tier
        /// mid-attack (see EvaluateAndAct's own comment).
        private bool TickInProgressAttack()
        {
            if (_task == GremlinTask.MovingToAttackTarget)
            {
                MoveAlongPathThen(ArriveAtAttackTarget);
                return true;
            }

            if (_task == GremlinTask.Attacking)
            {
                TickAttacking();
                return true;
            }

            return false;
        }

        /// Shared by both the Unhappy/Angry "occasionally/often lash out"
        /// behavior and Leaving's "no path out, destroy the domain"
        /// fallback — forced skips the chance roll (always attacks once
        /// the check interval passes) since there's nothing else left to
        /// do in that case. Only ever reached with _task at Idle (any
        /// attack in progress is handled by TickInProgressAttack before
        /// this is called), so it's purely "should a new attack start."
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

        /// Picks a wall or a room to go smash, 50/50 when both are
        /// reachable, falling back to whichever kind is if only one is.
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

        /// Any reachable Rock tile bordering a walkable floor tile — no
        /// resource/reinforced distinction, an angry Gremlin isn't picky.
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
            SetTask(GremlinTask.MovingToAttackTarget);
            return true;
        }

        /// Any reachable tile belonging to any room (Lair, Treasury,
        /// whatever) — see TickAttackingRoom for what actually happens to
        /// it once this Gremlin arrives and starts hitting it.
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
            SetTask(GremlinTask.MovingToAttackTarget);
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
            SetTask(GremlinTask.Attacking);
            _attackHitTimer = 0f;
        }

        /// Same Strength/Attackspeed-driven hit cadence as the Imp's own
        /// "Mine" basic attack (see ImplingAgent.MineHitInterval/
        /// MineHitDamage) — first real use of these stats on a Gremlin.
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

        /// Chips away at the target tile's room HP (see TileState.RoomMaxHp/
        /// DungeonGrid.ApplyRoomDamage) — once that hits 0, the whole room
        /// is torn down via LairManager.TrySellRoom, the one correct way to
        /// do that for any room type (cleans up every manager's own tile
        /// list/visuals/structures). There's no partial-room removal: this
        /// tile depleting takes the whole room with it, not just itself.
        private void TickAttackingRoom()
        {
            if (!_grid.GetTile(_attackTargetCoord).HasRoom)
            {
                // Already gone (sold, or destroyed by another attacker
                // hitting the same tile) — nothing left to hit.
                SetTask(GremlinTask.Idle);
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
                _lairManager.TrySellRoom(_attackTargetCoord);
                GameplayLog.Write($"{Name} ({_happiness.Tier}) destroyed a room at ({_attackTargetCoord.x},{_attackTargetCoord.y})");
                SetTask(GremlinTask.Idle);
            }
        }

        private void TickAttackingWall()
        {
            if (_grid.GetTile(_attackTargetCoord).Type != TileType.Rock)
            {
                // Already gone (e.g. an Imp finished digging it out from
                // under this attack) — nothing left to hit.
                SetTask(GremlinTask.Idle);
                return;
            }

            _attackHitTimer += Time.deltaTime;
            if (_attackHitTimer < AttackHitInterval)
            {
                return;
            }

            _attackHitTimer -= AttackHitInterval;
            var destroyed = _grid.ApplyDigDamage(_attackTargetCoord, AttackHitDamage, out _, out _);
            if (destroyed)
            {
                GameplayLog.Write($"{Name} ({_happiness.Tier}) smashed a wall at ({_attackTargetCoord.x},{_attackTargetCoord.y})");
                SetTask(GremlinTask.Idle);
            }
        }

        /// Prefers walking to an existing unclaimed Lair (e.g. the starting
        /// one from GameBootstrap, or one placed by the player) over
        /// building a brand-new one — only falls back to
        /// TryFindRandomLairSpot if no unclaimed Lair is reachable at all.
        /// See ArriveAtLairSpot for what happens once it gets there.
        private bool TryBeginPursueLair()
        {
            var fromCoord = _grid.WorldToGrid(transform.position);

            if (_lairManager.TryFindNearestUnclaimedLairTile(fromCoord, out var existingCoord) && PlanPathTo(existingCoord, _grid.GridToWorld(existingCoord)))
            {
                _lairTargetCoord = existingCoord;
                SetTask(GremlinTask.MovingToLairSpot);
                return true;
            }

            if (TryFindRandomLairSpot(out var newCoord) && PlanPathTo(newCoord, _grid.GridToWorld(newCoord)))
            {
                _lairTargetCoord = newCoord;
                SetTask(GremlinTask.MovingToLairSpot);
                return true;
            }

            return false;
        }

        /// _lairTargetCoord is either an existing unclaimed Lair tile (just
        /// claim it) or a plain buildable tile with no room on it yet
        /// (place a brand-new 1x1 Lair there first, then claim it) — see
        /// TryBeginPursueLair for which. Claiming is per-tile (see
        /// LairManager.TryClaimLairTile), not per-room, so this only ever
        /// takes the one tile it walked to, not the whole Lair.
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

            SetTask(GremlinTask.Idle);
        }

        private bool TryBeginPursueFood()
        {
            if (_baconBeaconManager == null || !_baconBeaconManager.TryFindNearestTileWithBacon(_grid.WorldToGrid(transform.position), out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return false;
            }

            _foodTargetCoord = coord;
            SetTask(GremlinTask.MovingToFood);
            return true;
        }

        private void ArriveAtFood()
        {
            if (_baconBeaconManager.TryEatBacon(_foodTargetCoord, BaconBeaconManager.MealBaconAmount))
            {
                _hunger.Eat();
            }

            SetTask(GremlinTask.Idle);
        }

        private void TryBeginTrainOrRoam()
        {
            if (_trainingRoomManager != null
                && _trainingRoomManager.TryFindNearestDummyTile(_grid.WorldToGrid(transform.position), out var trainingCoord)
                && PlanPathTo(trainingCoord, _grid.GridToWorld(trainingCoord)))
            {
                _trainingTargetCoord = trainingCoord;
                SetTask(GremlinTask.MovingToTraining);
                return;
            }

            TryBeginRoam();
        }

        private void ArriveAtTraining()
        {
            SetTask(GremlinTask.Training);
            _trainTimer = 0f;
            _trainPauseTimer = 0f;
            _trainPauseDuration = UnityEngine.Random.Range(_minTrainPauseSeconds, _maxTrainPauseSeconds);
        }

        /// Exp ticks on its own fixed cadence the whole time this Gremlin is
        /// Training, regardless of how long it lingers at any one dummy.
        /// Once the randomized pause for the current dummy is up, it
        /// wanders off toward a different one (see TryMoveToNextDummy) —
        /// same "stop for a few seconds, then move to another" pattern
        /// Warlock's Researching uses.
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

        /// Picks a different reachable training-dummy tile and heads there
        /// — falls back to Idle (re-evaluated next frame) if none is
        /// reachable any more, e.g. the Training Room was sold out from
        /// under this Gremlin mid-training.
        private void TryMoveToNextDummy()
        {
            if (_trainingRoomManager != null
                && _trainingRoomManager.TryFindRandomDummyTile(_grid.WorldToGrid(transform.position), _trainingTargetCoord, out var coord)
                && PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                _trainingTargetCoord = coord;
                SetTask(GremlinTask.MovingToTraining);
                return;
            }

            SetTask(GremlinTask.Idle);
        }

        /// "Roam the dungeon to random spots" — picks any reachable walkable
        /// floor tile, not just room tiles, since this is meant to look like
        /// aimless wandering ("to find combat") rather than heading anywhere
        /// specific.
        private void TryBeginRoam()
        {
            var fromCoord = _grid.WorldToGrid(transform.position);
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            if (!TryPickRandomCoord(distances.Keys, out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return;
            }

            _roamTargetCoord = coord;
            SetTask(GremlinTask.MovingToRoam);
        }

        private void ArriveAtRoam()
        {
            SetTask(GremlinTask.RoamPausing);
            _roamPauseTimer = 0f;
        }

        private void TickRoamPause()
        {
            _roamPauseTimer += Time.deltaTime;
            if (_roamPauseTimer >= _roamPauseDuration)
            {
                SetTask(GremlinTask.Idle);
            }
        }

        /// Every Claimed, buildable, room-free Floor tile reachable from
        /// this Gremlin's current position — same CanBuildRoomOn rule every
        /// room placement (LairManager included) already funnels through.
        private bool TryFindRandomLairSpot(out Vector2Int coord)
        {
            var fromCoord = _grid.WorldToGrid(transform.position);
            var distances = _grid.GetReachableFloorDistances(fromCoord);

            var candidates = new List<Vector2Int>();
            foreach (var candidate in distances.Keys)
            {
                if (_grid.CanBuildRoomOn(candidate))
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

        private void SetTask(GremlinTask newTask)
        {
            _task = newTask;
        }

        // Same A*-planned-route movement ImplingAgent uses (see its own
        // PlanPathTo/MoveAlongPathThen for the full rationale) — duplicated
        // here rather than shared, matching how GremlinSpawner/WarlockSpawner
        // are already duplicated rather than sharing a base.
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
    }
}

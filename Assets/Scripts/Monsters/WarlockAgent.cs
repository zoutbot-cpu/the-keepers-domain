using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// What a Warlock is currently doing — decided every frame by priority
    /// (see EvaluateAndAct). Happiness gates everything: Leaving overrides
    /// every other tier (see TickLeaving), Unhappy/Angry refuse productive
    /// tasks and periodically lash out instead (see TickHostile),
    /// GettingUnhappy just refuses tasks. Otherwise, highest priority wins:
    /// 100 no personal Lair -> claim/create one, 80 hungry -> eat Bacon,
    /// 40 Library available -> research, 30 otherwise (no Library) -> train
    /// in a Training Room instead. Idle if none of those apply (unlike
    /// Gremlin, Warlock has no roam fallback). Neither Researching nor
    /// Training is a single stationary state — see TickResearching/
    /// TryMoveToNextBookcase and TickTraining/TryMoveToNextDummy — each
    /// alternates between walking to the next bookcase/dummy and pausing in
    /// place for a few seconds, the same Moving.../... pair just repeating
    /// with a new target each cycle.
    public enum WarlockTask
    {
        Idle,
        MovingToLairSpot,
        MovingToFood,
        MovingToResearch,
        Researching,
        MovingToTraining,
        Training,
        MovingToAttackTarget,
        Attacking,
        MovingToPortal
    }

    /// The second non-Imp creature, and the first "intelligent" one per the
    /// design doc (see Library's design-doc entry — Tavern's "food
    /// for intelligent creatures" line was written with this creature in
    /// mind). Recruited the same "join via the Portal's pool" way a Gremlin
    /// is (see WarlockSpawner). Behavior is a priority list (see
    /// WarlockTask): claim/build a Lair to rest in, eat when hungry,
    /// otherwise research in a Library (or train in a Training Room if no
    /// Library exists) — see EvaluateAndAct. Happiness (see design-doc.md's
    /// Happiness section) can override all of that.
    public class WarlockAgent : MonoBehaviour, ICombatant
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool (see Portal.SeedPool/TryTakeFromPool and
        /// WarlockSpawner.TryRecruitWarlock).
        public const string CreatureKind = "Warlock";

        private static int _nextId;
        private static readonly List<WarlockAgent> _all = new List<WarlockAgent>();

        /// Every currently-alive Warlock — for debug/inspection UI only,
        /// same convention ImplingAgent.All/GremlinAgent.All use.
        public static IReadOnlyList<WarlockAgent> All => _all;

        /// How many currently-alive Warlocks belong to ownerId — spawner
        /// population caps are per-keeper now (see WarlockSpawner.
        /// MeetsJoinRequirements).
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

        /// A random name from CreatureNames.WarlockNames, picked once at
        /// spawn (see Awake) and kept for life — plus the numeric Id, in
        /// case two Warlocks roll the same name out of the 50-name pool.
        public string Name => _name;
        private string _name;

        public WarlockTask Task => _task;

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

        /// Creature-vs-creature combat — see design-doc.md's Combat section
        /// and GremlinAgent for the shared wiring.
        public Combatant Combat => _combat;
        public bool IsImp => false;
        public string Species => CreatureKind;
        private readonly Combatant _combat = new Combatant();

        // 60 starting HP per the brief. Movespeed/Strength/Attackspeed have
        // no design-brief values yet — placeholders (slower/weaker than
        // Gremlin's, reads as a heavier caster-type) just so movement and
        // the Unhappy/Angry attack behavior (see TryBeginAttackWall) work
        // at all.
        [SerializeField]
        private CreatureStatBlock _baseStats = new CreatureStatBlock
        {
            MaxHP = 60f,
            HPRegen = 1f,
            Movespeed = 2.5f,
            Strength = 10f,
            Attackspeed = 0.6f
        };

        // Basic per-level growth — same "+10% Strength, +7.5% Attackspeed,
        // +5% Movespeed per level, +1 Armor by level 10" ratios as Gremlin's
        // own growth block, scaled off this creature's own (lower) base
        // stats — no design-brief curve exists yet.
        [SerializeField]
        private CreatureStatBlock _growthPerLevel = new CreatureStatBlock
        {
            MaxHP = 8f,
            HPRegen = 0.2f,
            Strength = 1f,
            Movespeed = 0.125f,
            Attackspeed = 0.045f,
            Armor = 1f / 9f
        };

        // Same default as Gremlin's for now — see design-doc.md's leveling
        // note ("exact values per creature: TBD").
        [SerializeField] private int _expPerLevelStep = 100;

        // How long a researching Warlock lingers at one bookcase before
        // wandering to another — randomized per stop within this range so
        // the room doesn't read as a metronome. Exp still ticks on its own
        // fixed LibraryManager.ResearchTickSeconds cadence throughout,
        // independent of this pause length.
        [SerializeField] private float _minBookcasePauseSeconds = 3f;
        [SerializeField] private float _maxBookcasePauseSeconds = 5f;

        // Same idea as the bookcase pause above, but for training at a
        // dummy when no Library exists (see TryBeginResearchOrTrain).
        [SerializeField] private float _minTrainPauseSeconds = 3f;
        [SerializeField] private float _maxTrainPauseSeconds = 5f;

        // How often a refusing (Unhappy/Angry) Warlock re-rolls whether to
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
        private TavernManager _tavernManager;
        private LibraryManager _libraryManager;
        private TrainingRoomManager _trainingRoomManager;
        private TreasuryManager _treasuryManager;
        private Portal _portal;

        private WarlockTask _task = WarlockTask.Idle;
        private string _myLairRoomId;
        private Vector2Int _myLairCoord;
        private Vector2Int _lairTargetCoord;
        private Vector2Int _foodTargetCoord;
        private Vector2Int _researchTargetCoord;
        private Vector2Int _trainingTargetCoord;
        private Vector2Int _attackTargetCoord;
        private bool _attackTargetIsRoom;
        private float _researchTimer;
        private float _researchPauseTimer;
        private float _researchPauseDuration;
        private float _trainTimer;
        private float _trainPauseTimer;
        private float _trainPauseDuration;
        private float _attackCheckTimer;
        private float _attackHitTimer;

        // Set by MinionGrabController when the player's Grab hand drops
        // this Warlock onto a Training Room tile — see SetTrainingPriority.
        private bool _hasTrainingPriority;

        private readonly List<Vector2Int> _gridPathBuffer = new List<Vector2Int>();
        private readonly List<Vector3> _waypoints = new List<Vector3>();
        private int _waypointIndex;

        // The goal last handed to PlanPathTo — cached so
        // ReplanPathFromCurrentPosition can re-run the exact same call
        // after this Warlock's position changes out from under it (see
        // MinionGrabController), without needing to know which task kind
        // that goal belonged to.
        private Vector2Int _lastGoalCoord;
        private Vector3 _lastGoalWorldPos;

        private void Awake()
        {
            Id = _nextId++;
            _all.Add(this);
            _name = $"{CreatureNames.GetRandom(CreatureNames.WarlockNames)} #{Id}";

            _creature = new Creature(_baseStats, _growthPerLevel, _expPerLevelStep);
        }

        public void Initialize(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, LibraryManager libraryManager, TrainingRoomManager trainingRoomManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            _grid = grid;
            _lairManager = lairManager;
            _tavernManager = tavernManager;
            _libraryManager = libraryManager;
            _trainingRoomManager = trainingRoomManager;
            _treasuryManager = treasuryManager;
            _portal = portal;
            _creature.SetOwner(ownerId);
            CreatureHealthRing.Attach(gameObject, _creature, grid);
            _lairManager.RoomSold += OnLairSold;

            _combat.Initialize(this, this, grid, _creature, _hunger, _happiness,
                KeepersDomain.Core.KeeperContext.ForOwner(ownerId)?.ThroneCoord ?? grid.WorldToGrid(transform.position),
                () => _myLairRoomId != null ? _myLairCoord : (Vector2Int?)null,
                () => SetTask(WarlockTask.Idle),
                isImp: false);
        }

        private void Update()
        {
            _creature.Tick(Time.deltaTime);
            _hunger.Tick(Time.deltaTime);
            _happiness.Tick(Time.deltaTime, _hunger.IsHungry, _task == WarlockTask.Researching && !_combat.InCombat);
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

        /// Payday — draws this Warlock's wage (see Pay.WageFor) straight
        /// out of the Treasury, no walking/task involved (unlike eating,
        /// which needs a Tavern trip). A successful payment now also
        /// bumps Happiness (Happiness.ApplyPaidBonus); going unpaid marks it
        /// unhappy (Pay.IsUnhappy) and dents Happiness instead
        /// (Happiness.ApplyUnpaidPenalty).
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

                // Whatever Lair tile this Warlock had claimed frees up when
                // it stops existing, whatever the reason (left through the
                // Portal, or any future death path) — otherwise the tile
                // would stay permanently claimed by nothing.
                if (_myLairRoomId != null)
                {
                    _lairManager.ReleaseLairTile(_myLairCoord);
                }
            }
        }

        /// A Lair sold out from under this Warlock (whether or not it was
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
            // these two states, so without this check a recovered Warlock
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
            if (_task == WarlockTask.MovingToPortal)
            {
                SetTask(WarlockTask.Idle);
            }

            // Tier 100: no personal Lair claimed yet.
            if (_myLairRoomId == null && _task != WarlockTask.MovingToLairSpot)
            {
                if (TryBeginPursueLair())
                {
                    return;
                }
            }

            if (_task == WarlockTask.MovingToLairSpot)
            {
                MoveAlongPathThen(ArriveAtLairSpot);
                return;
            }

            // Tier 80: hungry.
            if (_hunger.IsHungry && _task != WarlockTask.MovingToFood)
            {
                if (TryBeginPursueFood())
                {
                    return;
                }
            }

            if (_task == WarlockTask.MovingToFood)
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
                    SetTask(WarlockTask.Idle);
                }
                return;
            }

            // Tier 40/30: research if a Library exists, otherwise train in
            // a Training Room; Idle if neither exists (no further fallback).
            if (_task == WarlockTask.Idle)
            {
                TryBeginResearchOrTrain();
            }

            switch (_task)
            {
                case WarlockTask.MovingToResearch:
                    MoveAlongPathThen(ArriveAtResearch);
                    break;
                case WarlockTask.Researching:
                    TickResearching();
                    break;
                case WarlockTask.MovingToTraining:
                    MoveAlongPathThen(ArriveAtTraining);
                    break;
                case WarlockTask.Training:
                    TickTraining();
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
            if (_task == WarlockTask.MovingToPortal)
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

            SetTask(WarlockTask.MovingToPortal);
            return true;
        }

        private void ArriveAtPortal()
        {
            GameplayLog.Write(_creature.OwnerId, $"{Name} walked up the Portal stairs and left the domain for good");
            Destroy(gameObject);
        }

        /// Whether an attack that's already under way (walking to the
        /// target, or mid-hits on a wall) continues this frame — called
        /// unconditionally at the top of EvaluateAndAct so an attack always
        /// runs to completion even if the creature's mood changes tier
        /// mid-attack (see EvaluateAndAct's own comment).
        private bool TickInProgressAttack()
        {
            if (_task == WarlockTask.MovingToAttackTarget)
            {
                MoveAlongPathThen(ArriveAtAttackTarget);
                return true;
            }

            if (_task == WarlockTask.Attacking)
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
        /// resource/reinforced distinction, an angry Warlock isn't picky.
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
            SetTask(WarlockTask.MovingToAttackTarget);
            return true;
        }

        /// Any reachable tile belonging to any room (Lair, Treasury,
        /// whatever) — see TickAttackingRoom for what actually happens to
        /// it once this Warlock arrives and starts hitting it.
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
            SetTask(WarlockTask.MovingToAttackTarget);
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
            SetTask(WarlockTask.Attacking);
            _attackHitTimer = 0f;
        }

        /// Same Strength/Attackspeed-driven hit cadence as the Imp's own
        /// "Mine" basic attack (see ImplingAgent.MineHitInterval/
        /// MineHitDamage) — first real use of these stats on a Warlock.
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
                SetTask(WarlockTask.Idle);
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
                SetTask(WarlockTask.Idle);
            }
        }

        private void TickAttackingWall()
        {
            if (_grid.GetTile(_attackTargetCoord).Type != TileType.Rock)
            {
                // Already gone (e.g. an Imp finished digging it out from
                // under this attack) — nothing left to hit.
                SetTask(WarlockTask.Idle);
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
                SetTask(WarlockTask.Idle);
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
                SetTask(WarlockTask.MovingToLairSpot);
                return true;
            }

            if (TryFindRandomLairSpot(out var newCoord) && PlanPathTo(newCoord, _grid.GridToWorld(newCoord)))
            {
                _lairTargetCoord = newCoord;
                SetTask(WarlockTask.MovingToLairSpot);
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
                GameplayLog.Write(_creature.OwnerId, $"{Name} claimed a Lair tile at ({_lairTargetCoord.x},{_lairTargetCoord.y})");
            }

            SetTask(WarlockTask.Idle);
        }

        private bool TryBeginPursueFood()
        {
            if (_tavernManager == null || !_tavernManager.TryFindNearestTileWithBacon(_grid.WorldToGrid(transform.position), out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return false;
            }

            _foodTargetCoord = coord;
            SetTask(WarlockTask.MovingToFood);
            return true;
        }

        private void ArriveAtFood()
        {
            if (_tavernManager.TryEatBacon(_foodTargetCoord, TavernManager.MealBaconAmount))
            {
                _hunger.Eat();
            }

            SetTask(WarlockTask.Idle);
        }

        /// Set by MinionGrabController when the player's Grab hand drops
        /// this Warlock onto a Training Room tile — "throw it at the
        /// Training Room to make it actually go train." Flips the tier-40
        /// pick below from its default Research-first order (see
        /// TryBeginResearchOrTrain) to Training-first instead, without
        /// touching anything above it: an unclaimed Lair (100) and hunger
        /// (80) still come first, and the Happiness mood gate still
        /// applies exactly as before — this only reorders which of this
        /// Warlock's own productive tasks it reaches for.
        ///
        /// If this Warlock is already walking to (or working at) a
        /// bookcase when the flag flips on, that's now the wrong choice —
        /// drop it back to Idle so EvaluateAndAct re-derives it fresh next
        /// frame with the new priority applied (Lair/hunger/mood are
        /// re-checked first regardless, same as any other Idle frame, so
        /// this can't jump the queue above them). Already-training is left
        /// alone — the flag has nothing to correct there.
        public void SetTrainingPriority(bool hasPriority)
        {
            _hasTrainingPriority = hasPriority;

            if (hasPriority && _task is WarlockTask.MovingToResearch or WarlockTask.Researching)
            {
                SetTask(WarlockTask.Idle);
            }
        }

        /// Research (Library) first, Training (Training Room) as the
        /// fallback if no Library exists — unless _hasTrainingPriority
        /// flips that order (see SetTrainingPriority), in which case
        /// Training is tried first instead. Idle if neither is available,
        /// same either way.
        private void TryBeginResearchOrTrain()
        {
            if (_hasTrainingPriority)
            {
                _ = TryBeginTrain() || TryBeginResearch();
            }
            else
            {
                _ = TryBeginResearch() || TryBeginTrain();
            }
        }

        private bool TryBeginResearch()
        {
            if (_libraryManager == null
                || !_libraryManager.TryFindNearestBookcaseTile(_grid.WorldToGrid(transform.position), out var libraryCoord)
                || !PlanPathTo(libraryCoord, _grid.GridToWorld(libraryCoord)))
            {
                return false;
            }

            _researchTargetCoord = libraryCoord;
            SetTask(WarlockTask.MovingToResearch);
            return true;
        }

        private bool TryBeginTrain()
        {
            if (_trainingRoomManager == null
                || !_trainingRoomManager.TryFindNearestDummyTile(_grid.WorldToGrid(transform.position), out var trainingCoord)
                || !PlanPathTo(trainingCoord, _grid.GridToWorld(trainingCoord)))
            {
                return false;
            }

            _trainingTargetCoord = trainingCoord;
            SetTask(WarlockTask.MovingToTraining);
            return true;
        }

        private void ArriveAtResearch()
        {
            SetTask(WarlockTask.Researching);
            _researchTimer = 0f;
            _researchPauseTimer = 0f;
            _researchPauseDuration = UnityEngine.Random.Range(_minBookcasePauseSeconds, _maxBookcasePauseSeconds);
        }

        /// Exp ticks on its own fixed cadence the whole time this Warlock is
        /// Researching, regardless of how long it lingers at any one
        /// bookcase. Once the randomized pause for the current bookcase is
        /// up, it wanders off toward a different one (see
        /// TryMoveToNextBookcase) — "stopping for 3-5 seconds at a
        /// bookcase, then moving on to another," per the brief.
        private void TickResearching()
        {
            _researchTimer += Time.deltaTime;
            if (_researchTimer >= LibraryManager.ResearchTickSeconds)
            {
                _researchTimer -= LibraryManager.ResearchTickSeconds;
                _creature.AddExp(LibraryManager.ResearchExpPerTick);
            }

            _researchPauseTimer += Time.deltaTime;
            if (_researchPauseTimer >= _researchPauseDuration)
            {
                TryMoveToNextBookcase();
            }
        }

        /// Picks a different reachable bookcase-adjacent Library tile and
        /// heads there — falls back to Idle (re-evaluated next frame) if
        /// none is reachable any more, e.g. the Library was sold out from
        /// under this Warlock mid-research.
        private void TryMoveToNextBookcase()
        {
            if (_libraryManager != null
                && _libraryManager.TryFindRandomBookcaseTile(_grid.WorldToGrid(transform.position), _researchTargetCoord, out var coord)
                && PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                _researchTargetCoord = coord;
                SetTask(WarlockTask.MovingToResearch);
                return;
            }

            SetTask(WarlockTask.Idle);
        }

        private void ArriveAtTraining()
        {
            SetTask(WarlockTask.Training);
            _trainTimer = 0f;
            _trainPauseTimer = 0f;
            _trainPauseDuration = UnityEngine.Random.Range(_minTrainPauseSeconds, _maxTrainPauseSeconds);
        }

        /// Exp ticks on its own fixed cadence the whole time this Warlock is
        /// Training, regardless of how long it lingers at any one dummy.
        /// Once the randomized pause for the current dummy is up, it
        /// wanders off toward a different one (see TryMoveToNextDummy) —
        /// same "stop for a few seconds, then move to another" pattern
        /// Researching uses for bookcases.
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
        /// under this Warlock mid-training.
        private void TryMoveToNextDummy()
        {
            if (_trainingRoomManager != null
                && _trainingRoomManager.TryFindRandomDummyTile(_grid.WorldToGrid(transform.position), _trainingTargetCoord, out var coord)
                && PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                _trainingTargetCoord = coord;
                SetTask(WarlockTask.MovingToTraining);
                return;
            }

            SetTask(WarlockTask.Idle);
        }

        /// Every Claimed, buildable, room-free Floor tile reachable from
        /// this Warlock's current position — same CanBuildRoomOn rule every
        /// room placement (LairManager included) already funnels through.
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

        private void SetTask(WarlockTask newTask)
        {
            _task = newTask;
        }

        /// Re-plans this Warlock's route to whatever it was last walking
        /// toward, from wherever it is right now — called by
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

            SetTask(WarlockTask.Idle);
        }

        private static bool IsMovingTask(WarlockTask task)
        {
            return task is WarlockTask.MovingToLairSpot or WarlockTask.MovingToFood or WarlockTask.MovingToResearch
                or WarlockTask.MovingToTraining or WarlockTask.MovingToAttackTarget or WarlockTask.MovingToPortal;
        }

        // Same A*-planned-route movement ImplingAgent uses (see its own
        // PlanPathTo/MoveAlongPathThen for the full rationale) — duplicated
        // here rather than shared, matching how GremlinSpawner/WarlockSpawner
        // are already duplicated rather than sharing a base.
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

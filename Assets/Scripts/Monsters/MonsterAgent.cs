using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// What a monster is currently doing — decided every frame by priority
    /// (see MonsterAgent.EvaluateAndAct). One enum for every species: each
    /// only ever enters the subset its own behavior uses (an Elf never
    /// Researches, a Warlock never Roams), and the value names are what the
    /// UI shows and CreatureActivityMap buckets on, so they're kept exactly
    /// as the old per-species enums spelled them.
    ///
    /// The Moving.../working pairs (Training, Researching, Teaching) aren't
    /// single stationary states — each alternates between walking to the
    /// next dummy/bookcase/bench and pausing there a few seconds, the same
    /// pair just repeating with a new target each cycle.
    public enum MonsterTask
    {
        Idle,
        MovingToLairSpot,
        MovingToFood,
        MovingToTraining,
        Training,
        MovingToResearch,
        Researching,
        MovingToTeaching,
        Teaching,
        MovingToHaunt,
        HauntPausing,
        MovingToRoam,
        RoamPausing,
        MovingToAttackTarget,
        Attacking,
        MovingToPortal
    }

    /// Everything the five Portal-side creatures (Gremlin, Warlock, Maze
    /// Rattler, Bean Counter, Elf) share: the Creature/Hunger/Pay/Happiness/
    /// Combatant composition, the GridMover, payday, and the whole priority
    /// ladder except its bottom "productive" tier — Happiness Leaving (walk
    /// out through the Portal, or wreck the domain if there's no way out),
    /// an attack already under way, 100 claim/build a Lair, 80 eat, then
    /// Unhappy/Angry refusing work and periodically lashing out. Training
    /// (Training Room dummies) and roaming live here too since several
    /// species use them.
    ///
    /// A species subclass supplies its stat blocks + name pool, wires its
    /// own room managers in its Initialize, picks its productive task in
    /// BeginProductiveTask, and ticks any species-only task by overriding
    /// TickTask. Derive from MonsterAgent&lt;TSelf&gt;, not this directly —
    /// that layer gives each species its own All roster and Id counter.
    public abstract class MonsterAgent : MonoBehaviour, ICombatant
    {
        public int Id { get; private set; }
        public Vector3 Position => transform.position;

        /// A random name from the species' CreatureNames pool, picked once
        /// at spawn (see Awake) and kept for life — plus the numeric Id, in
        /// case two creatures roll the same name.
        public string Name => _name;
        private string _name;

        public MonsterTask Task => _task;

        /// Level/stats/skill slots, per design-doc.md's Creatures section.
        /// Read-only from the outside; ticked internally.
        public Creature Creature => _creature;

        /// Read-only from the outside — ticked internally. Imps don't have
        /// Hunger/Pay/Happiness (see each one's own header).
        public Hunger Hunger => _hunger;
        public Pay Pay => _pay;
        public Happiness Happiness => _happiness;

        /// Creature-vs-creature combat (see design-doc.md's Combat section) —
        /// ticked at the top of Update before EvaluateAndAct. ICombatant lets
        /// a Combatant reason about this agent uniformly.
        public Combatant Combat => _combat;
        public bool IsImp => false;
        public abstract string Species { get; }
        public string TaskLabel => _task.ToString();
        private readonly Combatant _combat = new Combatant();

        // Exp needed per level is Level * _expPerLevelStep (see
        // Creature.ExpToNextLevel) — the same for every species for now,
        // until per-creature leveling paces are designed.
        [SerializeField] private int _expPerLevelStep = 100;

        [SerializeField] private float _roamPauseDuration = 2f;

        // How long a training creature lingers at one dummy before
        // wandering to another — randomized per stop so the room doesn't
        // read as a metronome. Exp ticks on TrainingRoomManager's own fixed
        // cadence throughout, independent of this pause length.
        [SerializeField] private float _minTrainPauseSeconds = 3f;
        [SerializeField] private float _maxTrainPauseSeconds = 5f;

        // How often a refusing (Unhappy/Angry) creature re-rolls whether to
        // lash out, and the odds each time — "occasionally" vs "often" per
        // the design brief. All placeholder tuning.
        [SerializeField] private float _attackCheckIntervalSeconds = 8f;
        [SerializeField] private float _unhappyAttackChance = 0.25f;
        [SerializeField] private float _angryAttackChance = 0.6f;

        protected Creature _creature;
        protected readonly Hunger _hunger = new Hunger();
        private readonly Pay _pay = new Pay();
        protected readonly Happiness _happiness = new Happiness();

        protected DungeonGrid _grid;
        private LairManager _lairManager;
        private TavernManager _tavernManager;
        private TreasuryManager _treasuryManager;
        private Portal _portal;

        /// Optional — set by a species that trains (Gremlin, Warlock, Maze
        /// Rattler) in its Initialize before calling InitializeCore.
        protected TrainingRoomManager _trainingRoomManager;

        private MonsterTask _task = MonsterTask.Idle;
        private string _myLairRoomId;
        private Vector2Int _myLairCoord;
        private Vector2Int _lairTargetCoord;
        private Vector2Int _foodTargetCoord;
        private Vector2Int _trainingTargetCoord;
        private Vector2Int _attackTargetCoord;
        private bool _attackTargetIsRoom;
        private float _trainTimer;
        private float _trainPauseTimer;
        private float _trainPauseDuration;
        private float _roamPauseTimer;
        private float _attackCheckTimer;
        private float _attackHitTimer;

        // A*-planned route walking (PlanPathTo / MoveAlongPathThen /
        // Replan) — shared with every other creature agent, see GridMover.
        private readonly GridMover _mover = new GridMover();

        protected abstract CreatureStatBlock BaseStats { get; }
        protected abstract CreatureStatBlock GrowthPerLevel { get; }
        protected abstract string[] NamePool { get; }

        /// Adds this instance to its species roster and returns its Id —
        /// implemented by MonsterAgent&lt;TSelf&gt;.
        protected abstract int RegisterInstance();
        protected abstract void UnregisterInstance();

        /// The productive tier (40/30 in the ladder) — called when Idle and
        /// nothing higher-priority applies. Starts whatever this species
        /// does with its time (train, research, teach, haunt, roam), or
        /// leaves the task at Idle if there's nothing to do.
        protected abstract void BeginProductiveTask();

        /// Whether the current task is this species' preferred room job —
        /// feeds Happiness (doing its favorite work keeps it content).
        protected virtual bool IsDoingPreferredRoomJob => false;

        protected virtual void Awake()
        {
            Id = RegisterInstance();
            _name = $"{CreatureNames.GetRandom(NamePool)} #{Id}";

            _creature = new Creature(BaseStats, GrowthPerLevel, _expPerLevelStep);
        }

        /// The wiring every species shares — call from the species'
        /// Initialize after setting its own room managers.
        protected void InitializeCore(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            _grid = grid;
            _mover.Initialize(grid, transform, () => _creature.Stats.Movespeed);
            _lairManager = lairManager;
            _tavernManager = tavernManager;
            _treasuryManager = treasuryManager;
            _portal = portal;
            _creature.SetOwner(ownerId);
            CreatureHealthRing.Attach(gameObject, _creature, grid);
            _lairManager.RoomSold += OnLairSold;

            _combat.Initialize(this, this, grid, _creature, _hunger, _happiness,
                KeepersDomain.Core.KeeperContext.ForOwner(ownerId)?.ThroneCoord ?? grid.WorldToGrid(transform.position),
                () => _myLairRoomId != null ? _myLairCoord : (Vector2Int?)null,
                () => SetTask(MonsterTask.Idle),
                isImp: false);
        }

        protected virtual void Update()
        {
            _creature.Tick(Time.deltaTime);
            _hunger.Tick(Time.deltaTime);
            _happiness.Tick(Time.deltaTime, _hunger.IsHungry, IsDoingPreferredRoomJob && !_combat.InCombat);
            if (_pay.Tick(Time.deltaTime))
            {
                TryGetPaid();
            }

            if (_grid == null)
            {
                return;
            }

            // Combat overrides the normal priority list while engaged,
            // fleeing, or healing up afterward — see design-doc.md's Combat
            // section. onDisengage (wired in InitializeCore) drops the task
            // back to Idle so EvaluateAndAct re-plans from where combat left
            // off.
            if (_combat.Tick(Time.deltaTime))
            {
                return;
            }

            EvaluateAndAct();
        }

        /// Payday — draws this creature's wage (see Pay.WageFor) straight
        /// out of the Treasury, no walking/task involved (unlike eating,
        /// which needs a Tavern trip). Paid bumps Happiness; unpaid marks
        /// it unhappy (Pay.IsUnhappy) and dents Happiness instead.
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

        protected virtual void OnDestroy()
        {
            UnregisterInstance();
            _combat.Dispose();

            if (_lairManager != null)
            {
                _lairManager.RoomSold -= OnLairSold;

                // Whatever Lair tile this creature had claimed frees up when
                // it stops existing, whatever the reason (left through the
                // Portal, or any future death path) — otherwise the tile
                // would stay permanently claimed by nothing.
                if (_myLairRoomId != null)
                {
                    _lairManager.ReleaseLairTile(_myLairCoord);
                }
            }
        }

        /// A Lair sold out from under this creature (whether or not it was
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
        /// TickLeaving. Unhappy/Angry (10-40) refuse the productive tier and
        /// periodically attack instead — see TickHostile — but still eat/
        /// claim a Lair, since those aren't "tasks." GettingUnhappy (40-50)
        /// just refuses the productive tier.
        private void EvaluateAndAct()
        {
            // An attack already in progress always finishes, regardless of
            // which tier the creature is in *now* — mood can recover
            // mid-swing, and the productive tier has no case for these two
            // states, so without this check a recovered creature would
            // orphan mid-attack instead of resuming normal behavior.
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
            if (_task == MonsterTask.MovingToPortal)
            {
                SetTask(MonsterTask.Idle);
            }

            // Tier 100: no personal Lair claimed yet.
            if (_myLairRoomId == null && _task != MonsterTask.MovingToLairSpot)
            {
                if (TryBeginPursueLair())
                {
                    return;
                }
            }

            if (_task == MonsterTask.MovingToLairSpot)
            {
                MoveAlongPathThen(ArriveAtLairSpot);
                return;
            }

            // Tier 80: hungry.
            if (_hunger.IsHungry && _task != MonsterTask.MovingToFood)
            {
                if (TryBeginPursueFood())
                {
                    return;
                }
            }

            if (_task == MonsterTask.MovingToFood)
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
                    SetTask(MonsterTask.Idle);
                }
                return;
            }

            // Productive tier — the species decides what that means.
            if (_task == MonsterTask.Idle)
            {
                BeginProductiveTask();
            }

            TickTask(_task);
        }

        /// Ticks the current productive-tier task. Handles the shared
        /// Training and Roam pairs; a species with its own task (Research,
        /// Teaching, Haunt) overrides, handles those, and calls base.
        protected virtual void TickTask(MonsterTask task)
        {
            switch (task)
            {
                case MonsterTask.MovingToTraining:
                    MoveAlongPathThen(ArriveAtTraining);
                    break;
                case MonsterTask.Training:
                    TickTraining();
                    break;
                case MonsterTask.MovingToRoam:
                    MoveAlongPathThen(ArriveAtRoam);
                    break;
                case MonsterTask.RoamPausing:
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
            if (_task == MonsterTask.MovingToPortal)
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

            SetTask(MonsterTask.MovingToPortal);
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
            if (_task == MonsterTask.MovingToAttackTarget)
            {
                MoveAlongPathThen(ArriveAtAttackTarget);
                return true;
            }

            if (_task == MonsterTask.Attacking)
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
        /// resource/reinforced distinction, an angry creature isn't picky.
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
            SetTask(MonsterTask.MovingToAttackTarget);
            return true;
        }

        /// Any reachable tile belonging to any room (Lair, Treasury,
        /// whatever) — see TickAttackingRoom for what actually happens to
        /// it once this creature arrives and starts hitting it.
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
            SetTask(MonsterTask.MovingToAttackTarget);
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
            SetTask(MonsterTask.Attacking);
            _attackHitTimer = 0f;
        }

        /// Same Strength/Attackspeed-driven hit cadence as the Imp's own
        /// "Mine" basic attack (see ImplingAgent.MineHitInterval/
        /// MineHitDamage).
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
                SetTask(MonsterTask.Idle);
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
                SetTask(MonsterTask.Idle);
            }
        }

        private void TickAttackingWall()
        {
            if (_grid.GetTile(_attackTargetCoord).Type != TileType.Rock)
            {
                // Already gone (e.g. an Imp finished digging it out from
                // under this attack) — nothing left to hit.
                SetTask(MonsterTask.Idle);
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
                SetTask(MonsterTask.Idle);
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
                SetTask(MonsterTask.MovingToLairSpot);
                return true;
            }

            if (TryFindRandomLairSpot(out var newCoord) && PlanPathTo(newCoord, _grid.GridToWorld(newCoord)))
            {
                _lairTargetCoord = newCoord;
                SetTask(MonsterTask.MovingToLairSpot);
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

            SetTask(MonsterTask.Idle);
        }

        private bool TryBeginPursueFood()
        {
            if (_tavernManager == null || !_tavernManager.TryFindNearestTileWithBacon(_grid.WorldToGrid(transform.position), out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return false;
            }

            _foodTargetCoord = coord;
            SetTask(MonsterTask.MovingToFood);
            return true;
        }

        private void ArriveAtFood()
        {
            if (_tavernManager.TryEatBacon(_foodTargetCoord, TavernManager.MealBaconAmount))
            {
                _hunger.Eat();
            }

            SetTask(MonsterTask.Idle);
        }

        /// Heads for the nearest reachable Training Room dummy. False (task
        /// untouched) if there's no Training Room or none is reachable.
        protected bool TryBeginTrain()
        {
            if (_trainingRoomManager == null
                || !_trainingRoomManager.TryFindNearestDummyTile(_grid.WorldToGrid(transform.position), out var trainingCoord)
                || !PlanPathTo(trainingCoord, _grid.GridToWorld(trainingCoord)))
            {
                return false;
            }

            _trainingTargetCoord = trainingCoord;
            SetTask(MonsterTask.MovingToTraining);
            return true;
        }

        private void ArriveAtTraining()
        {
            SetTask(MonsterTask.Training);
            _trainTimer = 0f;
            _trainPauseTimer = 0f;
            _trainPauseDuration = UnityEngine.Random.Range(_minTrainPauseSeconds, _maxTrainPauseSeconds);
        }

        /// Exp ticks on its own fixed cadence the whole time this creature
        /// is Training, regardless of how long it lingers at any one dummy.
        /// Once the randomized pause for the current dummy is up, it
        /// wanders off toward a different one (see TryMoveToNextDummy).
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
        /// under this creature mid-training.
        private void TryMoveToNextDummy()
        {
            if (_trainingRoomManager != null
                && _trainingRoomManager.TryFindRandomDummyTile(_grid.WorldToGrid(transform.position), _trainingTargetCoord, out var coord)
                && PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                _trainingTargetCoord = coord;
                SetTask(MonsterTask.MovingToTraining);
                return;
            }

            SetTask(MonsterTask.Idle);
        }

        /// "Roam the dungeon to random spots" — picks any reachable walkable
        /// floor tile, not just room tiles, since this is meant to look like
        /// aimless wandering ("to find combat") rather than heading anywhere
        /// specific.
        protected void TryBeginRoam()
        {
            var fromCoord = _grid.WorldToGrid(transform.position);
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            if (!TryPickRandomCoord(distances.Keys, out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return;
            }

            SetTask(MonsterTask.MovingToRoam);
        }

        private void ArriveAtRoam()
        {
            SetTask(MonsterTask.RoamPausing);
            _roamPauseTimer = 0f;
        }

        private void TickRoamPause()
        {
            _roamPauseTimer += Time.deltaTime;
            if (_roamPauseTimer >= _roamPauseDuration)
            {
                SetTask(MonsterTask.Idle);
            }
        }

        /// Every Claimed, buildable, room-free Floor tile reachable from
        /// this creature's current position — same CanBuildRoomOn rule every
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

        protected static bool TryPickRandomCoord(ICollection<Vector2Int> candidates, out Vector2Int coord)
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

        protected void SetTask(MonsterTask newTask)
        {
            _task = newTask;
        }

        /// Re-plans this creature's route to whatever it was last walking
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
            // Combat doesn't resume after the hand sets the creature down —
            // it re-evaluates fresh (design-doc.md's Combat section).
            _combat.OnExternalReposition();

            if (!IsMovingTask(_task))
            {
                return;
            }

            if (_mover.Replan())
            {
                return;
            }

            SetTask(MonsterTask.Idle);
        }

        private static bool IsMovingTask(MonsterTask task)
        {
            return task is MonsterTask.MovingToLairSpot or MonsterTask.MovingToFood or MonsterTask.MovingToTraining
                or MonsterTask.MovingToResearch or MonsterTask.MovingToTeaching or MonsterTask.MovingToHaunt
                or MonsterTask.MovingToRoam or MonsterTask.MovingToAttackTarget or MonsterTask.MovingToPortal;
        }

        // Thin forwarders to the shared GridMover (see its header).
        protected bool PlanPathTo(Vector2Int goalCoord, Vector3 finalWorldPos)
        {
            return _mover.PlanPathTo(goalCoord, finalWorldPos);
        }

        protected void MoveAlongPathThen(Action onArrive)
        {
            _mover.MoveAlongPathThen(onArrive);
        }
    }

    /// Per-species registry layer — static fields on a generic class are
    /// per closed type, so each species gets its own All roster and Id
    /// counter (ids stay "Gremlin #0, #1, …" per species, as before).
    public abstract class MonsterAgent<TSelf> : MonsterAgent where TSelf : MonsterAgent<TSelf>
    {
        private static int _nextId;
        private static readonly List<TSelf> _all = new List<TSelf>();

        /// Every currently-alive creature of this species — for rosters,
        /// fog of war, and debug/inspection UI.
        public static IReadOnlyList<TSelf> All => _all;

        /// How many currently-alive creatures of this species belong to
        /// ownerId — spawner population caps are per-keeper, so a rival
        /// keeper's roster never gates your own recruiting.
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

        protected sealed override int RegisterInstance()
        {
            _all.Add((TSelf)this);
            return _nextId++;
        }

        protected sealed override void UnregisterInstance()
        {
            _all.Remove((TSelf)this);
        }
    }
}

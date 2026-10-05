using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;

namespace KeepersDomain.Monsters
{
    /// The second non-Imp creature, and the first "intelligent" one per the
    /// design doc (see Library's design-doc entry — Tavern's "food for
    /// intelligent creatures" line was written with this creature in mind).
    /// Recruited the same "join via the Portal's pool" way a Gremlin is (see
    /// WarlockSpawner). Shared behavior lives in MonsterAgent; a Warlock's
    /// productive tier is: research in a Library, or train in a Training
    /// Room if no Library exists (order flipped by SetTrainingPriority).
    /// Idle if neither applies — unlike Gremlin, a Warlock has no roam
    /// fallback.
    public class WarlockAgent : MonsterAgent<WarlockAgent>
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool (see Portal.SeedPool/TryTakeFromPool and
        /// WarlockSpawner.TryRecruitWarlock).
        public const string CreatureKind = "Warlock";

        public override string Species => CreatureKind;

        // 60 starting HP per the brief. Movespeed/Strength/Attackspeed have
        // no design-brief values yet — placeholders (slower/weaker than
        // Gremlin's, reads as a heavier caster-type) just so movement and
        // the Unhappy/Angry attack behavior work at all.
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

        // How long a researching Warlock lingers at one bookcase before
        // wandering to another — randomized per stop within this range so
        // the room doesn't read as a metronome. Exp still ticks on its own
        // fixed LibraryManager.ResearchTickSeconds cadence throughout,
        // independent of this pause length.
        [SerializeField] private float _minBookcasePauseSeconds = 3f;
        [SerializeField] private float _maxBookcasePauseSeconds = 5f;

        private LibraryManager _libraryManager;
        private Vector2Int _researchTargetCoord;
        private float _researchTimer;
        private float _researchPauseTimer;
        private float _researchPauseDuration;

        // Set by MinionGrabController when the player's Grab hand drops
        // this Warlock onto a Training Room tile — see SetTrainingPriority.
        private bool _hasTrainingPriority;

        protected override CreatureStatBlock BaseStats => _baseStats;
        protected override CreatureStatBlock GrowthPerLevel => _growthPerLevel;
        protected override string[] NamePool => CreatureNames.WarlockNames;
        protected override bool IsDoingPreferredRoomJob => Task == MonsterTask.Researching;

        public void Initialize(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, LibraryManager libraryManager, TrainingRoomManager trainingRoomManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            _libraryManager = libraryManager;
            _trainingRoomManager = trainingRoomManager;
            InitializeCore(grid, lairManager, tavernManager, treasuryManager, portal, ownerId);
        }

        /// Flips this Warlock's productive-tier order to Training first,
        /// Research as the fallback (see BeginProductiveTask).
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

            if (hasPriority && Task is MonsterTask.MovingToResearch or MonsterTask.Researching)
            {
                SetTask(MonsterTask.Idle);
            }
        }

        /// Research (Library) first, Training (Training Room) as the
        /// fallback if no Library exists — unless _hasTrainingPriority
        /// flips that order (see SetTrainingPriority), in which case
        /// Training is tried first instead. Idle if neither is available,
        /// same either way.
        protected override void BeginProductiveTask()
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

        protected override void TickTask(MonsterTask task)
        {
            switch (task)
            {
                case MonsterTask.MovingToResearch:
                    MoveAlongPathThen(ArriveAtResearch);
                    break;
                case MonsterTask.Researching:
                    TickResearching();
                    break;
                default:
                    base.TickTask(task);
                    break;
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
            SetTask(MonsterTask.MovingToResearch);
            return true;
        }

        private void ArriveAtResearch()
        {
            SetTask(MonsterTask.Researching);
            _researchTimer = 0f;
            _researchPauseTimer = 0f;
            _researchPauseDuration = Random.Range(_minBookcasePauseSeconds, _maxBookcasePauseSeconds);
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
                SetTask(MonsterTask.MovingToResearch);
                return;
            }

            SetTask(MonsterTask.Idle);
        }
    }
}

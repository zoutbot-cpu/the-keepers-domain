using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;

namespace KeepersDomain.Monsters
{
    /// A preacher, not a brawler. Shared behavior lives in MonsterAgent;
    /// differs in its join requirement (a placed Conversion Class — see
    /// BeanCounterSpawner) and its productive tier: with a Conversion Class
    /// placed, it walks to a bench-adjacent tile and lectures there,
    /// periodically pulling a random prisoner out of whichever Jail is
    /// holding one and tormenting it (see
    /// ConversionClassManager.TryTormentRandomPrisoner); otherwise it roams.
    /// Visual is a placeholder sickly yellow-green capsule until a real
    /// model exists — see BeanCounterSpawner.
    public class BeanCounterAgent : MonsterAgent<BeanCounterAgent>
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool (see Portal.SeedPool/TryTakeFromPool and
        /// BeanCounterSpawner.TryRecruitBeanCounter).
        public const string CreatureKind = "BeanCounter";

        public override string Species => CreatureKind;

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

        // Basic per-level growth — same "+10% Strength, +7.5% Attackspeed,
        // +5% Movespeed per level, +1 Armor by level 10" ratios as
        // Gremlin's own growth block, scaled off this creature's own
        // (lower) base stats — no design-brief curve exists yet.
        [SerializeField]
        private CreatureStatBlock _growthPerLevel = new CreatureStatBlock
        {
            MaxHP = 5f,
            HPRegen = 0.15f,
            Strength = 0.6f,
            Movespeed = 0.11f,
            Attackspeed = 0.04f,
            Armor = 1f / 9f
        };

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

        private ConversionClassManager _conversionClassManager;
        private JailManager _jailManager;
        private Vector2Int _teachTargetCoord;
        private float _teachTimer;
        private float _teachPauseTimer;
        private float _teachPauseDuration;
        private bool _hasTormentedThisSession;

        protected override CreatureStatBlock BaseStats => _baseStats;
        protected override CreatureStatBlock GrowthPerLevel => _growthPerLevel;
        protected override string[] NamePool => CreatureNames.BeanCounterNames;
        protected override bool IsDoingPreferredRoomJob => Task == MonsterTask.Teaching;

        public void Initialize(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, ConversionClassManager conversionClassManager, JailManager jailManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            _conversionClassManager = conversionClassManager;
            _jailManager = jailManager;
            InitializeCore(grid, lairManager, tavernManager, treasuryManager, portal, ownerId);
        }

        /// Teach at a Conversion Class bench if one is reachable, otherwise
        /// roam.
        protected override void BeginProductiveTask()
        {
            if (_conversionClassManager != null
                && _conversionClassManager.TryFindNearestBenchTile(_grid.WorldToGrid(transform.position), out var benchCoord)
                && PlanPathTo(benchCoord, _grid.GridToWorld(benchCoord)))
            {
                _teachTargetCoord = benchCoord;
                SetTask(MonsterTask.MovingToTeaching);
                return;
            }

            TryBeginRoam();
        }

        protected override void TickTask(MonsterTask task)
        {
            switch (task)
            {
                case MonsterTask.MovingToTeaching:
                    MoveAlongPathThen(ArriveAtTeaching);
                    break;
                case MonsterTask.Teaching:
                    TickTeaching();
                    break;
                default:
                    base.TickTask(task);
                    break;
            }
        }

        private void ArriveAtTeaching()
        {
            SetTask(MonsterTask.Teaching);
            _teachTimer = 0f;
            _teachPauseTimer = 0f;
            _teachPauseDuration = Random.Range(_minTeachPauseSeconds, _maxTeachPauseSeconds);
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
                SetTask(MonsterTask.MovingToTeaching);
                return;
            }

            SetTask(MonsterTask.Idle);
        }
    }
}

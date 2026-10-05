using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;

namespace KeepersDomain.Monsters
{
    /// A fast, fragile skirmisher. Shared behavior lives in MonsterAgent;
    /// differs from Gremlin only in its stats, its join requirement (a
    /// placed Jail rather than Hatchery/Training Room population caps — see
    /// MazeRattlerSpawner) and its productive tier: Training Room first,
    /// then a placed Jail's pit ("haunt the prisoners"), then plain roam.
    /// Visual is a placeholder brown capsule until a real model exists —
    /// see MazeRattlerSpawner.
    public class MazeRattlerAgent : MonsterAgent<MazeRattlerAgent>
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool (see Portal.SeedPool/TryTakeFromPool and
        /// MazeRattlerSpawner.TryRecruitMazeRattler).
        public const string CreatureKind = "MazeRattler";

        public override string Species => CreatureKind;

        // A fast, fragile skirmisher — the ratman starts squishier than a
        // Gremlin (60 vs 80 HP) but noticeably quicker on its feet and much
        // faster to attack, so it trades staying power for tempo. Still all
        // placeholder numbers, but its own now rather than a Gremlin copy.
        [SerializeField]
        private CreatureStatBlock _baseStats = new CreatureStatBlock
        {
            MaxHP = 60f,
            HPRegen = 1f,
            Movespeed = 4.2f,
            Strength = 14f,
            Attackspeed = 1.1f
        };

        // Scales toward that skirmisher fantasy: less HP per level than the
        // others, but the fastest Movespeed/Attackspeed growth of any
        // creature. Shared +1-Armor-by-10 curve.
        [SerializeField]
        private CreatureStatBlock _growthPerLevel = new CreatureStatBlock
        {
            MaxHP = 5f,
            HPRegen = 0.15f,
            Strength = 1.4f,
            Movespeed = 0.22f,
            Attackspeed = 0.08f,
            Armor = 1f / 9f
        };

        // How long a haunting Maze Rattler lingers at one pit tile before
        // wandering to another — same idea as the roam pause, just its own
        // tunable, since haunting is thematically its own behavior even
        // though the code shape is identical.
        [SerializeField] private float _hauntPauseDuration = 2f;

        private JailManager _jailManager;
        private float _hauntPauseTimer;

        protected override CreatureStatBlock BaseStats => _baseStats;
        protected override CreatureStatBlock GrowthPerLevel => _growthPerLevel;
        protected override string[] NamePool => CreatureNames.MazeRattlerNames;
        protected override bool IsDoingPreferredRoomJob => Task == MonsterTask.Training;

        public void Initialize(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, TrainingRoomManager trainingRoomManager, JailManager jailManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            _trainingRoomManager = trainingRoomManager;
            _jailManager = jailManager;
            InitializeCore(grid, lairManager, tavernManager, treasuryManager, portal, ownerId);
        }

        /// Training Room first (if any dummy is reachable), then a placed
        /// Jail's pit ("haunt the prisoners"), then plain roam as the last
        /// fallback — the fallback chain "below training" the brief asked
        /// for.
        protected override void BeginProductiveTask()
        {
            if (TryBeginTrain() || TryBeginHaunt())
            {
                return;
            }

            TryBeginRoam();
        }

        protected override void TickTask(MonsterTask task)
        {
            switch (task)
            {
                case MonsterTask.MovingToHaunt:
                    MoveAlongPathThen(ArriveAtHaunt);
                    break;
                case MonsterTask.HauntPausing:
                    TickHauntPause();
                    break;
                default:
                    base.TickTask(task);
                    break;
            }
        }

        /// "Haunt the prisoners in the jail" — walks to a random reachable
        /// pit tile of any placed Jail (JailManager.TryFindRandomPitTile)
        /// and pauses there a while (see ArriveAtHaunt/TickHauntPause),
        /// same "walk somewhere, pause, re-evaluate" shape roaming uses —
        /// re-evaluating back through Idle each time naturally drifts this
        /// Maze Rattler between different pit tiles over time, no dedicated
        /// "move to a different one" step needed. Grants no exp — purely
        /// flavor movement.
        private bool TryBeginHaunt()
        {
            if (_jailManager == null || !_jailManager.TryFindRandomPitTile(_grid.WorldToGrid(transform.position), out var coord) || !PlanPathTo(coord, _grid.GridToWorld(coord)))
            {
                return false;
            }

            SetTask(MonsterTask.MovingToHaunt);
            return true;
        }

        private void ArriveAtHaunt()
        {
            SetTask(MonsterTask.HauntPausing);
            _hauntPauseTimer = 0f;
        }

        private void TickHauntPause()
        {
            _hauntPauseTimer += Time.deltaTime;
            if (_hauntPauseTimer >= _hauntPauseDuration)
            {
                SetTask(MonsterTask.Idle);
            }
        }
    }
}

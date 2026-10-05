using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;

namespace KeepersDomain.Monsters
{
    /// The first non-Imp creature — "a small, thin, green-blue-ish humanoid"
    /// per the brief. Visual is a placeholder capsule ("a green pill") until
    /// a real model exists. Shared behavior (Lair, eating, pay, mood,
    /// attacks, leaving) lives in MonsterAgent; a Gremlin's productive tier
    /// is: train if a Training Room exists, otherwise roam.
    public class GremlinAgent : MonsterAgent<GremlinAgent>
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool (see Portal.SeedPool/TryTakeFromPool and
        /// GremlinSpawner.TryRecruitGremlin).
        public const string CreatureKind = "Gremlin";

        public override string Species => CreatureKind;

        // 80 starting HP per the brief. Movespeed/Strength/Attackspeed have
        // no design-brief values yet — placeholders just so movement and
        // the Unhappy/Angry attack behavior work at all. Every other stat
        // sits at 0.
        [SerializeField]
        private CreatureStatBlock _baseStats = new CreatureStatBlock
        {
            MaxHP = 80f,
            HPRegen = 1f,
            Movespeed = 3.5f,
            Strength = 15f,
            Attackspeed = 0.8f
        };

        // Basic per-level growth — no design-brief curve exists yet, so
        // this just applies the same "roughly +10% Strength, +7.5%
        // Attackspeed, +5% Movespeed per level, +1 Armor by level 10"
        // ratios the Imp's own growth block already uses, scaled off this
        // creature's own base stats instead of copying the Imp's numbers.
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

        protected override CreatureStatBlock BaseStats => _baseStats;
        protected override CreatureStatBlock GrowthPerLevel => _growthPerLevel;
        protected override string[] NamePool => CreatureNames.GremlinNames;
        protected override bool IsDoingPreferredRoomJob => Task == MonsterTask.Training;

        public void Initialize(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, TrainingRoomManager trainingRoomManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            _trainingRoomManager = trainingRoomManager;
            InitializeCore(grid, lairManager, tavernManager, treasuryManager, portal, ownerId);
        }

        /// Train if a Training Room exists, otherwise roam.
        protected override void BeginProductiveTask()
        {
            if (!TryBeginTrain())
            {
                TryBeginRoam();
            }
        }
    }
}

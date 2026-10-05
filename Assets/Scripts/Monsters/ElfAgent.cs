using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;

namespace KeepersDomain.Monsters
{
    /// The "weak and worthless (meat shield)" failure outcome of Conversion
    /// Class's torment (see ConversionClassManager.TryTormentRandomPrisoner)
    /// — never recruited via the Portal's pool the way every other creature
    /// is (see ElfSpawner.SpawnElf, called directly rather than through a
    /// MeetsJoinRequirements/pool gate). Shared behavior lives in
    /// MonsterAgent, with deliberately weaker stats and no preferred-room
    /// job — an Elf just claims a Lair, eats, and otherwise roams; it has
    /// nothing it's good at. Visual is a placeholder pale-green capsule,
    /// smaller than every other creature's, until a real model exists.
    public class ElfAgent : MonsterAgent<ElfAgent>
    {
        /// Key used to look this creature type up in a Portal's recruitable
        /// pool — Elf is never actually seeded into one (see ElfSpawner),
        /// but the const still exists for symmetry with every other
        /// creature type and so ConversionClassManager can match on it by
        /// name the same way it matches Gremlin/Warlock/MazeRattler.
        public const string CreatureKind = "Elf";

        public override string Species => CreatureKind;

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

        // Same growth ratios as every other creature's block (+10%
        // Strength, +7.5% Attackspeed, +5% Movespeed per level, +1 Armor
        // by level 10) — an Elf still stays the weakest option at any given
        // level since its base stats are so far below everyone else's.
        [SerializeField]
        private CreatureStatBlock _growthPerLevel = new CreatureStatBlock
        {
            MaxHP = 3f,
            HPRegen = 0.1f,
            Strength = 0.4f,
            Movespeed = 0.15f,
            Attackspeed = 0.04f,
            Armor = 1f / 9f
        };

        protected override CreatureStatBlock BaseStats => _baseStats;
        protected override CreatureStatBlock GrowthPerLevel => _growthPerLevel;
        protected override string[] NamePool => CreatureNames.ElfNames;

        public void Initialize(DungeonGrid grid, LairManager lairManager, TavernManager tavernManager, TreasuryManager treasuryManager, Portal portal, int ownerId)
        {
            InitializeCore(grid, lairManager, tavernManager, treasuryManager, portal, ownerId);
        }

        /// No preferred-room job — an Elf just roams.
        protected override void BeginProductiveTask()
        {
            TryBeginRoam();
        }
    }
}

using UnityEngine;

namespace KeepersDomain.Creatures
{
    /// Shared base every creature (Imp, and whatever follows) is built on:
    /// a level from 1-10, the 11 stats from design-doc.md's Creatures
    /// section, and 6 skill slots. Composed into a creature's MonoBehaviour
    /// (see ImplingAgent) rather than inherited, matching how
    /// ImplingInventory is already composed in rather than subclassed.
    public class Creature
    {
        public const int MaxLevel = 10;

        // Exp needed to go from Level to Level+1 is Level * ExpPerLevelStep.
        // Placeholder curve — nothing grants exp yet (no tasks/training/
        // combat reward it), see design-doc.md's "Exp-per-level curve: TBD".
        private const int ExpPerLevelStep = 100;

        private readonly CreatureStatBlock _base;
        private readonly CreatureStatBlock _growth;

        public int Level { get; private set; } = 1;
        public int Exp { get; private set; }
        public int ExpToNextLevel => Level >= MaxLevel ? 0 : Level * ExpPerLevelStep;

        public CreatureStats Stats { get; } = new CreatureStats();
        public CreatureSkillSlots Skills { get; } = new CreatureSkillSlots();

        public Creature(CreatureStatBlock baseStats, CreatureStatBlock growthPerLevel)
        {
            _base = baseStats;
            _growth = growthPerLevel;
            RecalculateStats(initial: true);
        }

        /// Regen tick — call once per frame (or whatever cadence the owning
        /// creature updates on) to grow HP/Mana back toward max.
        public void Tick(float deltaTime)
        {
            Stats.HP = Mathf.Min(Stats.MaxHP, Stats.HP + Stats.HPRegen * deltaTime);
            Stats.Mana = Mathf.Min(Stats.MaxMana, Stats.Mana + Stats.ManaRegen * deltaTime);
        }

        /// Levels up as many times as the added exp covers, capped at
        /// MaxLevel — exp beyond that is simply discarded, there's no
        /// prestige/overflow system.
        public void AddExp(int amount)
        {
            if (amount <= 0 || Level >= MaxLevel)
            {
                return;
            }

            Exp += amount;
            while (Level < MaxLevel && Exp >= ExpToNextLevel)
            {
                Exp -= ExpToNextLevel;
                Level++;
                RecalculateStats(initial: false);
            }
        }

        /// initial: true sets HP/Mana to full (a fresh creature at spawn);
        /// false (a level-up) raises the max but leaves current HP/Mana
        /// where they were, only clamping down if the new max dropped
        /// below the current value.
        private void RecalculateStats(bool initial)
        {
            var levelsGained = Level - 1;

            Stats.MaxHP = _base.MaxHP + _growth.MaxHP * levelsGained;
            Stats.HPRegen = _base.HPRegen + _growth.HPRegen * levelsGained;
            Stats.MaxMana = _base.MaxMana + _growth.MaxMana * levelsGained;
            Stats.ManaRegen = _base.ManaRegen + _growth.ManaRegen * levelsGained;
            Stats.Strength = _base.Strength + _growth.Strength * levelsGained;
            Stats.Movespeed = _base.Movespeed + _growth.Movespeed * levelsGained;
            Stats.Attackspeed = _base.Attackspeed + _growth.Attackspeed * levelsGained;
            Stats.Intelligence = _base.Intelligence + _growth.Intelligence * levelsGained;
            Stats.Craftmanship = _base.Craftmanship + _growth.Craftmanship * levelsGained;
            Stats.Armor = _base.Armor + _growth.Armor * levelsGained;
            Stats.Lifesteal = _base.Lifesteal + _growth.Lifesteal * levelsGained;

            Stats.HP = initial ? Stats.MaxHP : Mathf.Min(Stats.HP, Stats.MaxHP);
            Stats.Mana = initial ? Stats.MaxMana : Mathf.Min(Stats.Mana, Stats.MaxMana);
        }
    }
}

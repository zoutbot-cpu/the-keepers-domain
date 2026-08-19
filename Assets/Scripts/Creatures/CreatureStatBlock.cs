using System;

namespace KeepersDomain.Creatures
{
    /// A creature's stat values at a single level "checkpoint" — used both
    /// as the level-1 base block and as the flat per-level growth block
    /// added on top of it (see Creature.RecalculateStats). HP and Mana are
    /// represented by their max/regen only here; the *current* HP/Mana a
    /// creature is sitting at lives on CreatureStats instead, since neither
    /// a base block nor a growth block has a meaningful "current" value.
    [Serializable]
    public struct CreatureStatBlock
    {
        public float MaxHP;
        public float HPRegen;
        public float MaxMana;
        public float ManaRegen;
        public float Strength;
        public float Movespeed;
        public float Attackspeed;
        public float Intelligence;
        public float Craftmanship;
        public float Armor;
        public float Lifesteal;
    }
}

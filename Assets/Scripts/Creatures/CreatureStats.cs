namespace KeepersDomain.Creatures
{
    /// A creature's live stat values, recalculated whenever its level
    /// changes (see Creature.RecalculateStats). HP/Mana carry both a
    /// current and max value; every other stat is just a flat number.
    public class CreatureStats
    {
        public float HP;
        public float MaxHP;
        public float HPRegen;
        public float Mana;
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

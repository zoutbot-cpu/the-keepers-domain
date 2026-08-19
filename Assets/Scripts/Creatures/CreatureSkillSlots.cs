namespace KeepersDomain.Creatures
{
    /// The 6 skill slots every creature has, per design-doc.md's Creatures
    /// section. Slot 0 is always the creature's basic attack (e.g. the
    /// Imp's "Mine"); slots 1-5 are unused placeholders until individual
    /// creature kits are designed — this class only tracks names, it
    /// doesn't execute anything.
    public class CreatureSkillSlots
    {
        public const int SlotCount = 6;
        public const int BasicAttackSlot = 0;

        private readonly string[] _slots = new string[SlotCount];

        public string Get(int slot) => _slots[slot];

        public void Set(int slot, string skillName)
        {
            _slots[slot] = skillName;
        }

        public bool IsFilled(int slot) => !string.IsNullOrEmpty(_slots[slot]);
    }
}

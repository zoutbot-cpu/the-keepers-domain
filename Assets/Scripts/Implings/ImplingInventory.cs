using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Implings
{
    /// What a single impling is currently carrying. Both resource types
    /// weigh 1 per unit ("1 gold weighs 1", "1 mana crystal weighs 1"), so
    /// CarriedWeight is just the unit count — kept as its own property
    /// anyway so a future resource with a different weight doesn't need
    /// every caller rewritten.
    public class ImplingInventory
    {
        public const int MaxWeight = 60;

        private const int WeightPerGold = 1;
        private const int WeightPerManaCrystal = 1;
        private const int WeightPerSlime = 1;

        public int Gold { get; private set; }
        public int ManaCrystals { get; private set; }
        public int Slimes { get; private set; }

        public int CarriedWeight => Gold * WeightPerGold + ManaCrystals * WeightPerManaCrystal + Slimes * WeightPerSlime;
        public bool IsFull => CarriedWeight >= MaxWeight;
        public bool HasCargo => CarriedWeight > 0;

        /// Adds up to amount of type, capped by remaining capacity — excess
        /// is simply not picked up (lost), there's no "drop on the ground"
        /// mechanic. Returns how much was actually added.
        public int Add(ResourceType type, int amount)
        {
            if (amount <= 0 || type == ResourceType.None)
            {
                return 0;
            }

            var weightPerUnit = type == ResourceType.Gold ? WeightPerGold : WeightPerManaCrystal;
            var remainingCapacity = MaxWeight - CarriedWeight;
            var added = Mathf.Min(amount, remainingCapacity / weightPerUnit);
            if (added <= 0)
            {
                return 0;
            }

            if (type == ResourceType.Gold)
            {
                Gold += added;
            }
            else
            {
                ManaCrystals += added;
            }

            return added;
        }

        /// Same capped-add shape as Add(ResourceType, int), kept separate
        /// since a hauled slime isn't a mined wall resource (see
        /// ResourceType) — SlimeHatcheryManager hands these out directly
        /// rather than through DungeonGrid.ApplyDigDamage.
        public int AddSlimes(int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }

            var added = Mathf.Min(amount, (MaxWeight - CarriedWeight) / WeightPerSlime);
            if (added <= 0)
            {
                return 0;
            }

            Slimes += added;
            return added;
        }

        public void RemoveGold(int amount)
        {
            Gold = Mathf.Max(0, Gold - amount);
        }

        public void RemoveManaCrystals(int amount)
        {
            ManaCrystals = Mathf.Max(0, ManaCrystals - amount);
        }

        public void RemoveSlimes(int amount)
        {
            Slimes = Mathf.Max(0, Slimes - amount);
        }
    }
}

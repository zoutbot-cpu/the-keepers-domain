using System.Collections.Generic;
using UnityEngine;

namespace KeepersDomain.Creatures
{
    /// A non-creature thing a hostile Combatant will walk up to and attack —
    /// currently just the Throne Room. Kept separate from ICombatant (which
    /// carries a Creature, an AI, movement) since a structure just sits
    /// there and soaks hits. See design-doc.md's Combat section.
    public interface IAttackTarget
    {
        int OwnerId { get; }

        /// Grid tile used for range / line-of-sight / the approach path.
        Vector2Int Coord { get; }

        Vector3 Position { get; }

        /// While false, Combatants ignore it entirely.
        bool IsAlive { get; }

        string DisplayName { get; }

        void ReceiveAttack(int rawDamage, ICombatant attacker);
    }

    /// Every live IAttackTarget — scanned by Combatant when a creature has
    /// no creature to fight. Implementers add/remove themselves.
    public static class AttackTargets
    {
        public static readonly List<IAttackTarget> All = new List<IAttackTarget>();
    }
}

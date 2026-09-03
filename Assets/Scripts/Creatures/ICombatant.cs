using UnityEngine;

namespace KeepersDomain.Creatures
{
    /// Implemented by every creature agent (ImplingAgent + the five
    /// Monsters/*Agent types) so a Combatant can reason about any creature
    /// uniformly — resolve its owner, reach its Creature/Combat, hit it, and
    /// skip it once it's down — without the "switch on every concrete type"
    /// shape MinionGrabController still uses. transform/gameObject/name are
    /// already provided by MonoBehaviour; the rest each agent wires to its
    /// own composed pieces in one line.
    public interface ICombatant
    {
        Creature Creature { get; }
        Combatant Combat { get; }
        Transform transform { get; }
        GameObject gameObject { get; }
        string Name { get; }

        /// The creature-kind key ("Gremlin", "Imp", ...) — each agent
        /// returns its own CreatureKind const (or "Imp"). Used when a
        /// downed body is carried into a Jail (JailManager.TryCapture).
        string Species { get; }

        /// True only for ImplingAgent — Imps flee every hostile except each
        /// other, and only ever pick a fight with an enemy Imp (see
        /// design-doc.md's Combat section).
        bool IsImp { get; }
    }
}

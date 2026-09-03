using System.Collections.Generic;

namespace KeepersDomain.Core
{
    /// A Keeper's posture toward another Keeper — see design-doc.md's Combat
    /// section. Directional (P1's stance toward P2 is independent of P2's
    /// toward P1) and per-ordered-pair.
    public enum Stance
    {
        /// Attack every hostile creature on sight within aggro range.
        Aggressive,

        /// Don't start fights — but a struck creature retaliates against its
        /// attacker, and nearby same-owner creatures assist it.
        Neutral,

        /// A struck creature retaliates against its attacker, but nearby
        /// allies don't assist and nothing ever auto-aggros.
        Friendly
    }

    /// Every Keeper's stance toward every other Keeper. One instance per
    /// running game, created and owned by GameBootstrap and exposed here as
    /// a static Current so every Combatant resolves hostility through the
    /// same table without threading a reference through six spawners and six
    /// agent Initialize signatures (same static-registry convention
    /// KeeperContext.All uses). Host-authoritative once networking exists.
    ///
    /// Defaults: a Keeper is Friendly toward itself; the wild/neutral
    /// pseudo-owner (WildOwnerId) is Neutral toward everyone and everyone is
    /// Neutral toward it; every other ordered pair defaults to Aggressive
    /// until Set overrides it.
    public sealed class StanceRegistry
    {
        /// OwnerId for creatures that belong to no Keeper (future map fauna,
        /// converted-but-not-yet-joined prisoners, ...). Matches
        /// TileState.OwnerId's own "-1 means no owner" convention.
        public const int WildOwnerId = -1;

        /// Set by GameBootstrap.BuildWorld, cleared (null) on
        /// ReturnToMainMenu, same lifecycle as KeeperContext.All. A
        /// Combatant with no registry (e.g. a stale agent ticking once
        /// during teardown) treats everyone as non-hostile.
        public static StanceRegistry Current { get; set; }

        private readonly Dictionary<(int From, int Toward), Stance> _overrides =
            new Dictionary<(int, int), Stance>();

        public Stance Get(int fromOwner, int towardOwner)
        {
            if (fromOwner == towardOwner)
            {
                return Stance.Friendly;
            }

            if (fromOwner == WildOwnerId || towardOwner == WildOwnerId)
            {
                return Stance.Neutral;
            }

            return _overrides.TryGetValue((fromOwner, towardOwner), out var stance)
                ? stance
                : Stance.Aggressive;
        }

        public void Set(int fromOwner, int towardOwner, Stance stance)
        {
            if (fromOwner == towardOwner || fromOwner == WildOwnerId || towardOwner == WildOwnerId)
            {
                return;
            }

            _overrides[(fromOwner, towardOwner)] = stance;
        }

        /// Whether a creature owned by viewerOwner treats a creature owned by
        /// otherOwner as an attack-on-sight target — Aggressive only.
        /// Neutral/Friendly creatures only ever fight back once actually hit
        /// (see Combatant.ReceiveHit).
        public bool IsHostileOnSight(int viewerOwner, int otherOwner)
        {
            return Get(viewerOwner, otherOwner) == Stance.Aggressive;
        }

        /// Whether same-owner creatures near a struck victim rush to help —
        /// true for Neutral (the "nearby allies assist" rule), false for
        /// Friendly. Aggressive is moot (allies are already engaging on
        /// sight) but returns true anyway so an alarm still propagates.
        public bool AlliesAssist(int victimOwner, int attackerOwner)
        {
            return Get(victimOwner, attackerOwner) != Stance.Friendly;
        }
    }
}

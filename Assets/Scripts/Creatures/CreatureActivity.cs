namespace KeepersDomain.Creatures
{
    /// A coarse "what is this creature doing" bucket — the per-species task
    /// enums (GremlinTask, ImplingState, ...) all collapse into this so a
    /// networked client's roster can show *something* without replicating
    /// six different enums. See CreatureActivityMap / CreatureNetView.
    public enum CreatureActivity : byte
    {
        Idle,
        Moving,
        Working,
        Hauling,
        Fighting,
        Fleeing,
        Leaving,
        Down
    }

    public static class CreatureActivityMap
    {
        /// Host-side: bucket an agent's current state. Combat overrides the
        /// task machine (the agent's task freezes while Combatant drives it),
        /// so check combat first, then the frozen/idle task label.
        public static CreatureActivity From(ICombatant self, bool downed)
        {
            if (downed)
            {
                return CreatureActivity.Down;
            }

            var combat = self.Combat;
            if (combat != null)
            {
                if (combat.IsFleeing)
                {
                    return CreatureActivity.Fleeing;
                }

                if (combat.InCombat)
                {
                    return CreatureActivity.Fighting;
                }
            }

            var task = self.TaskLabel ?? string.Empty;

            if (task.Contains("Portal") || task == "Leaving")
            {
                return CreatureActivity.Leaving;
            }

            if (task == "CarryingBody")
            {
                return CreatureActivity.Hauling;
            }

            if (task.StartsWith("MovingTo") || task == "ReturningToLair")
            {
                return CreatureActivity.Moving;
            }

            if (task == "Idle" || task == "IdleInLair" || task == "SeekingJob" || task.EndsWith("Pausing"))
            {
                return CreatureActivity.Idle;
            }

            return CreatureActivity.Working;
        }
    }
}

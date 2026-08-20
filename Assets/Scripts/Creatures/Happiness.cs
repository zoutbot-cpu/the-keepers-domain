namespace KeepersDomain.Creatures
{
    /// Non-Imp minions only — Imps don't have moods. Driven entirely by
    /// Hunger and Pay (the two "problem" states that already exist) rather
    /// than its own independent source: decays while hungry, recovers
    /// (capped at StartingValue — nothing currently pushes it higher, so
    /// Enjoying/Ecstatic aren't reachable yet) while not, and takes a
    /// one-time hit every payday it goes unpaid. See
    /// GremlinAgent/WarlockAgent's own Tick/TryGetPaid for where those get
    /// wired in.
    public enum HappinessTier
    {
        Ecstatic,
        EnjoyingThemselves,
        Happy,
        GettingUnhappy,
        Unhappy,
        Angry,
        Leaving
    }

    public class Happiness
    {
        public const float Max = 100f;
        public const float StartingValue = 60f;

        // All placeholder tuning, not balanced — see design-doc.md's
        // Happiness section.
        private const float HungryDecayPerSecond = 30f / 600f; // -30 over 10 min while hungry
        private const float RecoveryPerSecond = 20f / 600f;    // +20 over 10 min while not hungry, capped at StartingValue
        private const float UnpaidPenalty = 15f;               // one-time hit per missed payday

        public float Value { get; private set; } = StartingValue;
        public HappinessTier Tier => GetTier(Value);

        /// isHungry drives the direction (decay vs. recovery); Pay's own
        /// missed-payday hit is applied separately via ApplyUnpaidPenalty,
        /// since payday is a discrete event, not a per-frame state.
        public void Tick(float deltaTime, bool isHungry)
        {
            Value = isHungry
                ? UnityEngine.Mathf.Max(0f, Value - HungryDecayPerSecond * deltaTime)
                : UnityEngine.Mathf.Min(StartingValue, Value + RecoveryPerSecond * deltaTime);
        }

        public void ApplyUnpaidPenalty()
        {
            Value = UnityEngine.Mathf.Max(0f, Value - UnpaidPenalty);
        }

        public static HappinessTier GetTier(float value)
        {
            if (value >= 90f) return HappinessTier.Ecstatic;
            if (value >= 75f) return HappinessTier.EnjoyingThemselves;
            if (value >= 50f) return HappinessTier.Happy;
            if (value >= 40f) return HappinessTier.GettingUnhappy;
            if (value >= 25f) return HappinessTier.Unhappy;
            if (value >= 10f) return HappinessTier.Angry;
            return HappinessTier.Leaving;
        }

        /// GettingUnhappy and below all refuse productive tasks (training/
        /// research/roaming) — see each agent's EvaluateAndAct. Self-care
        /// (eating, claiming a Lair) isn't a "task" and stays active
        /// through every tier except Leaving, which overrides everything.
        public static bool RefusesTasks(HappinessTier tier)
        {
            return tier is HappinessTier.GettingUnhappy or HappinessTier.Unhappy or HappinessTier.Angry;
        }

        /// Unhappy and Angry additionally lash out — "occasionally" vs
        /// "often" per the design brief, see each agent's attack-chance
        /// tunables for the actual rates.
        public static bool IsHostile(HappinessTier tier)
        {
            return tier is HappinessTier.Unhappy or HappinessTier.Angry;
        }
    }
}

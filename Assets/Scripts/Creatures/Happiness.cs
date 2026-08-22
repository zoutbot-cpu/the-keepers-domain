namespace KeepersDomain.Creatures
{
    /// Non-Imp minions only — Imps don't have moods. Driven by Hunger, Pay,
    /// and productive work: decays while hungry, recovers (capped at
    /// StartingValue) while not, gains a flat bonus on a successful payday,
    /// takes a one-time hit on a missed one, and trickles up further (past
    /// StartingValue, capped at PreferredRoomHappinessCap) while doing a
    /// job in the creature's preferred room — Training Room for Gremlin,
    /// Library for Warlock (its actual first-choice room; Training Room is
    /// only Warlock's fallback and doesn't count). See GremlinAgent/
    /// WarlockAgent's own Tick/TryGetPaid for where those get wired in.
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

        /// Ceiling for the preferred-room-job trickle (see Tick) — separate
        /// from StartingValue since that's still the ceiling for plain
        /// not-hungry recovery. Ecstatic (90+) stays unreachable; Enjoying
        /// Themselves (75+) is now reachable through sustained productive
        /// work.
        public const float PreferredRoomHappinessCap = 85f;

        // All placeholder tuning, not balanced — see design-doc.md's
        // Happiness section.
        private const float HungryDecayPerSecond = 5f / 60f;         // -5 per minute while hungry
        private const float RecoveryPerSecond = 20f / 600f;          // +20 over 10 min while not hungry, capped at StartingValue
        private const float PreferredRoomRecoveryPerSecond = 1f / 60f; // +1 per minute doing a job in the preferred room, capped at PreferredRoomHappinessCap
        private const float UnpaidPenalty = 15f;                     // one-time hit per missed payday
        private const float PaidBonus = 5f;                          // one-time bump per successful payday

        public float Value { get; private set; } = StartingValue;
        public HappinessTier Tier => GetTier(Value);

        /// isHungry drives the base decay/recovery direction; isDoingPreferredRoomJob
        /// layers an additional trickle on top (only when raising, and only past
        /// wherever the base recovery already capped out, since it's stacked as
        /// its own step below) whenever a job is actively bringing the creature
        /// into its preferred room's productive state (Training for Gremlin,
        /// Researching for Warlock — not Warlock's Training fallback). Pay's own
        /// paid/missed-payday hits are applied separately via
        /// ApplyPaidBonus/ApplyUnpaidPenalty, since payday is a discrete event,
        /// not a per-frame state.
        public void Tick(float deltaTime, bool isHungry, bool isDoingPreferredRoomJob)
        {
            Value = isHungry
                ? UnityEngine.Mathf.Max(0f, Value - HungryDecayPerSecond * deltaTime)
                : UnityEngine.Mathf.Min(StartingValue, Value + RecoveryPerSecond * deltaTime);

            if (isDoingPreferredRoomJob)
            {
                Value = UnityEngine.Mathf.Min(PreferredRoomHappinessCap, Value + PreferredRoomRecoveryPerSecond * deltaTime);
            }
        }

        public void ApplyUnpaidPenalty()
        {
            Value = UnityEngine.Mathf.Max(0f, Value - UnpaidPenalty);
        }

        public void ApplyPaidBonus()
        {
            Value = UnityEngine.Mathf.Min(Max, Value + PaidBonus);
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

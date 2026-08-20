using UnityEngine;

namespace KeepersDomain.Creatures
{
    /// Non-Imp minions only (Gremlin, Warlock, ...) — Imps don't get hungry.
    /// Starts full and decays linearly; once it drops to HungryThreshold the
    /// creature is "hungry" and should go eat (see GremlinAgent/WarlockAgent's
    /// priority-80 check). Decay keeps going at the same rate below the
    /// threshold rather than stopping there — nothing currently happens if a
    /// creature goes without food beyond that (no starvation damage), that's
    /// just unspecified/unbuilt for now.
    public class Hunger
    {
        public const float Max = 100f;
        public const float HungryThreshold = 50f;

        // Decay rate is derived so Max reaches HungryThreshold in exactly
        // this many seconds (10 min), per the design brief.
        private const float SecondsFromMaxToHungryThreshold = 600f;
        private const float DecayPerSecond = (Max - HungryThreshold) / SecondsFromMaxToHungryThreshold;

        public float Value { get; private set; } = Max;
        public bool IsHungry => Value <= HungryThreshold;

        public void Tick(float deltaTime)
        {
            Value = Mathf.Max(0f, Value - DecayPerSecond * deltaTime);
        }

        /// Eating fully satiates — restores to Max. No partial-meal/overeating
        /// concept exists (unspecified), so this is the only way hunger moves
        /// back up.
        public void Eat()
        {
            Value = Max;
        }
    }
}

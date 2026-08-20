using UnityEngine;

namespace KeepersDomain.Creatures
{
    /// Non-Imp minions only (Gremlin, Warlock, ...) — Imps don't get paid,
    /// the Keeper mana-conjured them into existence rather than recruiting
    /// them (see ImplingAgent). Wage is BaseWagePerLevel gold per level
    /// (base 5 at level 1, +5 per level gained), drawn from the Treasury
    /// every PayIntervalSeconds for as long as the creature is alive — see
    /// each agent's own Update, which calls Tick and then attempts the
    /// payment itself (Pay has no Treasury reference of its own).
    public class Pay
    {
        public const int BaseWagePerLevel = 5;

        // 10 minutes, per the design brief.
        public const float PayIntervalSeconds = 600f;

        private float _timer;

        /// Set by MarkUnpaid when a payday attempt fails (Treasury couldn't
        /// afford the wage) and cleared by MarkPaid — tracked but has no
        /// further consequence yet (no desertion/morale system exists),
        /// same "tracked, not yet consequential" placeholder pattern
        /// Hunger's missing starvation penalty uses.
        public bool IsUnhappy { get; private set; }

        /// This creature's current wage, per BaseWagePerLevel * level.
        public static int WageFor(int level)
        {
            return level * BaseWagePerLevel;
        }

        /// Advances the pay timer; returns true exactly once every
        /// PayIntervalSeconds ("payday" — the owning agent should attempt a
        /// payment via MarkPaid/MarkUnpaid right after this returns true).
        public bool Tick(float deltaTime)
        {
            _timer += deltaTime;
            if (_timer < PayIntervalSeconds)
            {
                return false;
            }

            _timer -= PayIntervalSeconds;
            return true;
        }

        public void MarkPaid()
        {
            IsUnhappy = false;
        }

        public void MarkUnpaid()
        {
            IsUnhappy = true;
        }
    }
}

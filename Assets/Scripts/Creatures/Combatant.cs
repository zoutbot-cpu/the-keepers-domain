using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Core;
using KeepersDomain.Grid;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Creatures
{
    /// Creature-vs-creature combat — see design-doc.md's Combat section.
    /// Composed into every creature agent the same way Creature / Hunger /
    /// Pay / Happiness are (a plain class the agent ticks, not a
    /// MonoBehaviour), so the whole system lives here once rather than
    /// pasted into six agents. The agent calls Tick(dt) once per frame
    /// BEFORE its own EvaluateAndAct / state machine and returns early when
    /// Tick returns true — combat, fleeing and post-fight healing all drive
    /// the creature's transform directly during those frames.
    ///
    /// Not host-authoritative yet (there's no netcode): every number here is
    /// resolved locally. The single damage funnel (ReceiveHit) and the
    /// StanceRegistry lookup are shaped so a host-authority layer can slot
    /// in later without touching callers.
    public sealed class Combatant
    {
        private const float ScanInterval = 0.25f;
        private const float ChaseReplanInterval = 0.3f;
        private const float LosLostDropSeconds = 3f;
        private const float FleeHpFraction = 0.20f;
        private const float ResumeHpFraction = 0.80f;
        private const float LeashTiles = 7f;
        private const float BreakOffHunger = 30f;
        private const float BreakOffHappiness = 30f;
        private const float LeavingHappiness = 10f;
        private const float AssistRadiusTiles = 5f;
        private const float LairRestFractionPerMinute = 0.25f;
        private const float ExpPerDamageDealt = 1f;
        private const float ExpPerDamageTaken = 0.5f;

        /// Every live Combatant, for target scans. A downed one stays in the
        /// list (its agent is only disabled, not destroyed) — scans skip it
        /// via IsDowned. Removed for good in the agent's OnDestroy (Dispose).
        public static readonly List<Combatant> All = new List<Combatant>();

        /// Whether an Aggressive creature standing over a knocked-out enemy
        /// finishes it off (permadeath) instead of leaving it for the
        /// come-to timer / an enemy Imp to capture. Off by default — set
        /// from BottomMenuBar's Settings menu, reset each game by
        /// GameBootstrap. See design-doc.md's Combat section.
        public static bool AllowFinishOffEnemies;

        private ICombatant _self;
        private Behaviour _agent;
        private GameObject _go;
        private Transform _tf;
        private DungeonGrid _grid;
        private Creature _creature;
        private Hunger _hunger;          // null for Imp
        private Happiness _happiness;    // null for Imp
        private Vector2Int _throneCoord;
        private Func<Vector2Int?> _getLairCoord;
        private Action _onDisengage;
        private bool _isImp;
        private int _owner;

        private ICombatant _target;
        private bool _fleeing;
        private bool _healing;
        private bool _wasActive;
        private bool _engaged;
        private Vector2Int _engageAnchor;

        private float _scanTimer;
        private float _chaseReplanTimer;
        private float _losLostTimer;
        private float _attackTimer;
        private float _expAccum;
        private Vector2Int _chaseTargetCell;

        private readonly List<Vector2Int> _pathBuffer = new List<Vector2Int>();
        private readonly List<Vector2Int> _scratchPath = new List<Vector2Int>();
        private readonly List<Vector3> _waypoints = new List<Vector3>();
        private int _waypointIndex;
        private Vector2Int _pathGoal;

        public bool IsDowned { get; private set; }
        public bool InCombat => _target != null;
        public bool IsFleeing => _fleeing;
        public ICombatant Target => _target;
        public string SelfName => _self != null ? _self.Name : "creature";
        public string SelfSpecies => _self != null ? _self.Species : "creature";

        /// getLairCoord returns the creature's own claimed Lair tile (or null
        /// if it hasn't claimed one) — used as the post-combat heal
        /// destination and the leash anchor. onDisengage is called the frame
        /// combat stops driving the creature, so the agent can reset its
        /// task and re-plan from where combat left it (its own waypoints are
        /// stale by then). hunger/happiness are null for Imps.
        public void Initialize(ICombatant self, Behaviour agent, DungeonGrid grid, Creature creature,
            Hunger hunger, Happiness happiness, Vector2Int throneCoord, Func<Vector2Int?> getLairCoord,
            Action onDisengage, bool isImp)
        {
            _self = self;
            _agent = agent;
            _go = agent.gameObject;
            _tf = agent.transform;
            _grid = grid;
            _creature = creature;
            _hunger = hunger;
            _happiness = happiness;
            _throneCoord = throneCoord;
            _getLairCoord = getLairCoord;
            _onDisengage = onDisengage;
            _isImp = isImp;
            _owner = creature.OwnerId;
            All.Add(this);
        }

        public void Dispose()
        {
            All.Remove(this);
        }

        /// Called by the agent's ReplanPathFromCurrentPosition after the
        /// player's Grab hand drops it — combat does not resume on release
        /// (design-doc.md); the agent re-evaluates fresh.
        public void OnExternalReposition()
        {
            _target = null;
            _engaged = false;
            _fleeing = false;
            _healing = false;
            _waypoints.Clear();
            _waypointIndex = 0;
            _wasActive = false;
        }

        /// Called by DownedBody.StandUp when a fainted creature comes to /
        /// finishes recovering — re-enters the heal state (routes to the
        /// Lair) if it stood up still wounded.
        public void OnRevived()
        {
            IsDowned = false;
            _target = null;
            _engaged = false;
            _fleeing = false;
            _attackTimer = 0f;
            _expAccum = 0f;
            _waypoints.Clear();
            _waypointIndex = 0;
            _healing = !_isImp && _creature != null
                && _creature.Stats.HP < _creature.Stats.MaxHP * ResumeHpFraction;
            _wasActive = _healing;
        }

        public bool Tick(float dt)
        {
            if (IsDowned || _creature == null || _grid == null || StanceRegistry.Current == null)
            {
                return false;
            }

            _attackTimer = Mathf.Min(_attackTimer + dt, 10f);

            // Happiness Leaving overrides combat outright — hand back to the
            // agent, which walks to the Portal / destroys the domain.
            if (_happiness != null && _happiness.Value < LeavingHappiness)
            {
                _target = null;
                _fleeing = false;
                _healing = false;
                return Finish(false);
            }

            // Post-combat / just-came-to healing: walk to the Lair and rest,
            // ignoring fights, until back to 80%.
            if (_healing)
            {
                if (_creature.Stats.HP >= _creature.Stats.MaxHP * ResumeHpFraction)
                {
                    _healing = false;
                }
                else
                {
                    TickHeal(dt);
                    return Finish(true);
                }
            }

            var myCoord = _grid.WorldToGrid(_tf.position);

            // Validate the current target (still up, in LOS recently, not
            // way out of range).
            if (_target != null && !IsTargetAlive(_target))
            {
                _target = null;
            }

            if (_target != null)
            {
                var tCoord = _grid.WorldToGrid(_target.transform.position);
                if (_grid.HasLineOfSight(myCoord, tCoord))
                {
                    _losLostTimer = 0f;
                }
                else
                {
                    _losLostTimer += dt;
                    if (_losLostTimer >= LosLostDropSeconds)
                    {
                        _target = null;
                    }
                }

                if (_target != null
                    && Vector2Int.Distance(myCoord, tCoord) > Mathf.Max(2f, _creature.Stats.AggroRadius * 2f))
                {
                    _target = null;
                }
            }

            _scanTimer += dt;
            if (_scanTimer >= ScanInterval)
            {
                _scanTimer = 0f;
                RefreshPerception(myCoord);
            }

            // Imp running from a non-Imp — its top concern.
            if (_fleeing)
            {
                DropTarget();
                TickFleeToThrone(dt, myCoord);
                return Finish(true);
            }

            if (_target == null)
            {
                DropTarget();

                if (TryFinishDownedEnemy(myCoord))
                {
                    return Finish(true);
                }

                // Siege the enemy Throne if it's in range and this creature
                // is healthy enough to bother — lower priority than a
                // creature fight, higher than going home to heal.
                if (!_isImp && _creature.Stats.HP > _creature.Stats.MaxHP * FleeHpFraction
                    && TryAttackStructure(myCoord, dt))
                {
                    return Finish(true);
                }

                if (!_isImp && _creature.Stats.HP < _creature.Stats.MaxHP * ResumeHpFraction)
                {
                    _healing = true;
                    TickHeal(dt);
                    return Finish(true);
                }

                return Finish(false);
            }

            // First frame of this engagement — remember where it started, so
            // the leash below measures how far the creature has been dragged
            // *chasing*, not how far it is from its own Keeper's territory (a
            // creature dropped in an enemy base still defends itself).
            if (!_engaged)
            {
                _engaged = true;
                _engageAnchor = myCoord;
            }

            // Break-off checks while engaged, in priority order.
            if (_creature.Stats.HP <= _creature.Stats.MaxHP * FleeHpFraction)
            {
                DropTarget();
                _fleeing = true;
                TickFleeToThrone(dt, myCoord);
                return Finish(true);
            }

            if (_hunger != null && _hunger.Value < BreakOffHunger)
            {
                DropTarget();
                return Finish(false);
            }

            if (_happiness != null && _happiness.Value < BreakOffHappiness && CanReachAnchor())
            {
                DropTarget();
                return Finish(false);
            }

            if (Vector2Int.Distance(myCoord, _engageAnchor) > LeashTiles)
            {
                DropTarget();
                return Finish(false);
            }

            TickFight(dt, myCoord);
            return Finish(true);
        }

        private void DropTarget()
        {
            _target = null;
            _engaged = false;
        }

        /// The single point every hit lands through — armor, HP, exp for
        /// damage taken, retaliation, the assist alarm, and the faint. Runs
        /// on the victim's Combatant. Returns the damage actually applied
        /// (after armor) so the attacker can credit exp / lifesteal.
        public int ReceiveHit(int rawDamage, ICombatant attacker)
        {
            if (IsDowned || _creature == null)
            {
                return 0;
            }

            var applied = Mathf.Max(0, rawDamage - Mathf.RoundToInt(_creature.Stats.Armor));
            if (applied <= 0)
            {
                return 0;
            }

            _creature.Stats.HP -= applied;
            AddExp(applied * ExpPerDamageTaken);

            if (attacker != null && attacker.Combat != null && !attacker.Combat.IsDowned)
            {
                if (_healing)
                {
                    _healing = false;
                    if (_target == null)
                    {
                        _target = attacker;
                    }
                }
                else if (!_isImp && _target == null)
                {
                    _target = attacker;
                }

                RaiseAlarm(attacker);
            }

            if (_creature.Stats.HP <= 0f)
            {
                _creature.Stats.HP = 0f;
                Faint(attacker);
            }

            return applied;
        }

        private void RefreshPerception(Vector2Int myCoord)
        {
            if (_isImp)
            {
                _fleeing = AnyFearedCreatureNear(myCoord);
                _target = _fleeing ? null : AcquireNearest(myCoord, requireImp: true, mutualOnly: true);
                return;
            }

            // Only ever acquires an Aggressive-stance target on sight. A
            // Neutral/Friendly creature that's fighting back a specific
            // attacker (set in ReceiveHit) keeps that target — don't clear
            // it just because a fresh scan finds nobody to aggro.
            var found = AcquireNearest(myCoord, requireImp: false, mutualOnly: false);
            if (found != null)
            {
                _target = found;
            }
        }

        private ICombatant AcquireNearest(Vector2Int myCoord, bool requireImp, bool mutualOnly)
        {
            var radius = Mathf.Max(1f, _creature.Stats.AggroRadius);
            ICombatant best = null;
            var bestDist = float.MaxValue;

            foreach (var other in All)
            {
                if (other == this || other.IsDowned || other._self == null || other._creature == null)
                {
                    continue;
                }

                if (requireImp && !other._isImp)
                {
                    continue;
                }

                var hostile = mutualOnly
                    ? MutualAggression(_owner, other._owner)
                    : StanceRegistry.Current.IsHostileOnSight(_owner, other._owner);
                if (!hostile)
                {
                    continue;
                }

                var oCoord = _grid.WorldToGrid(other._tf.position);
                var d = Vector2Int.Distance(myCoord, oCoord);
                if (d > radius || d >= bestDist)
                {
                    continue;
                }

                if (!_grid.HasLineOfSight(myCoord, oCoord))
                {
                    continue;
                }

                best = other._self;
                bestDist = d;
            }

            return best;
        }

        private bool AnyFearedCreatureNear(Vector2Int myCoord)
        {
            var radius = Mathf.Max(1f, _creature.Stats.AggroRadius);
            foreach (var other in All)
            {
                if (other == this || other.IsDowned || other._self == null || other._isImp)
                {
                    continue;
                }

                if (!MutualAggression(_owner, other._owner))
                {
                    continue;
                }

                var oCoord = _grid.WorldToGrid(other._tf.position);
                if (Vector2Int.Distance(myCoord, oCoord) > radius)
                {
                    continue;
                }

                if (_grid.HasLineOfSight(myCoord, oCoord))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MutualAggression(int a, int b)
        {
            if (a == b)
            {
                return false;
            }

            var s = StanceRegistry.Current;
            return s.Get(a, b) == Stance.Aggressive || s.Get(b, a) == Stance.Aggressive;
        }

        private bool IsTargetAlive(ICombatant t)
        {
            return t != null && t.Combat != null && !t.Combat.IsDowned && t.Creature != null
                && t.transform != null;
        }

        private void TickFight(float dt, Vector2Int myCoord)
        {
            var tCoord = _grid.WorldToGrid(_target.transform.position);

            // Melee range is Chebyshev 1 — anything from stacked on the
            // target to diagonally adjacent.
            if (Chebyshev(myCoord, tCoord) <= 1)
            {
                // Hard stop: drop any leftover chase path so the creature
                // holds its ground and trades blows instead of drifting.
                _waypoints.Clear();
                _waypointIndex = 0;

                var interval = _creature.Stats.Attackspeed > 0.01f ? 1f / _creature.Stats.Attackspeed : 1f;
                if (_attackTimer >= interval)
                {
                    _attackTimer = 0f;
                    var dmg = Mathf.Max(1, Mathf.RoundToInt(_creature.Stats.Strength));
                    var applied = _target.Combat.ReceiveHit(dmg, _self);
                    if (applied > 0)
                    {
                        GameplayLog.Write(_owner,
                            $"{_self.Name} hits {Label(_target)} for {applied} ({HpText(_target)})");
                        AddExp(applied * ExpPerDamageDealt);
                        if (_creature.Stats.Lifesteal > 0f)
                        {
                            _creature.Stats.HP = Mathf.Min(_creature.Stats.MaxHP,
                                _creature.Stats.HP + _creature.Stats.Lifesteal);
                        }
                    }
                }

                return;
            }

            // Close the distance — path to a tile *beside* the target (not
            // onto it), and re-plan the instant the target changes tile so
            // the chase stays tight instead of lagging a stale path.
            _chaseReplanTimer += dt;
            if (_chaseReplanTimer >= ChaseReplanInterval || _waypointIndex >= _waypoints.Count
                || tCoord != _chaseTargetCell)
            {
                _chaseReplanTimer = 0f;
                _chaseTargetCell = tCoord;
                if (!PlanPathTo(ApproachCell(myCoord, tCoord)) && !PlanPathTo(tCoord))
                {
                    _target = null;
                    return;
                }
            }

            MoveAlongPath();
        }

        /// The walkable cardinal neighbour of target closest to `from` — so
        /// a chasing creature ends up standing next to the target rather
        /// than stacking on its tile. Falls back to target itself if it's
        /// boxed in.
        private Vector2Int ApproachCell(Vector2Int from, Vector2Int target)
        {
            var best = target;
            var bestDist = int.MaxValue;
            foreach (var dir in GridDirections.Cardinal)
            {
                var cell = target + dir;
                if (!_grid.IsWalkable(cell, _isImp))
                {
                    continue;
                }

                var d = Mathf.Abs(cell.x - from.x) + Mathf.Abs(cell.y - from.y);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = cell;
                }
            }

            return best;
        }

        private bool TryFinishDownedEnemy(Vector2Int myCoord)
        {
            if (_isImp || !AllowFinishOffEnemies)
            {
                return false;
            }

            foreach (var body in DownedBody.All)
            {
                if (body == null || body.OwnerId == _owner || body.OwnerId == StanceRegistry.WildOwnerId)
                {
                    continue;
                }

                if (StanceRegistry.Current.Get(_owner, body.OwnerId) != Stance.Aggressive)
                {
                    continue;
                }

                if (Chebyshev(myCoord, _grid.WorldToGrid(body.transform.position)) > 1)
                {
                    continue;
                }

                var interval = _creature.Stats.Attackspeed > 0.01f ? 1f / _creature.Stats.Attackspeed : 1f;
                if (_attackTimer >= interval)
                {
                    _attackTimer = 0f;
                    var dmg = Mathf.Max(1, Mathf.RoundToInt(_creature.Stats.Strength));
                    GameplayLog.Write(_owner,
                        $"{_self.Name} strikes downed {GameplayLog.OwnerTag(body.OwnerId)}{body.DisplayName} for {dmg}");
                    body.ReceiveFinishingHit(dmg, _self);
                    AddExp(dmg * ExpPerDamageDealt);
                }

                return true;
            }

            return false;
        }

        /// Walk up to and hit the nearest hostile IAttackTarget (the enemy
        /// Throne) in aggro range + LOS — a fallback for a creature with no
        /// creature to fight. Same chase-to-a-tile-beside-it / hold-and-hit
        /// shape as TickFight.
        private bool TryAttackStructure(Vector2Int myCoord, float dt)
        {
            if (AttackTargets.All.Count == 0)
            {
                return false;
            }

            var radius = Mathf.Max(1f, _creature.Stats.AggroRadius);
            IAttackTarget best = null;
            var bestDist = float.MaxValue;

            foreach (var structure in AttackTargets.All)
            {
                if (structure == null || !structure.IsAlive
                    || !StanceRegistry.Current.IsHostileOnSight(_owner, structure.OwnerId))
                {
                    continue;
                }

                var d = Vector2Int.Distance(myCoord, structure.Coord);
                if (d > radius || d >= bestDist || !_grid.HasLineOfSight(myCoord, structure.Coord))
                {
                    continue;
                }

                best = structure;
                bestDist = d;
            }

            if (best == null)
            {
                return false;
            }

            var tCoord = best.Coord;
            if (Chebyshev(myCoord, tCoord) <= 1)
            {
                _waypoints.Clear();
                _waypointIndex = 0;

                var interval = _creature.Stats.Attackspeed > 0.01f ? 1f / _creature.Stats.Attackspeed : 1f;
                if (_attackTimer >= interval)
                {
                    _attackTimer = 0f;
                    var dmg = Mathf.Max(1, Mathf.RoundToInt(_creature.Stats.Strength));
                    best.ReceiveAttack(dmg, _self);   // logs its own running-HP line
                    AddExp(dmg * ExpPerDamageDealt);
                    AlertStructureDefenders(best, myCoord);
                }

                return true;
            }

            _chaseReplanTimer += dt;
            if (_chaseReplanTimer >= ChaseReplanInterval || _waypointIndex >= _waypoints.Count)
            {
                _chaseReplanTimer = 0f;
                if (!PlanPathTo(ApproachCell(myCoord, tCoord)) && !PlanPathTo(tCoord))
                {
                    return false;
                }
            }

            MoveAlongPath();
            return true;
        }

        /// Attacking the enemy Throne rallies its Keeper's nearby idle
        /// defenders onto the attacker (unless that Keeper is Friendly to
        /// this one, in which case there's no fight to answer).
        private void AlertStructureDefenders(IAttackTarget structure, Vector2Int myCoord)
        {
            if (!StanceRegistry.Current.AlliesAssist(structure.OwnerId, _owner))
            {
                return;
            }

            foreach (var ally in All)
            {
                if (ally == this || ally._isImp || ally.IsDowned || ally._owner != structure.OwnerId)
                {
                    continue;
                }

                if (ally.InCombat || ally._healing || ally._fleeing)
                {
                    continue;
                }

                var allyCoord = _grid.WorldToGrid(ally._tf.position);
                if (Vector2Int.Distance(allyCoord, structure.Coord) > AssistRadiusTiles)
                {
                    continue;
                }

                if (_grid.HasLineOfSight(allyCoord, myCoord))
                {
                    ally._target = _self;
                }
            }
        }

        private void TickFleeToThrone(float dt, Vector2Int myCoord)
        {
            if (Chebyshev(myCoord, _throneCoord) <= 2)
            {
                _fleeing = false;
                if (!_isImp && _creature.Stats.HP < _creature.Stats.MaxHP * ResumeHpFraction)
                {
                    _healing = true;
                }

                return;
            }

            _chaseReplanTimer += dt;
            if (_chaseReplanTimer >= ChaseReplanInterval || _waypointIndex >= _waypoints.Count)
            {
                _chaseReplanTimer = 0f;
                PlanPathTo(_throneCoord);
            }

            MoveAlongPath();
        }

        private void TickHeal(float dt)
        {
            var lair = _getLairCoord?.Invoke();
            if (lair.HasValue)
            {
                var myCoord = _grid.WorldToGrid(_tf.position);
                if (myCoord != lair.Value)
                {
                    _chaseReplanTimer += dt;
                    if (_chaseReplanTimer >= ChaseReplanInterval || _waypointIndex >= _waypoints.Count)
                    {
                        _chaseReplanTimer = 0f;
                        PlanPathTo(lair.Value);
                    }

                    MoveAlongPath();
                }
            }

            var perSecond = _creature.Stats.MaxHP * LairRestFractionPerMinute / 60f;
            _creature.Stats.HP = Mathf.Min(_creature.Stats.MaxHP, _creature.Stats.HP + perSecond * dt);
        }

        private void RaiseAlarm(ICombatant attacker)
        {
            if (attacker.Combat == null
                || !StanceRegistry.Current.AlliesAssist(_owner, attacker.Combat._owner))
            {
                return;
            }

            var atkCoord = _grid.WorldToGrid(attacker.transform.position);
            foreach (var ally in All)
            {
                if (ally == this || ally._isImp || ally.IsDowned || ally._owner != _owner)
                {
                    continue;
                }

                if (ally.InCombat || ally._healing || ally._fleeing)
                {
                    continue;
                }

                var allyCoord = _grid.WorldToGrid(ally._tf.position);
                if (Vector2Int.Distance(allyCoord, atkCoord) > AssistRadiusTiles && Vector2Int.Distance(
                        _grid.WorldToGrid(_tf.position), allyCoord) > AssistRadiusTiles)
                {
                    continue;
                }

                if (_grid.HasLineOfSight(allyCoord, atkCoord))
                {
                    ally._target = attacker;
                }
            }
        }

        private void Faint(ICombatant killer)
        {
            IsDowned = true;
            _target = null;
            _engaged = false;
            _fleeing = false;
            _healing = false;
            _waypoints.Clear();
            _waypointIndex = 0;

            var body = _go.AddComponent<DownedBody>();
            body.Configure(this, _agent, _grid, _creature, _throneCoord, _getLairCoord, _isImp);

            _onDisengage?.Invoke();
            _wasActive = false;
        }

        private bool CanReachAnchor()
        {
            var anchor = _getLairCoord?.Invoke() ?? _throneCoord;
            return AStarPathfinder.TryFindPath(_grid, _grid.WorldToGrid(_tf.position), anchor, _scratchPath, _isImp);
        }

        private void AddExp(float amount)
        {
            if (amount <= 0f)
            {
                return;
            }

            _expAccum += amount;
            if (_expAccum >= 1f)
            {
                var whole = Mathf.FloorToInt(_expAccum);
                _expAccum -= whole;
                _creature.AddExp(whole);
            }
        }

        private bool Finish(bool active)
        {
            if (_wasActive && !active)
            {
                _onDisengage?.Invoke();
            }

            _wasActive = active;
            return active;
        }

        private bool PlanPathTo(Vector2Int goal)
        {
            _pathGoal = goal;
            _waypoints.Clear();
            _waypointIndex = 0;

            var start = _grid.WorldToGrid(_tf.position);
            if (start == goal)
            {
                return true;
            }

            if (!AStarPathfinder.TryFindPath(_grid, start, goal, _pathBuffer, _isImp))
            {
                return false;
            }

            foreach (var cell in _pathBuffer)
            {
                _waypoints.Add(_grid.GridToWorld(cell));
            }

            return _waypoints.Count > 0;
        }

        private void MoveAlongPath()
        {
            if (_waypointIndex >= _waypoints.Count)
            {
                return;
            }

            var target = _waypoints[_waypointIndex];
            var flat = new Vector3(target.x, _tf.position.y, target.z);
            _tf.position = Vector3.MoveTowards(_tf.position, flat, _creature.Stats.Movespeed * Time.deltaTime);
            if (Vector3.Distance(_tf.position, flat) < 0.05f)
            {
                _waypointIndex++;
            }
        }

        private static int Chebyshev(Vector2Int a, Vector2Int b)
        {
            return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }

        private static string Label(ICombatant c)
        {
            return $"{GameplayLog.OwnerTag(c.Creature.OwnerId)}{c.Name}";
        }

        private static string HpText(ICombatant c)
        {
            return $"{c.Creature.Stats.HP:0}/{c.Creature.Stats.MaxHP:0} HP";
        }
    }
}

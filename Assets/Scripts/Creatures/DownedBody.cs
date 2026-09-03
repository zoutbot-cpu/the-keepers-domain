using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Creatures
{
    /// A creature knocked to 0 HP — see design-doc.md's Combat section. The
    /// live agent isn't destroyed, just disabled; this component is added to
    /// the same GameObject and takes over. The creature comes back exactly
    /// as it was (its Creature, level, exp, name all still on the disabled
    /// agent) if it's rescued or left alone long enough; it's gone for good
    /// if finished off, or dropped on Lava/Chasm.
    ///
    /// Carrying is done by an Imp (Rescue Ally / Capture Enemy jobs — see
    /// BuilderJobBoard) or the player's Grab hand: the carrier moves this
    /// transform and calls BeginCarry / DropFromCarry / BeginRecovery.
    public class DownedBody : MonoBehaviour
    {
        /// Every downed body right now — for Imp job discovery, the
        /// "Aggressive creature finishes a downed enemy" check, and the Grab
        /// hand. Registered in Configure, unregistered in OnDestroy.
        public static readonly List<DownedBody> All = new List<DownedBody>();

        private const float ComeToSeconds = 60f;
        private const float FaintHpFraction = 0.10f;
        private const float RecoverFractionPerMinute = 0.25f;

        // A jailed prisoner slowly patches itself up: an instant 10% MaxHP
        // on being thrown in, then 5% MaxHP per minute. Shows on the
        // creature's health ring (still ticking on the parked capsule).
        private const float JailEntryHealFraction = 0.10f;
        private const float JailRecoverFractionPerMinute = 0.05f;

        private Combatant _combat;
        private Behaviour _agent;
        private DungeonGrid _grid;
        private Creature _creature;
        private bool _isImp;

        private float _faintHp;
        private float _comeToTimer;
        private bool _recovering;
        private bool _carried;
        private bool _jailed;

        // Cheap "knocked out" read: tip the placeholder capsule onto its
        // side while down, restore its upright rotation on stand-up. A real
        // model would have a proper faint animation instead.
        private Quaternion _uprightRotation;
        private static readonly Quaternion DownRotation = Quaternion.Euler(90f, 0f, 0f);

        public int OwnerId => _creature != null ? _creature.OwnerId : -1;
        public bool IsImp => _isImp;
        public bool IsCarried => _carried;
        public bool IsRecovering => _recovering;
        public string DisplayName => _combat != null ? _combat.SelfName : name;
        public string Species => _combat != null ? _combat.SelfSpecies : "creature";
        public int Level => _creature != null ? _creature.Level : 1;

        public void Configure(Combatant combat, Behaviour agent, DungeonGrid grid, Creature creature,
            Vector2Int throneCoord, Func<Vector2Int?> getLairCoord, bool isImp)
        {
            _combat = combat;
            _agent = agent;
            _grid = grid;
            _creature = creature;
            _isImp = isImp;

            _faintHp = Mathf.Max(1f, creature.Stats.MaxHP * FaintHpFraction);
            _creature.Stats.HP = 0f;
            if (_agent != null)
            {
                _agent.enabled = false;
            }

            _uprightRotation = transform.rotation;
            transform.rotation = DownRotation;

            All.Add(this);
            GameplayLog.Write(OwnerId, $"{DisplayName} was knocked out (faint-HP {_faintHp:0})");
        }

        private void OnDestroy()
        {
            All.Remove(this);
        }

        private void Update()
        {
            if (_creature == null || _grid == null)
            {
                return;
            }

            if (_jailed)
            {
                var jailPerSecond = _creature.Stats.MaxHP * JailRecoverFractionPerMinute / 60f;
                _creature.Stats.HP = Mathf.Min(_creature.Stats.MaxHP,
                    _creature.Stats.HP + jailPerSecond * Time.deltaTime);
                return;
            }

            // While carried the body rides above the terrain in the hand /
            // on the Imp — only a body actually at rest on Lava/Chasm dies
            // to it (a drop, not a fly-over).
            if (_carried)
            {
                return;
            }

            var tileType = _grid.GetTile(_grid.WorldToGrid(transform.position)).Type;
            if (tileType == TileType.Lava || tileType == TileType.Chasm)
            {
                Permadeath($"dropped into {tileType}");
                return;
            }

            if (_recovering)
            {
                var perSecond = _creature.Stats.MaxHP * RecoverFractionPerMinute / 60f;
                _creature.Stats.HP = Mathf.Min(_creature.Stats.MaxHP,
                    _creature.Stats.HP + perSecond * Time.deltaTime);
                if (_creature.Stats.HP >= _creature.Stats.MaxHP)
                {
                    StandUp();
                }

                return;
            }

            _comeToTimer += Time.deltaTime;
            if (_comeToTimer >= ComeToSeconds)
            {
                StandUp();
            }
        }

        /// A deliberate finishing blow from an Aggressive enemy standing over
        /// the body (see Combatant.TryFinishDownedEnemy). Chews through the
        /// faint-HP buffer; 0 is permadeath. Any hit also resets the
        /// come-to timer.
        public void ReceiveFinishingHit(int damage, ICombatant attacker)
        {
            _comeToTimer = 0f;
            _faintHp -= Mathf.Max(1, damage);
            GameplayLog.Write(OwnerId, $"{DisplayName} takes a finishing blow ({_faintHp:0} faint-HP left)");
            if (_faintHp <= 0f)
            {
                Permadeath(attacker != null ? $"finished off by {attacker.Name}" : "finished off");
            }
        }

        public void BeginCarry()
        {
            _carried = true;
            _recovering = false;
        }

        public void DropFromCarry()
        {
            _carried = false;
            _comeToTimer = 0f;
        }

        /// Delivered to one of its own Keeper's Lair tiles by a Rescue Ally
        /// job — recover 25% MaxHP/min in place, then stand up at full HP.
        public void BeginRecovery()
        {
            _carried = false;
            _recovering = true;
            _comeToTimer = 0f;
            GameplayLog.Write(OwnerId, $"{DisplayName} is recovering in the Lair");
        }

        /// Hauled into a Jail pit (see JailManager.TryCaptureBody) — the
        /// creature's own capsule stays parked in the pit as the prisoner;
        /// come-to / recovery / finish-off all stop, and it drops out of the
        /// live-body registry so nothing tries to rescue or re-capture it.
        /// Ends when the Jail releases it (Conversion Class, or a Jail sale),
        /// which destroys this GameObject.
        public bool IsJailed => _jailed;

        public void MarkJailed()
        {
            _jailed = true;
            _carried = false;
            _recovering = false;
            _creature.Stats.HP = Mathf.Min(_creature.Stats.MaxHP,
                _creature.Stats.HP + _creature.Stats.MaxHP * JailEntryHealFraction);
            if (_agent != null)
            {
                _agent.enabled = false;
            }

            All.Remove(this);
        }

        private void StandUp()
        {
            _creature.Stats.HP = Mathf.Clamp(Mathf.Max(_creature.Stats.HP, _faintHp), 1f, _creature.Stats.MaxHP);
            transform.rotation = _uprightRotation;
            if (_agent != null)
            {
                _agent.enabled = true;
            }

            _combat?.OnRevived();
            GameplayLog.Write(OwnerId, $"{DisplayName} came to ({_creature.Stats.HP:0} HP)");
            Destroy(this);
        }

        private void Permadeath(string reason)
        {
            GameplayLog.Write(OwnerId, $"{DisplayName} died — {reason}");
            Destroy(gameObject);
        }
    }
}

using Unity.Netcode;
using UnityEngine;
using KeepersDomain.Creatures;
using KeepersDomain.Grid;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Net
{
    /// One networked creature. The prefab (Resources/Net/CreatureNetView) is
    /// a capsule + NetworkObject + NetworkTransform (server-authoritative,
    /// position/rotation only) + this. The host spawner instantiates it,
    /// adds the real species agent, and calls HostFinalize; the client gets
    /// a ghost that renders the capsule + health ring from the netvars,
    /// with NetworkTransform driving its position.
    public class CreatureNetView : NetworkBehaviour
    {
        /// True on the host of a running networked game — spawners take the
        /// networked path only then; offline and on the client, false.
        public static bool HostActive =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        private readonly NetworkVariable<EditorCreatureKind> _species = new NetworkVariable<EditorCreatureKind>();
        private readonly NetworkVariable<int> _owner = new NetworkVariable<int>();
        private readonly NetworkVariable<float> _hp = new NetworkVariable<float>(1f);
        private readonly NetworkVariable<float> _maxHp = new NetworkVariable<float>(1f);
        private readonly NetworkVariable<int> _level = new NetworkVariable<int>(1);
        private readonly NetworkVariable<bool> _downed = new NetworkVariable<bool>();

        // Host only.
        private Creature _creature;
        private DownedBody _downedProbe;

        // Client only.
        private bool _clientReady;

        /// Host path — instantiate the prefab + shape it for `kind`,
        /// grounded on groundPos. Spawners call this instead of
        /// CreatureFactory.CreateOfflineBody.
        public static GameObject CreateHostBody(EditorCreatureKind kind, Vector3 groundPos)
        {
            var go = Object.Instantiate(Resources.Load<GameObject>("Net/CreatureNetView"));
            CreatureFactory.ShapeBody(go, kind, groundPos);
            return go;
        }

        /// Host — bind to the live Creature (HP/level mirror source), Spawn,
        /// then set the identity netvars. A NetworkVariable can only be
        /// written once its NetworkObject is spawned, so Spawn() must come
        /// first; the client's ClientInit reads _species.Value (and
        /// subscribes to OnValueChanged) after the spawn arrives, so it
        /// still sees the right species. A no-op on an offline body (which
        /// has no CreatureNetView), so spawners can call it unconditionally.
        public static void HostFinalize(GameObject go, EditorCreatureKind kind, Creature creature)
        {
            var view = go.GetComponent<CreatureNetView>();
            if (view == null)
            {
                return;
            }

            view._creature = creature;
            go.GetComponent<NetworkObject>().Spawn();

            view._species.Value = kind;
            view._owner.Value = creature.OwnerId;
            view._maxHp.Value = creature.Stats.MaxHP;
            view._hp.Value = creature.Stats.HP;
            view._level.Value = creature.Level;
        }

        private void Update()
        {
            if (IsServer)
            {
                HostMirror();
            }
            else
            {
                ClientInit();
            }
        }

        private void HostMirror()
        {
            if (_creature == null)
            {
                return;
            }

            if (!Mathf.Approximately(_hp.Value, _creature.Stats.HP)) _hp.Value = _creature.Stats.HP;
            if (!Mathf.Approximately(_maxHp.Value, _creature.Stats.MaxHP)) _maxHp.Value = _creature.Stats.MaxHP;
            if (_level.Value != _creature.Level) _level.Value = _creature.Level;

            if (_downedProbe == null)
            {
                _downedProbe = GetComponent<DownedBody>();
            }

            var downed = _downedProbe != null;
            if (_downed.Value != downed) _downed.Value = downed;
        }

        private void ClientInit()
        {
            if (_clientReady)
            {
                return;
            }

            // BuildClientWorld (triggered from NetGame's spawn) may not have
            // created the grid yet if this ghost replicated in first — retry
            // until it exists.
            var grid = FindAnyObjectByType<DungeonGrid>();
            if (grid == null)
            {
                return;
            }

            _clientReady = true;
            CreatureFactory.ApplyLook(gameObject, _species.Value);
            _species.OnValueChanged += (_, kind) => CreatureFactory.ApplyLook(gameObject, kind);

            CreatureHealthRing.Attach(gameObject,
                () => _maxHp.Value > 0f ? Mathf.Clamp01(_hp.Value / _maxHp.Value) : 0f,
                () => _owner.Value,
                grid);
        }
    }
}

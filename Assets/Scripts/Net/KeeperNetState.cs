using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using KeepersDomain.Core;

namespace KeepersDomain.Net
{
    /// One per keeper (prefab Resources/Net/KeeperNetState, spawned by the
    /// host in OnHostReady). Mirrors that keeper's headline economy numbers
    /// from its KeeperContext into netvars every frame so the client's HUD
    /// can show its own gold / mana / bacon / throne HP without running any
    /// of the simulation. Registers itself in a static per-owner lookup the
    /// client HUD reads.
    public class KeeperNetState : NetworkBehaviour
    {
        private static readonly Dictionary<int, KeeperNetState> ByOwner = new Dictionary<int, KeeperNetState>();

        public static KeeperNetState ForOwner(int ownerId) =>
            ByOwner.TryGetValue(ownerId, out var s) ? s : null;

        public readonly NetworkVariable<int> OwnerId = new NetworkVariable<int>(-1);
        public readonly NetworkVariable<int> Gold = new NetworkVariable<int>();
        public readonly NetworkVariable<int> Mana = new NetworkVariable<int>();
        public readonly NetworkVariable<int> MaxMana = new NetworkVariable<int>();
        public readonly NetworkVariable<int> Bacon = new NetworkVariable<int>();
        public readonly NetworkVariable<int> ThroneHp = new NetworkVariable<int>();
        public readonly NetworkVariable<int> ThroneMaxHp = new NetworkVariable<int>();

        private KeeperContext _ctx;

        /// Host — call after Spawn (so the OwnerId write replicates).
        public void HostBind(KeeperContext ctx)
        {
            _ctx = ctx;
            OwnerId.Value = ctx.OwnerId;
            ByOwner[ctx.OwnerId] = this;
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer)
            {
                Register();
                OwnerId.OnValueChanged += (_, __) => Register();
            }
        }

        public override void OnNetworkDespawn()
        {
            if (OwnerId.Value >= 0 && ByOwner.TryGetValue(OwnerId.Value, out var s) && s == this)
            {
                ByOwner.Remove(OwnerId.Value);
            }
        }

        private void Register()
        {
            if (OwnerId.Value >= 0)
            {
                ByOwner[OwnerId.Value] = this;
            }
        }

        private void Update()
        {
            if (!IsServer || _ctx == null)
            {
                return;
            }

            Set(Gold, _ctx.Treasury != null ? _ctx.Treasury.TotalGold : 0);
            Set(Mana, _ctx.Throne != null ? _ctx.Throne.CurrentMana : 0);
            Set(MaxMana, _ctx.Throne != null ? _ctx.Throne.MaxMana : 0);
            Set(Bacon, _ctx.Tavern != null ? _ctx.Tavern.TotalBacon : 0);
            Set(ThroneHp, _ctx.Throne != null ? _ctx.Throne.Hp : 0);
            Set(ThroneMaxHp, _ctx.Throne != null ? _ctx.Throne.MaxHp : 0);
        }

        private static void Set(NetworkVariable<int> v, int value)
        {
            if (v.Value != value)
            {
                v.Value = value;
            }
        }
    }
}

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using KeepersDomain.Core;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;

namespace KeepersDomain.Net
{
    /// One per keeper (prefab Resources/Net/KeeperNetState, spawned by the
    /// host in OnHostReady). Mirrors that keeper's headline economy numbers
    /// from its KeeperContext into netvars every frame so the client's HUD
    /// can show its own gold / mana / bacon / throne HP without running any
    /// of the simulation. Registers itself in a static per-owner lookup the
    /// client HUD reads.
    ///
    /// Also carries the keeper's Throne / Portal tile coords (fixed at world
    /// build) so the client can put those two landmark props in-world — the
    /// client builds no KeeperContext, so this is the only channel for them.
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

        // Tile coords of this keeper's two landmark rooms, packed
        // (throneX, throneY, portalX, portalY) — all -1 until HostBind runs.
        // Vector4 so it rides one built-in NGO serializer; the client
        // watches it to build the props.
        public readonly NetworkVariable<Vector4> LandmarkCoords =
            new NetworkVariable<Vector4>(new Vector4(-1f, -1f, -1f, -1f));

        private KeeperContext _ctx;

        // Client — the props it has already built (so it builds them once).
        private bool _clientPropsBuilt;

        /// Host — call after Spawn (so the OwnerId write replicates).
        public void HostBind(KeeperContext ctx)
        {
            _ctx = ctx;
            OwnerId.Value = ctx.OwnerId;
            LandmarkCoords.Value = new Vector4(
                ctx.ThroneCoord.x, ctx.ThroneCoord.y, ctx.PortalCoord.x, ctx.PortalCoord.y);
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
            if (!IsServer)
            {
                TryBuildClientProps();
                return;
            }

            if (_ctx == null)
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

        /// Client — once the coords have replicated and BuildClientWorld's
        /// grid exists, drop this keeper's Throne Room + Portal props in
        /// world. Visual only: ThroneRoom/Portal.Initialize just position a
        /// prop and (for the Throne) attach a hide-when-full health ring
        /// reading a local Creature that never takes damage here, so the
        /// ring stays hidden — the HUD's throne HP comes off ThroneHp.
        private void TryBuildClientProps()
        {
            if (_clientPropsBuilt)
            {
                return;
            }

            var packed = LandmarkCoords.Value;
            if (packed.x < 0f || packed.z < 0f)
            {
                return;
            }

            var throne = new Vector2Int(Mathf.RoundToInt(packed.x), Mathf.RoundToInt(packed.y));
            var portal = new Vector2Int(Mathf.RoundToInt(packed.z), Mathf.RoundToInt(packed.w));

            var grid = FindAnyObjectByType<DungeonGrid>();
            if (grid == null)
            {
                return;
            }

            _clientPropsBuilt = true;

            var ownerColor = grid.GetOwnerColor(OwnerId.Value);

            var throneGo = new GameObject($"ThroneRoom P{OwnerId.Value + 1} (client)");
            var throneRoom = throneGo.AddComponent<ThroneRoom>();
            throneRoom.PlayerColor = ownerColor;
            throneRoom.Initialize(throne, grid, OwnerId.Value);

            var portalGo = new GameObject($"Portal P{OwnerId.Value + 1} (client)");
            portalGo.AddComponent<Portal>().Initialize(portal, grid);
        }
    }
}

using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace KeepersDomain.Net
{
    /// One row in the lobby roster — who's connected and whether they've
    /// readied up. Unmanaged (ulong + two bools) so it rides a NetworkList.
    public struct LobbyPlayer : INetworkSerializable, IEquatable<LobbyPlayer>
    {
        public ulong ClientId;
        public bool IsHost;
        public bool Ready;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ClientId);
            s.SerializeValue(ref IsHost);
            s.SerializeValue(ref Ready);
        }

        public bool Equals(LobbyPlayer other) =>
            ClientId == other.ClientId && IsHost == other.IsHost && Ready == other.Ready;

        public override bool Equals(object obj) => obj is LobbyPlayer other && Equals(other);

        public override int GetHashCode() => ClientId.GetHashCode();
    }

    /// The pre-game lobby's one networked object (prefab
    /// Resources/Net/NetLobby, spawned by the host in
    /// GameBootstrap.OnHostReady the moment the Relay transport is up —
    /// BEFORE any world is built). Everyone connected shows up in Players;
    /// each toggles their own Ready flag; the host's "Start Game" only
    /// unlocks once every player is ready, so nobody drops into a game
    /// that's already running. On start the host flips GameStarted and
    /// runs the authoritative world build (OnHostBuildGame) — NetGame
    /// spawning then pulls each client into BuildClientWorld exactly as
    /// before.
    ///
    /// It stays spawned for the session (harmless once GameStarted is set);
    /// NetSession.Leave()'s NetworkManager.Shutdown despawns it on the way
    /// back to the main menu.
    public class NetLobby : NetworkBehaviour
    {
        public static NetLobby Instance { get; private set; }

        // Session cap is 2 (see NetSession.ApproveConnection) — surfaced
        // here purely for the "Players (n/2)" line and the CanStart gate.
        public const int MaxPlayers = 2;

        // The empty string is the "generate a fresh procedural map" choice;
        // anything else is a level name for LevelFileIO.Load. The client
        // only shows this — the actual world is built host-side (the client
        // gets it as the tile snapshot regardless of which map it is).
        public const string ProceduralMapId = "";

        public readonly NetworkList<LobbyPlayer> Players = new NetworkList<LobbyPlayer>();
        public readonly NetworkVariable<bool> GameStarted = new NetworkVariable<bool>();
        public readonly NetworkVariable<FixedString64Bytes> SelectedMap =
            new NetworkVariable<FixedString64Bytes>(new FixedString64Bytes("level1"));

        /// Host only — GameBootstrap wires this to the real world build.
        public Action OnHostBuildGame;

        public override void OnNetworkSpawn()
        {
            Instance = this;

            if (IsServer)
            {
                AddPlayer(NetworkManager.LocalClientId);
                NetworkManager.OnClientConnectedCallback += AddPlayer;
                NetworkManager.OnClientDisconnectCallback += RemovePlayer;
            }
            else
            {
                // Symmetric with NetGame.OnNetworkSpawn -> OnClientReady:
                // this is the client's "you're in the lobby now" signal.
                NetSession.Instance?.OnClientLobby?.Invoke();
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && NetworkManager != null)
            {
                NetworkManager.OnClientConnectedCallback -= AddPlayer;
                NetworkManager.OnClientDisconnectCallback -= RemovePlayer;
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ---- host roster upkeep ----

        private void AddPlayer(ulong clientId)
        {
            if (!IsServer || IndexOf(clientId) >= 0)
            {
                return;
            }

            Players.Add(new LobbyPlayer
            {
                ClientId = clientId,
                IsHost = clientId == NetworkManager.ServerClientId,
                Ready = false,
            });
        }

        private void RemovePlayer(ulong clientId)
        {
            if (!IsServer)
            {
                return;
            }

            var i = IndexOf(clientId);
            if (i >= 0)
            {
                Players.RemoveAt(i);
            }
        }

        private int IndexOf(ulong clientId)
        {
            for (int i = 0; i < Players.Count; i++)
            {
                if (Players[i].ClientId == clientId)
                {
                    return i;
                }
            }

            return -1;
        }

        // ---- ready toggle ----

        /// Called by the local player's own Lobby screen — applied straight
        /// on the host, or sent up to the server by a client.
        public void SetLocalReady(bool ready)
        {
            if (IsServer)
            {
                ApplyReady(NetworkManager.LocalClientId, ready);
            }
            else
            {
                SetReadyRpc(ready);
            }
        }

        [Rpc(SendTo.Server)]
        private void SetReadyRpc(bool ready, RpcParams p = default)
        {
            ApplyReady(p.Receive.SenderClientId, ready);
        }

        private void ApplyReady(ulong clientId, bool ready)
        {
            var i = IndexOf(clientId);
            if (i < 0)
            {
                return;
            }

            var slot = Players[i];
            if (slot.Ready != ready)
            {
                slot.Ready = ready;
                Players[i] = slot;
            }
        }

        public bool LocalReady
        {
            get
            {
                var i = IndexOf(NetworkManager.LocalClientId);
                return i >= 0 && Players[i].Ready;
            }
        }

        // ---- map selection (host only) ----

        public string MapId => SelectedMap.Value.ToString();

        /// Host only — from the Lobby screen's map picker. Changing the map
        /// clears everyone's ready flag: you readied up for a different one.
        public void HostSetMap(string mapId)
        {
            if (!IsServer || GameStarted.Value)
            {
                return;
            }

            var next = new FixedString64Bytes(mapId ?? ProceduralMapId);
            if (SelectedMap.Value.Equals(next))
            {
                return;
            }

            SelectedMap.Value = next;

            for (int i = 0; i < Players.Count; i++)
            {
                if (Players[i].Ready)
                {
                    var slot = Players[i];
                    slot.Ready = false;
                    Players[i] = slot;
                }
            }
        }

        // ---- start ----

        /// Host only — every connected player (the host included) is ready
        /// and there's at least one other player to be fair to.
        public bool CanStart
        {
            get
            {
                if (!IsServer || GameStarted.Value || Players.Count < 2)
                {
                    return false;
                }

                for (int i = 0; i < Players.Count; i++)
                {
                    if (!Players[i].Ready)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// Host only — the Lobby screen's Start button.
        public void HostStartGame()
        {
            if (!CanStart)
            {
                return;
            }

            GameStarted.Value = true;
            OnHostBuildGame?.Invoke();
        }
    }
}

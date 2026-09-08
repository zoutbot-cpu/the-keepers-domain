using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using KeepersDomain.Core;

namespace KeepersDomain.Net
{
    /// One row in the lobby roster — who's connected, their display name,
    /// and whether they've readied up. Still fully blittable (a
    /// FixedString32Bytes is an unmanaged fixed buffer, not a managed
    /// string); INetworkSerializeByMemcpy is the marker that makes NGO's
    /// codegen actually generate a memcpy serializer for it — without it
    /// NetworkList<LobbyPlayer> hits the FallbackSerializer and throws
    /// "Serialization has not been generated" mid connection-approval,
    /// dropping every joining client.
    public struct LobbyPlayer : INetworkSerializeByMemcpy, IEquatable<LobbyPlayer>
    {
        public ulong ClientId;
        public bool IsHost;
        public bool Ready;
        // Set from the sender's GameSettings.PlayerName — the host writes
        // its own in AddPlayer, a client's arrives by SubmitNameRpc.
        public FixedString32Bytes Name;

        public bool Equals(LobbyPlayer other) =>
            ClientId == other.ClientId && IsHost == other.IsHost
            && Ready == other.Ready && Name.Equals(other.Name);

        public override bool Equals(object obj) => obj is LobbyPlayer o && Equals(o);

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
        // anything else is a level name for LevelFileIO.Load.
        public const string ProceduralMapId = "";

        public readonly NetworkList<LobbyPlayer> Players = new NetworkList<LobbyPlayer>();
        public readonly NetworkVariable<bool> GameStarted = new NetworkVariable<bool>();

        /// Host only — GameBootstrap wires this to the real world build.
        public Action OnHostBuildGame;

        // Server-authoritative, replicated to clients by RPC rather than a
        // NetworkVariable<FixedString> (that serializer tripped the initial
        // behaviour sync). The client only shows this — the map is built
        // host-side and reaches the client as the tile snapshot regardless.
        private string _selectedMap = "level1";

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
                RequestMapRpc();
                SubmitNameRpc(GameSettings.SanitizeName(GameSettings.PlayerName));
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

        // Host: a client's name that landed (SubmitNameRpc) before its
        // roster row existed — applied when AddPlayer catches up.
        private readonly Dictionary<ulong, FixedString32Bytes> _pendingNames
            = new Dictionary<ulong, FixedString32Bytes>();

        private void AddPlayer(ulong clientId)
        {
            if (!IsServer || IndexOf(clientId) >= 0)
            {
                return;
            }

            var isHost = clientId == NetworkManager.ServerClientId;
            FixedString32Bytes name;
            if (isHost)
            {
                name = ToFixedName(GameSettings.SanitizeName(GameSettings.PlayerName));
            }
            else if (!_pendingNames.TryGetValue(clientId, out name))
            {
                name = default;
            }

            _pendingNames.Remove(clientId);

            Players.Add(new LobbyPlayer
            {
                ClientId = clientId,
                IsHost = isHost,
                Ready = false,
                Name = name,
            });
        }

        /// A string long enough to overflow FixedString32Bytes (29 bytes)
        /// throws on the implicit conversion — trim by UTF-8 byte length
        /// first. GameSettings.SanitizeName already caps at 20 chars, so
        /// this only ever bites on multi-byte names.
        private static FixedString32Bytes ToFixedName(string s)
        {
            s ??= string.Empty;
            while (s.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(s) > 29)
            {
                s = s.Substring(0, s.Length - 1);
            }

            FixedString32Bytes fs = default;
            fs.Append(s);
            return fs;
        }

        [Rpc(SendTo.Server)]
        private void SubmitNameRpc(string name, RpcParams p = default)
        {
            var clientId = p.Receive.SenderClientId;
            var clean = ToFixedName(GameSettings.SanitizeName(name));

            var i = IndexOf(clientId);
            if (i < 0)
            {
                // Row not built yet — AddPlayer will pick this up.
                _pendingNames[clientId] = clean;
                return;
            }

            var slot = Players[i];
            if (!slot.Name.Equals(clean))
            {
                slot.Name = clean;
                Players[i] = slot;
            }
        }

        private void RemovePlayer(ulong clientId)
        {
            if (!IsServer)
            {
                return;
            }

            _pendingNames.Remove(clientId);

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

        private void ClearAllReady()
        {
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

        // ---- map selection (host only) ----

        public string MapId => _selectedMap ?? "level1";

        /// Host only — from the Lobby screen's map picker. Changing the map
        /// clears everyone's ready flag: you readied up for a different one.
        public void HostSetMap(string mapId)
        {
            if (!IsServer || GameStarted.Value)
            {
                return;
            }

            var next = mapId ?? ProceduralMapId;
            if (_selectedMap == next)
            {
                return;
            }

            _selectedMap = next;
            ClearAllReady();
            SyncMapToClientsRpc(next);
        }

        /// Client just spawned in — ask the host which map is picked.
        [Rpc(SendTo.Server)]
        private void RequestMapRpc(RpcParams p = default)
        {
            SendMapRpc(_selectedMap ?? "level1",
                RpcTarget.Single(p.Receive.SenderClientId, RpcTargetUse.Temp));
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void SendMapRpc(string mapId, RpcParams p)
        {
            _selectedMap = mapId ?? "level1";
        }

        [Rpc(SendTo.NotServer)]
        private void SyncMapToClientsRpc(string mapId)
        {
            _selectedMap = mapId ?? "level1";
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

using System;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;

namespace KeepersDomain.Net
{
    /// Owns the whole networking lifecycle: the long-lived NetworkManager,
    /// Unity Gaming Services init + anonymous sign-in, and creating /
    /// joining a Relay-backed session (2 players, join code). GameBootstrap
    /// creates one in Init(); the Main Menu drives Host / Join through it.
    /// Offline "Start Game" never touches any of this — the NetworkManager
    /// just sits idle.
    ///
    /// Milestone 1a: connection + grid spectator sync only.
    public class NetSession : MonoBehaviour
    {
        public static NetSession Instance { get; private set; }

        public enum Phase { Idle, Connecting, Hosting, Client, Failed }

        public Phase State { get; private set; } = Phase.Idle;
        public string JoinCode { get; private set; }
        public string LastError { get; private set; }
        public bool IsNetworked => State == Phase.Hosting || State == Phase.Client;

        /// GameBootstrap wires these: what to run once the host's
        /// authoritative world / the client's render-only world should be
        /// built. OnHostReady fires here; OnClientReady is invoked from
        /// NetGame.OnNetworkSpawn (client) once the session controller has
        /// replicated in.
        public Action OnHostReady;
        public Action OnClientReady;
        public Action OnDisconnected;

        private NetworkManager _nm;
        private ISession _session;
        private static bool _servicesInit;

        public static void Create()
        {
            if (Instance != null)
            {
                return;
            }

            var go = new GameObject("NetSession");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<NetSession>();
            Instance.BuildNetworkManager();
        }

        private void BuildNetworkManager()
        {
            // NetworkManager must live on a root GameObject (NGO forbids it
            // being nested) — put it on THIS object, which Create() made a
            // root + DontDestroyOnLoad.
            var utp = gameObject.AddComponent<UnityTransport>();
            _nm = gameObject.AddComponent<NetworkManager>();

            _nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = utp,
                ConnectionApproval = true,
                EnableSceneManagement = false,
                TickRate = 30,
            };

            // The networked prefabs GameBootstrap spawns at runtime. Built
            // by Tools > Net > Setup Netcode Prefabs.
            RegisterPrefab("Net/NetGame");
            RegisterPrefab("Net/CreatureNetView");
            RegisterPrefab("Net/KeeperNetState");
            RegisterPrefab("Net/SlimeNetView");

            _nm.ConnectionApprovalCallback = ApproveConnection;
            _nm.OnClientDisconnectCallback += HandleClientDisconnect;
        }

        private void RegisterPrefab(string resourcePath)
        {
            var prefab = Resources.Load<GameObject>(resourcePath);
            if (prefab != null)
            {
                _nm.AddNetworkPrefab(prefab);
            }
            else
            {
                Debug.LogError($"NetSession: Resources/{resourcePath} prefab missing — run Tools > Net > Setup Netcode Prefabs.");
            }
        }

        private void ApproveConnection(NetworkManager.ConnectionApprovalRequest req,
            NetworkManager.ConnectionApprovalResponse resp)
        {
            // Host + one client. The host connects through this too.
            resp.Approved = _nm.ConnectedClientsIds.Count < 2;
            resp.CreatePlayerObject = false;
            if (!resp.Approved)
            {
                resp.Reason = "Game is full (2 players).";
            }
        }

        // ---- Host ----

        public async void StartHost()
        {
            if (State == Phase.Connecting || State == Phase.Hosting)
            {
                return;
            }

            State = Phase.Connecting;
            try
            {
                await EnsureSignedIn();
                var options = new SessionOptions { MaxPlayers = 2 }.WithRelayNetwork();
                _session = await MultiplayerService.Instance.CreateSessionAsync(options);
                JoinCode = _session.Code;

                if (!_nm.IsListening)
                {
                    _nm.StartHost();
                }

                State = Phase.Hosting;
                OnHostReady?.Invoke();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // ---- Client ----

        public async void JoinByCode(string code)
        {
            if (State == Phase.Connecting || State == Phase.Client || string.IsNullOrWhiteSpace(code))
            {
                return;
            }

            State = Phase.Connecting;
            try
            {
                await EnsureSignedIn();
                _session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim());

                if (!_nm.IsListening)
                {
                    _nm.StartClient();
                }

                State = Phase.Client;
                // OnClientReady is invoked from NetGame.OnNetworkSpawn.
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private async Task EnsureSignedIn()
        {
            if (!_servicesInit)
            {
                await UnityServices.InitializeAsync();
                _servicesInit = true;
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
        }

        private void Fail(Exception e)
        {
            LastError = e.Message;
            State = Phase.Failed;
            Debug.LogException(e);
        }

        private void HandleClientDisconnect(ulong clientId)
        {
            if (_nm == null)
            {
                return;
            }

            // Client losing the host, or host losing its one client.
            if (!_nm.IsServer && clientId == _nm.LocalClientId)
            {
                Debug.LogWarning("NetSession: disconnected from host.");
                OnDisconnected?.Invoke();
            }
        }

        public async void Leave()
        {
            try
            {
                if (_session != null)
                {
                    await _session.LeaveAsync();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            _session = null;
            if (_nm != null && _nm.IsListening)
            {
                _nm.Shutdown();
            }

            State = Phase.Idle;
            JoinCode = null;
        }
    }
}

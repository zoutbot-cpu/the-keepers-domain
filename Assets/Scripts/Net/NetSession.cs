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
        /// built. OnHostReady fires here the moment the transport is up (the
        /// host then shows the lobby, not the game); OnClientLobby is
        /// invoked from NetLobby.OnNetworkSpawn (client) once the lobby
        /// object has replicated in; OnClientReady is invoked from
        /// NetGame.OnNetworkSpawn (client) once the host has actually
        /// started the game.
        public Action OnHostReady;
        public Action OnClientLobby;
        public Action OnClientReady;
        public Action OnDisconnected;

        private NetworkManager _nm;
        private ISession _session;
        private static bool _servicesInit;

        // A leave/cleanup that's still finishing in the background. The next
        // Host/Join awaits it (EnsureIdle) before starting — the Multiplayer
        // SDK owns the NetworkManager's start/stop with a Relay session, and
        // a fresh start that races a still-completing shutdown makes it throw
        // "Failed to start the network manager".
        private Task _pendingLeave;

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
            RegisterPrefab("Net/NetLobby");
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
                await EnsureIdle();
                await EnsureSignedIn();
                // WithRelayNetwork() hands the NetworkManager's lifecycle to
                // the Multiplayer SDK — it calls StartHost itself inside
                // CreateSessionAsync, so we must not.
                var options = new SessionOptions { MaxPlayers = 2 }.WithRelayNetwork();
                _session = await MultiplayerService.Instance.CreateSessionAsync(options);
                JoinCode = _session.Code;

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
                await EnsureIdle();
                await EnsureSignedIn();
                // The SDK starts the client itself inside this call (see the
                // StartHost note) — we must not call _nm.StartClient.
                _session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim());

                State = Phase.Client;
                // OnClientReady is invoked from NetGame.OnNetworkSpawn.
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// Await any in-flight leave and make sure the NetworkManager is
        /// fully stopped before the SDK is asked to start it again.
        private async Task EnsureIdle()
        {
            if (_pendingLeave != null)
            {
                try
                {
                    await _pendingLeave;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                _pendingLeave = null;
            }

            var deadline = Time.realtimeSinceStartup + 5f;
            while (_nm != null && (_nm.IsListening || _nm.ShutdownInProgress)
                   && Time.realtimeSinceStartup < deadline)
            {
                await Task.Delay(50);
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

            // Tear the half-started session/transport down so the next
            // attempt (EnsureIdle) begins from a clean slate rather than
            // inheriting a stuck NetworkManager.
            _pendingLeave = TeardownAsync();
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

        public void Leave()
        {
            // This is an INTENTIONAL leave -- drop the disconnect handler so
            // the shutdown below doesn't fire OnClientDisconnectCallback ->
            // OnDisconnected -> a second teardown that wipes the fresh main
            // menu (GameBootstrap re-arms OnDisconnected on the next Host/
            // Join). Shut the transport down synchronously and up front so
            // no NetworkObject is still "spawned" while GameBootstrap
            // destroys the scene roots; the Relay/Lobby session leave + the
            // shutdown settling finish in the background, and the next Host/
            // Join awaits that (_pendingLeave, EnsureIdle).
            OnDisconnected = null;

            if (_nm != null && _nm.IsListening && !_nm.ShutdownInProgress)
            {
                _nm.Shutdown();
            }

            State = Phase.Idle;
            JoinCode = null;

            _pendingLeave = TeardownAsync();
        }

        private async Task TeardownAsync()
        {
            if (_nm != null && _nm.IsListening && !_nm.ShutdownInProgress)
            {
                _nm.Shutdown();
            }

            // Let NGO finish tearing down before anyone starts it again.
            var deadline = Time.realtimeSinceStartup + 5f;
            while (_nm != null && (_nm.IsListening || _nm.ShutdownInProgress)
                   && Time.realtimeSinceStartup < deadline)
            {
                await Task.Delay(50);
            }

            var session = _session;
            _session = null;
            try
            {
                if (session != null)
                {
                    await session.LeaveAsync();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}

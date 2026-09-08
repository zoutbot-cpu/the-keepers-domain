using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Core;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Net;

namespace KeepersDomain.UI
{
    /// The pre-game lobby, shown after Host Game / Join Game until the host
    /// starts. Lists every connected player and their ready state, lets the
    /// local player toggle their own Ready, and (host only) picks the map
    /// and exposes a Start Game button that unlocks once everyone is ready.
    /// Reads NetLobby.Instance for all state and destroys itself once the
    /// game starts (NetLobby.GameStarted) or the lobby goes away. Same
    /// procedural-IMGUI style as MainMenu — GameBootstrap creates it, no
    /// scene wiring.
    public class LobbyScreen : MonoBehaviour
    {
        private const float PanelWidth = 440f;
        private const float PanelHeight = 466f;
        private const float RowHeight = 26f;

        private bool _isHost;
        private Action _onLeave;

        // Host only — the maps the picker cycles through: the packaged
        // starting map, every save on disk, then "" (fresh procedural).
        private List<string> _maps;

        public void Initialize(bool isHost, Action onLeave)
        {
            _isHost = isHost;
            _onLeave = onLeave;

            if (isHost)
            {
                _maps = new List<string> { "level1" };
                foreach (var n in LevelFileIO.ListLevelNames())
                {
                    if (!_maps.Contains(n))
                    {
                        _maps.Add(n);
                    }
                }

                _maps.Add(NetLobby.ProceduralMapId);
            }
        }

        private void Update()
        {
            var net = NetSession.Instance;
            if (net != null && net.State == NetSession.Phase.Failed)
            {
                _onLeave?.Invoke();
                return;
            }

            var lobby = NetLobby.Instance;
            if (lobby == null || lobby.GameStarted.Value)
            {
                // Game is starting (host builds the world now; the client
                // world follows off NetGame's spawn) or the lobby was torn
                // down — either way this screen is done.
                Destroy(gameObject);
            }
        }

        private void OnGUI()
        {
            var net = NetSession.Instance;
            var lobby = NetLobby.Instance;

            var x = (Screen.width - PanelWidth) * 0.5f;
            var y = (Screen.height - PanelHeight) * 0.5f;
            GUI.Box(new Rect(x, y, PanelWidth, PanelHeight), GUIContent.none);

            var pad = x + 24f;
            var w = PanelWidth - 48f;
            var cursor = y + 18f;

            var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(pad, cursor, w, 30f), "Lobby", titleStyle);
            cursor += 40f;

            if (_isHost)
            {
                GUI.Label(new Rect(pad, cursor, 78f, 22f), "Join code:");
                var codeStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
                GUI.Label(new Rect(pad + 80f, cursor - 2f, w - 80f, 24f),
                    net != null ? (net.JoinCode ?? "...") : "...", codeStyle);
                cursor += 32f;
            }

            if (lobby == null)
            {
                GUI.Label(new Rect(pad, cursor, w, 22f), "Connecting...");
                DrawLeave(pad, y + PanelHeight - 46f, w);
                return;
            }

            // ---- map ----
            var currentMap = lobby.MapId;
            if (_isHost && _maps != null)
            {
                GUI.Label(new Rect(pad, cursor, 42f, 24f), "Map:");
                if (GUI.Button(new Rect(pad + 44f, cursor, 26f, 24f), "<"))
                {
                    lobby.HostSetMap(StepMap(currentMap, -1));
                }

                var mapStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                GUI.Label(new Rect(pad + 74f, cursor, w - 74f - 30f, 24f), MapLabel(currentMap), mapStyle);

                if (GUI.Button(new Rect(pad + w - 26f, cursor, 26f, 24f), ">"))
                {
                    lobby.HostSetMap(StepMap(currentMap, +1));
                }
                cursor += 30f;
            }
            else
            {
                GUI.Label(new Rect(pad, cursor, w, 22f), $"Map:  {MapLabel(currentMap)}");
                cursor += 26f;
            }

            // ---- players ----
            GUI.Label(new Rect(pad, cursor, w, 20f), $"Players ({lobby.Players.Count}/{NetLobby.MaxPlayers})");
            cursor += 24f;

            for (int i = 0; i < lobby.Players.Count; i++)
            {
                var p = lobby.Players[i];
                var name = p.IsHost ? $"Player {i + 1}  (host)" : $"Player {i + 1}";
                if (p.ClientId == lobby.NetworkManager.LocalClientId)
                {
                    name += "  — you";
                }

                GUI.Label(new Rect(pad, cursor, w - 110f, RowHeight), name);

                var readyStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
                readyStyle.normal.textColor = p.Ready
                    ? new Color(0.40f, 0.85f, 0.40f)
                    : new Color(0.85f, 0.70f, 0.32f);
                GUI.Label(new Rect(pad + w - 110f, cursor, 110f, RowHeight), p.Ready ? "READY" : "not ready", readyStyle);
                cursor += RowHeight;
            }

            cursor += 14f;

            var localReady = lobby.LocalReady;
            if (GUI.Button(new Rect(pad, cursor, w, 40f), localReady ? "Cancel Ready" : "Ready Up"))
            {
                lobby.SetLocalReady(!localReady);
            }
            cursor += 50f;

            var centered = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };

            if (_isHost)
            {
                GUI.enabled = lobby.CanStart;
                if (GUI.Button(new Rect(pad, cursor, w, 40f), "Start Game"))
                {
                    lobby.HostStartGame();
                }
                GUI.enabled = true;
                cursor += 44f;

                if (!lobby.CanStart)
                {
                    var hint = lobby.Players.Count < NetLobby.MaxPlayers
                        ? "Waiting for another player to join…"
                        : "Waiting for all players to ready up…";
                    GUI.Label(new Rect(pad, cursor, w, 20f), hint, centered);
                }
            }
            else
            {
                GUI.Label(new Rect(pad, cursor, w, 20f),
                    lobby.GameStarted.Value ? "Starting…" : "Waiting for the host to start…", centered);
            }

            DrawLeave(pad, y + PanelHeight - 46f, w);
        }

        private string StepMap(string currentId, int dir)
        {
            var i = _maps.IndexOf(currentId);
            if (i < 0)
            {
                i = 0;
            }

            i = (i + dir + _maps.Count) % _maps.Count;
            return _maps[i];
        }

        private static string MapLabel(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "Fresh procedural map";
            }

            return id == GameBootstrap.SaveGameSlot ? "Resume saved game" : id;
        }

        private void DrawLeave(float x, float y, float w)
        {
            if (GUI.Button(new Rect(x, y, w, 32f), "Leave"))
            {
                _onLeave?.Invoke();
            }
        }
    }
}

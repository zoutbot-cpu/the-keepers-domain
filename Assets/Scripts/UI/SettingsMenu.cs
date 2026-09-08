using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using KeepersDomain.Core;

namespace KeepersDomain.UI
{
    /// The main-menu Settings screen — a modal IMGUI panel opened by
    /// MainMenu's "Settings" button and closed with Back. Three tabs:
    ///
    ///   Display  — fullscreen mode + resolution (GameSettings.ApplyDisplay)
    ///   Player   — the display name multiplayer chat shows
    ///   Controls — rebind the four camera-orbit keys, plus a read-only
    ///              reference for the mouse / fixed controls
    ///
    /// All state lives in GameSettings (PlayerPrefs-backed); this class only
    /// draws it and forwards edits. Same procedural, no-scene-wiring style
    /// as MainMenu / LobbyScreen — MainMenu new's it up and destroys it.
    public class SettingsMenu : MonoBehaviour
    {
        /// MainMenu hides its own buttons while this is up.
        public static bool IsOpen { get; private set; }

        private enum Tab { Display, Player, Controls }

        private const float PanelWidth = 560f;
        private const float PanelHeight = 460f;

        private Tab _tab = Tab.Display;
        private string _nameDraft;
        private GameSettings.OrbitAction? _awaitingRebind;

        // De-duplicated (width, height) list off Screen.resolutions, plus
        // the player's current pick tracked as an index into it.
        private readonly List<Vector2Int> _resolutions = new List<Vector2Int>();
        private int _resIndex;

        private void OnEnable()
        {
            IsOpen = true;
        }

        private void OnDisable()
        {
            IsOpen = false;
        }

        private void Awake()
        {
            _nameDraft = GameSettings.PlayerName;
            BuildResolutionList();
        }

        private void BuildResolutionList()
        {
            _resolutions.Clear();
            foreach (var r in Screen.resolutions)
            {
                var entry = new Vector2Int(r.width, r.height);
                if (!_resolutions.Contains(entry))
                {
                    _resolutions.Add(entry);
                }
            }

            var current = new Vector2Int(
                GameSettings.ResolutionWidth > 0 ? GameSettings.ResolutionWidth : Screen.width,
                GameSettings.ResolutionHeight > 0 ? GameSettings.ResolutionHeight : Screen.height);

            if (!_resolutions.Contains(current))
            {
                _resolutions.Add(current);
            }

            _resIndex = Mathf.Max(0, _resolutions.IndexOf(current));
        }

        private void Update()
        {
            if (_awaitingRebind == null || Keyboard.current == null)
            {
                return;
            }

            foreach (var key in Keyboard.current.allKeys)
            {
                if (!key.wasPressedThisFrame)
                {
                    continue;
                }

                // Esc cancels the capture; anything else becomes the binding.
                if (key.keyCode != Key.Escape)
                {
                    GameSettings.SetOrbitKey(_awaitingRebind.Value, key.keyCode);
                }

                _awaitingRebind = null;
                break;
            }
        }

        private void OnGUI()
        {
            // Dim the menu behind the panel.
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = prev;

            var x = (Screen.width - PanelWidth) * 0.5f;
            var y = (Screen.height - PanelHeight) * 0.5f;
            GUI.Box(new Rect(x, y, PanelWidth, PanelHeight), GUIContent.none);

            var pad = x + 24f;
            var w = PanelWidth - 48f;
            var cursor = y + 18f;

            var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(pad, cursor, w, 30f), "Settings", titleStyle);
            cursor += 42f;

            // ---- tab strip ----
            var tabW = w / 3f;
            DrawTab(new Rect(pad, cursor, tabW, 28f), Tab.Display, "Display");
            DrawTab(new Rect(pad + tabW, cursor, tabW, 28f), Tab.Player, "Player");
            DrawTab(new Rect(pad + tabW * 2f, cursor, tabW, 28f), Tab.Controls, "Controls");
            cursor += 44f;

            var body = new Rect(pad, cursor, w, y + PanelHeight - 64f - cursor);
            switch (_tab)
            {
                case Tab.Display: DrawDisplay(body); break;
                case Tab.Player: DrawPlayer(body); break;
                case Tab.Controls: DrawControls(body); break;
            }

            if (GUI.Button(new Rect(pad, y + PanelHeight - 46f, w, 32f), "Back"))
            {
                Close();
            }
        }

        private void DrawTab(Rect r, Tab tab, string label)
        {
            var on = _tab == tab;
            if (GUI.Toggle(r, on, label, GUI.skin.button) && !on)
            {
                _tab = tab;
                _awaitingRebind = null;
            }
        }

        // ---- Display ----

        private void DrawDisplay(Rect body)
        {
            var row = body.y;

            GUI.Label(new Rect(body.x, row, body.width, 22f), "Window mode");
            row += 26f;

            var thirds = body.width / 3f;
            DrawModeButton(new Rect(body.x, row, thirds - 4f, 30f),
                FullScreenMode.ExclusiveFullScreen, "Fullscreen");
            DrawModeButton(new Rect(body.x + thirds, row, thirds - 4f, 30f),
                FullScreenMode.FullScreenWindow, "Borderless");
            DrawModeButton(new Rect(body.x + thirds * 2f, row, thirds - 4f, 30f),
                FullScreenMode.Windowed, "Windowed");
            row += 46f;

            GUI.Label(new Rect(body.x, row, body.width, 22f), "Resolution");
            row += 26f;

            if (GUI.Button(new Rect(body.x, row, 34f, 28f), "<"))
            {
                StepResolution(-1);
            }

            var res = _resolutions.Count > 0 ? _resolutions[_resIndex] : new Vector2Int(Screen.width, Screen.height);
            var resStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(body.x + 40f, row, body.width - 80f, 28f), $"{res.x} x {res.y}", resStyle);

            if (GUI.Button(new Rect(body.x + body.width - 34f, row, 34f, 28f), ">"))
            {
                StepResolution(1);
            }

            row += 40f;
            GUI.Label(new Rect(body.x, row, body.width, 40f),
                "Fullscreen and Borderless use this resolution; Windowed opens a window this size.",
                new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 });
        }

        private void DrawModeButton(Rect r, FullScreenMode mode, string label)
        {
            var on = GameSettings.DisplayMode == mode;
            if (GUI.Toggle(r, on, label, GUI.skin.button) && !on)
            {
                GameSettings.SetDisplayMode(mode);
            }
        }

        private void StepResolution(int dir)
        {
            if (_resolutions.Count == 0)
            {
                return;
            }

            _resIndex = (_resIndex + dir + _resolutions.Count) % _resolutions.Count;
            var r = _resolutions[_resIndex];
            GameSettings.SetResolution(r.x, r.y);
        }

        // ---- Player ----

        private void DrawPlayer(Rect body)
        {
            var row = body.y;
            GUI.Label(new Rect(body.x, row, body.width, 22f), "Display name");
            row += 26f;

            GUI.SetNextControlName("SettingsPlayerName");
            _nameDraft = GUI.TextField(new Rect(body.x, row, body.width, 28f),
                _nameDraft ?? "", GameSettings.MaxPlayerNameLength);
            row += 36f;

            GUI.Label(new Rect(body.x, row, body.width, 40f),
                "Shown to the other player in multiplayer chat. Saved when you press Back.",
                new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 });
        }

        // ---- Controls ----

        private void DrawControls(Rect body)
        {
            var row = body.y;

            GUI.Label(new Rect(body.x, row, body.width, 22f), "Camera orbit");
            row += 26f;

            row = DrawRebindRow(body.x, row, body.width, "Orbit up", GameSettings.OrbitAction.Up);
            row = DrawRebindRow(body.x, row, body.width, "Orbit down", GameSettings.OrbitAction.Down);
            row = DrawRebindRow(body.x, row, body.width, "Orbit left", GameSettings.OrbitAction.Left);
            row = DrawRebindRow(body.x, row, body.width, "Orbit right", GameSettings.OrbitAction.Right);

            row += 4f;
            if (GUI.Button(new Rect(body.x, row, 180f, 26f), "Reset to defaults"))
            {
                GameSettings.ResetOrbitKeys();
                _awaitingRebind = null;
            }

            row += 38f;
            GUI.Label(new Rect(body.x, row, body.width, 100f),
                "Also available:\n" +
                "  • Q / D / Z / S  — orbit (fixed, AZERTY-friendly)\n" +
                "  • Right-drag  — pan    • Mouse wheel  — zoom\n" +
                "  • Left-click  — select / dig    • Shift  — square-area mode\n" +
                "  • Enter  — multiplayer chat",
                new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 });
        }

        private float DrawRebindRow(float x, float y, float w, string label, GameSettings.OrbitAction action)
        {
            GUI.Label(new Rect(x, y, w * 0.4f, 26f), label);

            var awaiting = _awaitingRebind == action;
            var keyLabel = awaiting ? "press a key…" : GameSettings.GetOrbitKey(action).ToString();
            if (GUI.Button(new Rect(x + w * 0.42f, y, w * 0.58f, 26f), keyLabel))
            {
                _awaitingRebind = awaiting ? (GameSettings.OrbitAction?)null : action;
            }

            return y + 30f;
        }

        // ---- close ----

        private void Close()
        {
            GameSettings.SetPlayerName(_nameDraft);
            _awaitingRebind = null;
            Destroy(gameObject);
        }
    }
}

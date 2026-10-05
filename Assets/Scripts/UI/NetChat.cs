using UnityEngine;
using KeepersDomain.Net;

namespace KeepersDomain.UI
{
    /// The in-match chat overlay — created for host and client alongside
    /// NetHud / NetPauseScreen (WorldBuilder.BuildHostGame /
    /// BuildClientWorld). Bottom-left, just above the menu bar: a short
    /// rolling transcript that fades out when idle, plus a one-line input
    /// box that opens on Enter (or the bottom bar's Chat button) and sends
    /// on Enter, closes on Esc.
    ///
    /// While the box is focused NetChat.IsTyping is true so the camera
    /// orbit keys (IsoCameraController) and the debug player-switcher
    /// digits (LocalPlayerController) don't fire as the player types; while
    /// the pointer is over the open panel NetChat.PointerOverPanel is true
    /// so a click there doesn't also dig a tile (TileInteractionController).
    ///
    /// Reads only NetGame.Instance; it goes dormant the moment that
    /// despawns and is destroyed with the scene on ReturnToMainMenu like
    /// the rest of the net HUD.
    public class NetChat : MonoBehaviour
    {
        /// True while the local player has the chat box focused.
        public static bool IsTyping { get; private set; }

        /// True while the pointer is over the open chat panel.
        public static bool PointerOverPanel { get; private set; }

        private const float PanelWidth = 400f;
        private const float RowHeight = 18f;
        private const int MaxVisibleRows = 7;
        private const float BottomBarHeight = 44f;   // BottomMenuBar.BarHeight
        private const float Margin = 10f;
        private const float InputHeight = 24f;
        // Transcript stays up this long after the last line / after the
        // input closes, then fades over the final FadeLength seconds.
        private const float IdleHideDelay = 12f;
        private const float FadeLength = 2f;

        private const string InputControlName = "NetChatInput";

        private NetGame _net;
        private bool _open;
        private string _draft = string.Empty;
        private float _lastActivityTime = -999f;
        private bool _focusQueued;
        private Vector2 _scroll;
        private GUIStyle _rowStyle;

        private void Update()
        {
            if (_net == null)
            {
                var game = NetGame.Instance;
                if (game != null)
                {
                    _net = game;
                    _net.ChatLineReceived += OnChatLineReceived;
                    _lastActivityTime = Time.unscaledTime;
                }

                return;
            }

            // NetGame despawned (leaving the match) — go dormant so a stale
            // reference can't keep the typing/pointer guards latched on.
            if (NetGame.Instance == null)
            {
                _net.ChatLineReceived -= OnChatLineReceived;
                _net = null;
                _open = false;
                _draft = string.Empty;
                IsTyping = false;
                PointerOverPanel = false;
            }
        }

        private void OnDestroy()
        {
            if (_net != null)
            {
                _net.ChatLineReceived -= OnChatLineReceived;
            }

            IsTyping = false;
            PointerOverPanel = false;
        }

        private void OnChatLineReceived(NetGame.ChatLine line)
        {
            _lastActivityTime = Time.unscaledTime;
            _scroll.y = float.MaxValue;
        }

        /// The bottom bar's Chat button routes here.
        public void OpenInput()
        {
            _open = true;
            _focusQueued = true;
            _lastActivityTime = Time.unscaledTime;
        }

        private void OnGUI()
        {
            if (_net == null)
            {
                IsTyping = false;
                PointerOverPanel = false;
                return;
            }

            // Draw above NetPauseScreen so players can still talk while the
            // game is paused (chat RPCs are unaffected by Time.timeScale).
            var prevDepth = GUI.depth;
            GUI.depth = -50;

            EnsureStyles();
            var e = Event.current;

            HandleKeys(e);

            var now = Time.unscaledTime;
            var idleAge = now - _lastActivityTime;
            var showTranscript = _open || idleAge < IdleHideDelay;

            var panelHeight = MaxVisibleRows * RowHeight + 10f;
            var stackBottom = Screen.height - BottomBarHeight - Margin;
            var inputRect = new Rect(Margin, stackBottom - InputHeight, PanelWidth, InputHeight);
            var panelBottom = _open ? inputRect.y - 4f : stackBottom;
            var panelRect = new Rect(Margin, panelBottom - panelHeight, PanelWidth, panelHeight);

            PointerOverPanel = _open &&
                (panelRect.Contains(e.mousePosition) || inputRect.Contains(e.mousePosition));

            if (showTranscript && _net.ChatLog.Count > 0)
            {
                DrawTranscript(panelRect, _open ? 1f : Mathf.Clamp01(1f - (idleAge - (IdleHideDelay - FadeLength)) / FadeLength));
            }

            if (_open)
            {
                DrawInput(inputRect, e);
            }

            IsTyping = _open && GUI.GetNameOfFocusedControl() == InputControlName;
            GUI.depth = prevDepth;
        }

        private void HandleKeys(Event e)
        {
            if (e.type != EventType.KeyDown)
            {
                return;
            }

            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                if (!_open)
                {
                    OpenInput();
                }
                else
                {
                    Submit();
                }

                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape && _open)
            {
                CloseInput();
                e.Use();
            }
        }

        private void DrawTranscript(Rect panelRect, float alpha)
        {
            var prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Lerp(0.25f, 1f, alpha));

            GUI.Box(panelRect, GUIContent.none);

            var view = new Rect(panelRect.x + 6f, panelRect.y + 5f,
                panelRect.width - 12f, panelRect.height - 10f);
            var content = new Rect(0f, 0f, view.width - 16f, _net.ChatLog.Count * RowHeight);
            _scroll = GUI.BeginScrollView(view, _scroll, content, false, false);
            for (int i = 0; i < _net.ChatLog.Count; i++)
            {
                var line = _net.ChatLog[i];
                GUI.Label(new Rect(0f, i * RowHeight, content.width, RowHeight),
                    $"<b>{_net.ChatAuthorLabel(line)}:</b> {line.Text}", _rowStyle);
            }

            GUI.EndScrollView();
            GUI.color = prevColor;
        }

        private void DrawInput(Rect inputRect, Event e)
        {
            GUI.SetNextControlName(InputControlName);
            _draft = GUI.TextField(inputRect, _draft, 240);

            if (_focusQueued && e.type == EventType.Repaint)
            {
                GUI.FocusControl(InputControlName);
                _focusQueued = false;
            }
        }

        private void Submit()
        {
            var text = _draft;
            _draft = string.Empty;
            _lastActivityTime = Time.unscaledTime;

            if (!string.IsNullOrWhiteSpace(text))
            {
                _net.SendChatMessage(text);
            }

            CloseInput();
        }

        private void CloseInput()
        {
            _open = false;
            _focusQueued = false;
            IsTyping = false;
            GUI.FocusControl(null);
        }

        private void EnsureStyles()
        {
            if (_rowStyle != null)
            {
                return;
            }

            _rowStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = 12,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
        }
    }
}

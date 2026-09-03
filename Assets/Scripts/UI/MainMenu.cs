using System;
using UnityEngine;
using KeepersDomain.Net;

namespace KeepersDomain.UI
{
    /// The very first screen shown on launch — logo plus Start / Level
    /// Designer / Host / Join / Quit, gating GameBootstrap's world build
    /// until the player picks one (see GameBootstrap.ShowMainMenu).
    public class MainMenu : MonoBehaviour
    {
        private const float LogoWidth = 480f;
        private const float LogoHeight = 240f;
        private const float ButtonWidth = 220f;
        private const float ButtonHeight = 48f;
        private const float ButtonSpacing = 12f;

        private Action _onStart;
        private Action _onLevelDesigner;
        private Action _onHost;
        private Action<string> _onJoin;
        private Texture2D _logo;

        private string _joinCodeInput = "";

        public void Initialize(Action onStart, Action onLevelDesigner, Action onHost, Action<string> onJoin)
        {
            _onStart = onStart;
            _onLevelDesigner = onLevelDesigner;
            _onHost = onHost;
            _onJoin = onJoin;
            // Resources.Load, not a serialized field — every other object in
            // this prototype is created procedurally by GameBootstrap rather
            // than wired up in the Inspector. Assets/Resources/UI/logo.png.
            _logo = Resources.Load<Texture2D>("UI/logo");
        }

        private void OnGUI()
        {
            var net = NetSession.Instance;

            // Connection succeeded — the game/client world is being built;
            // this menu's job is done.
            if (net != null && (net.State == NetSession.Phase.Hosting || net.State == NetSession.Phase.Client))
            {
                Destroy(gameObject);
                return;
            }

            var centerX = Screen.width * 0.5f;

            if (_logo != null)
            {
                GUI.DrawTexture(new Rect(centerX - LogoWidth * 0.5f, Screen.height * 0.18f, LogoWidth, LogoHeight),
                    _logo, ScaleMode.ScaleToFit);
            }
            else
            {
                var style = new GUIStyle(GUI.skin.label) { fontSize = 32, alignment = TextAnchor.MiddleCenter };
                GUI.Label(new Rect(centerX - LogoWidth * 0.5f, Screen.height * 0.26f, LogoWidth, 60f),
                    "The Keeper's Domain", style);
            }

            var connecting = net != null && net.State == NetSession.Phase.Connecting;
            GUI.enabled = !connecting;

            var y = Screen.height * 0.5f;
            float Row() { var r = y; y += ButtonHeight + ButtonSpacing; return r; }
            Rect Btn(float rowY) => new Rect(centerX - ButtonWidth * 0.5f, rowY, ButtonWidth, ButtonHeight);

            if (GUI.Button(Btn(Row()), "Start Game (offline)"))
            {
                _onStart?.Invoke();
                Destroy(gameObject);
                return;
            }

            if (GUI.Button(Btn(Row()), "Level Designer"))
            {
                _onLevelDesigner?.Invoke();
                Destroy(gameObject);
                return;
            }

            if (GUI.Button(Btn(Row()), "Host Game"))
            {
                _onHost?.Invoke();
            }

            var joinRowY = Row();
            _joinCodeInput = GUI.TextField(
                new Rect(centerX - ButtonWidth * 0.5f, joinRowY, ButtonWidth - 90f, ButtonHeight), _joinCodeInput, 12);
            if (GUI.Button(new Rect(centerX - ButtonWidth * 0.5f + ButtonWidth - 84f, joinRowY, 84f, ButtonHeight), "Join"))
            {
                _onJoin?.Invoke(_joinCodeInput);
            }

            if (GUI.Button(Btn(Row()), "Quit"))
            {
                Application.Quit();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
            }

            GUI.enabled = true;

            var status = connecting ? "Connecting..."
                : net != null && net.State == NetSession.Phase.Failed ? $"Failed: {net.LastError}"
                : "";
            if (status.Length > 0)
            {
                var s = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                GUI.Label(new Rect(centerX - 300f, y + 8f, 600f, 44f), status, s);
            }
        }
    }
}

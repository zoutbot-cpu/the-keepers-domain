using UnityEngine;
using KeepersDomain.Core;

namespace KeepersDomain.UI
{
    /// The match-over overlay — shown when a Throne Room hits 0 HP (see
    /// ThroneRoom.Defeated / GameBootstrap.HandleThroneDefeated, and
    /// NetGame.MatchOverRpc on a networked client). A dimmed full-screen
    /// panel with a VICTORY / DEFEAT banner and a single "Main Menu" button
    /// that tears the game down through GameBootstrap.ReturnToMainMenu.
    /// The simulation keeps running behind it — this is a result screen, not
    /// a pause; nothing here needs Time.timeScale.
    public class EndScreen : MonoBehaviour
    {
        private static EndScreen _current;

        private bool _victory;
        private string _subtitle;

        /// Puts the overlay up (replacing any that's already showing). Safe
        /// to call from anywhere — creates its own GameObject.
        public static void Show(bool victory, string subtitle)
        {
            if (_current != null)
            {
                Destroy(_current.gameObject);
            }

            var go = new GameObject("EndScreen");
            _current = go.AddComponent<EndScreen>();
            _current._victory = victory;
            _current._subtitle = subtitle;
        }

        private void OnDestroy()
        {
            if (_current == this)
            {
                _current = null;
            }
        }

        private void OnGUI()
        {
            // Dim the whole screen.
            var dim = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = dim;

            const float panelWidth = 380f;
            const float panelHeight = 180f;
            var panel = new Rect((Screen.width - panelWidth) * 0.5f, (Screen.height - panelHeight) * 0.5f,
                panelWidth, panelHeight);
            GUI.Box(panel, GUIContent.none);

            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 32,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            titleStyle.normal.textColor = _victory ? new Color(0.4f, 0.9f, 0.45f) : new Color(0.95f, 0.35f, 0.35f);
            GUI.Label(new Rect(panel.x, panel.y + 18f, panel.width, 44f), _victory ? "VICTORY" : "DEFEAT", titleStyle);

            var subStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Label(new Rect(panel.x + 20f, panel.y + 70f, panel.width - 40f, 44f), _subtitle ?? "", subStyle);

            if (GUI.Button(new Rect(panel.x + panel.width * 0.5f - 70f, panel.y + panel.height - 46f, 140f, 32f), "Main Menu"))
            {
                GameBootstrap.ReturnToMainMenu();
            }
        }
    }
}

using UnityEngine;
using KeepersDomain.Core;
using KeepersDomain.Net;

namespace KeepersDomain.UI
{
    /// The multiplayer pause overlay. Created once per networked session
    /// (host and client) alongside NetHud; draws nothing until
    /// NetGame.Instance.Paused goes true, then a dimmed panel with:
    ///   - "Resume" — unpauses for everyone (NetGame.ToggleLocalPause).
    ///   - "Vote to Save & Quit (n/m)" — toggles this player's vote; once
    ///     every player has voted the host saves and everyone exits to the
    ///     menu (NetGame.ToggleLocalSaveQuitVote / TryResolveSaveQuit).
    /// The Pause button that opens it lives on the bottom bar
    /// (BottomMenuBar.DrawBar), shown only while NetGame.Instance != null.
    public class NetPauseScreen : MonoBehaviour
    {
        private void OnGUI()
        {
            var net = NetGame.Instance;
            if (net == null || !net.Paused.Value)
            {
                return;
            }

            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = prev;

            const float w = 380f;
            const float h = 254f;
            var panel = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(panel, GUIContent.none);

            var title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 30,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            GUI.Label(new Rect(panel.x, panel.y + 16f, panel.width, 40f), "PAUSED", title);

            var sub = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Label(new Rect(panel.x + 20f, panel.y + 60f, panel.width - 40f, 40f),
                "The game is paused for both players.", sub);

            if (GUI.Button(new Rect(panel.x + panel.width * 0.5f - 150f, panel.y + 108f, 300f, 34f), "Resume"))
            {
                net.ToggleLocalPause();
            }

            var voted = net.LocalVotedSaveQuit;
            var label = voted
                ? $"Waiting for the other player... ({net.SaveQuitVoteCount}/{net.PlayerCount.Value}) — cancel vote"
                : $"Vote to Save & Quit ({net.SaveQuitVoteCount}/{net.PlayerCount.Value})";
            if (GUI.Button(new Rect(panel.x + panel.width * 0.5f - 150f, panel.y + 150f, 300f, 34f), label))
            {
                net.ToggleLocalSaveQuitVote();
            }

            if (GUI.Button(new Rect(panel.x + panel.width * 0.5f - 150f, panel.y + 192f, 300f, 34f), "Leave (no save)"))
            {
                GameBootstrap.ReturnToMainMenu();
            }
        }
    }
}

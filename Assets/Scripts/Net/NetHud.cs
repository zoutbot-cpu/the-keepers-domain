using UnityEngine;

namespace KeepersDomain.Net
{
    /// Tiny corner overlay for a networked session — the host's join code
    /// to share, or the client's connection state. Created by GameBootstrap
    /// (OnHostReady / BuildClientWorld). Milestone 1a placeholder; folds
    /// into the real HUD later.
    public class NetHud : MonoBehaviour
    {
        private bool _isHost;

        public void Initialize(bool isHost)
        {
            _isHost = isHost;
        }

        private void OnGUI()
        {
            var net = NetSession.Instance;
            if (net == null)
            {
                return;
            }

            var box = new Rect(10f, 10f, 260f, _isHost ? 54f : 34f);
            GUI.Box(box, GUIContent.none);

            if (_isHost)
            {
                GUI.Label(new Rect(box.x + 8f, box.y + 6f, box.width - 16f, 20f), "Hosting — share this join code:");
                var codeStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
                GUI.Label(new Rect(box.x + 8f, box.y + 26f, box.width - 16f, 24f), net.JoinCode ?? "...", codeStyle);
            }
            else
            {
                var text = net.State == NetSession.Phase.Client ? "Connected to host" : net.State.ToString();
                GUI.Label(new Rect(box.x + 8f, box.y + 8f, box.width - 16f, 20f), text);
            }
        }
    }
}

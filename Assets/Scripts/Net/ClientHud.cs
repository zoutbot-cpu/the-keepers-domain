using UnityEngine;

namespace KeepersDomain.Net
{
    /// The joined client's minimal HUD (Milestone 1b) — its own keeper's
    /// gold / mana / bacon / throne HP, read straight off the replicated
    /// KeeperNetState. No tools yet; commands come in M1c. Created by
    /// GameBootstrap.BuildClientWorld.
    public class ClientHud : MonoBehaviour
    {
        // M1: the host is keeper 0, the one client is keeper 1. Proper
        // per-client assignment is M2.
        private const int LocalOwnerId = 1;

        private void OnGUI()
        {
            var s = KeeperNetState.ForOwner(LocalOwnerId);
            var bar = new Rect(10f, Screen.height - 34f, Screen.width - 20f, 26f);
            GUI.Box(bar, GUIContent.none);

            var text = s == null
                ? $"Player {LocalOwnerId + 1} — waiting for keeper state..."
                : $"Player {LocalOwnerId + 1}   Gold {s.Gold.Value}   Mana {s.Mana.Value}/{s.MaxMana.Value}   Bacon {s.Bacon.Value}   Throne {s.ThroneHp.Value}/{s.ThroneMaxHp.Value}";

            GUI.Label(new Rect(bar.x + 8f, bar.y + 4f, bar.width - 16f, 20f), text);
        }
    }
}

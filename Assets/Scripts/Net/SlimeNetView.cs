using Unity.Netcode;
using UnityEngine;

namespace KeepersDomain.Net
{
    /// One networked bred slime (SlimeHatcheryManager.SpawnSlime). The
    /// prefab (Resources/Net/SlimeNetView) is a sphere pre-scaled/tinted to
    /// match SlimeAgent's own look (see NetPrefabSetup) + NetworkObject +
    /// NetworkTransform (position only) + this. Unlike CreatureNetView a
    /// slime has no per-instance identity — no species, owner, HP — so
    /// there's nothing to put in a netvar; NetworkTransform alone keeps a
    /// client's ghost wandering in step with the host's real SlimeAgent.
    /// This component exists purely so CreateHostBody has something to
    /// instantiate/Spawn and the client gets a normal networked object.
    public class SlimeNetView : NetworkBehaviour
    {
        /// True on the host of a running networked game — SlimeHatcheryManager
        /// takes the networked path only then; offline and on the client,
        /// false (mirrors CreatureNetView.HostActive).
        public static bool HostActive =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        /// Host path — instantiate the prefab and Spawn it. SlimeHatcheryManager
        /// then adds the real SlimeAgent on top (same "prefab body + host
        /// agent" split CreatureNetView uses) and Initialize sets the
        /// actual spawn position, so groundPos here just avoids a one-frame
        /// pop at the world origin before that runs.
        public static GameObject CreateHostBody(Vector3 groundPos)
        {
            var go = Object.Instantiate(Resources.Load<GameObject>("Net/SlimeNetView"));
            go.transform.position = groundPos;
            go.GetComponent<NetworkObject>().Spawn();
            return go;
        }
    }
}

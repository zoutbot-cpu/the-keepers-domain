using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using KeepersDomain.Net;

namespace KeepersDomain.EditorTools
{
    /// One-time setup: builds the network prefabs GameBootstrap loads at
    /// runtime (Resources/Net/...). Same "a Tools menu item generates the
    /// asset from code" pattern as DungeonPack > Setup Props. Re-run after
    /// a fresh clone if the prefabs are missing (NetSession logs an error
    /// pointing here).
    public static class NetPrefabSetup
    {
        private const string Dir = "Assets/Resources/Net";

        [MenuItem("Tools/Net/Setup Netcode Prefabs")]
        public static void Setup()
        {
            Directory.CreateDirectory(Dir);

            // The session-lifetime controller — no transform sync (it never
            // moves), just a NetworkObject so the host can spawn it and the
            // client gets the OnNetworkSpawn signal.
            Build("NetGame", go => go.AddComponent<NetGame>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Netcode prefabs written to {Dir}");
        }

        private static void Build(string name, System.Action<GameObject> addComponents)
        {
            var go = new GameObject(name);
            go.AddComponent<NetworkObject>();
            addComponents(go);

            PrefabUtility.SaveAsPrefabAsset(go, $"{Dir}/{name}.prefab");
            Object.DestroyImmediate(go);
        }
    }
}

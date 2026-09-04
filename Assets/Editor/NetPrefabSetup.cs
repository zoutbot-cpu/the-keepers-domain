using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;
using KeepersDomain.Net;

namespace KeepersDomain.EditorTools
{
    /// One-time setup: builds the network prefabs GameBootstrap loads at
    /// runtime (Resources/Net/...). Same "a Tools menu item generates the
    /// asset from code" pattern as DungeonPack > Setup Props. Re-run after
    /// a fresh clone if the prefabs are missing (NetSession logs an error
    /// pointing here). Safe to re-run — overwrites in place.
    public static class NetPrefabSetup
    {
        private const string Dir = "Assets/Resources/Net";

        [MenuItem("Tools/Net/Setup Netcode Prefabs")]
        public static void Setup()
        {
            Directory.CreateDirectory(Dir);

            // Session-lifetime controller — no transform, just a
            // NetworkObject so the host can spawn it and the client gets
            // the OnNetworkSpawn signal.
            Build("NetGame", empty: true, go => go.AddComponent<NetGame>());

            // Per-keeper economy mirror — same shape.
            Build("KeeperNetState", empty: true, go => go.AddComponent<KeeperNetState>());

            // Networked creature — a capsule the host spawns and the client
            // renders. NetworkTransform syncs position/rotation only; the
            // client sets scale/colour from the replicated species netvar.
            Build("CreatureNetView", empty: false, go =>
            {
                var nt = go.AddComponent<NetworkTransform>();
                nt.SyncScaleX = false;
                nt.SyncScaleY = false;
                nt.SyncScaleZ = false;
                go.AddComponent<CreatureNetView>();
            });

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Netcode prefabs written to {Dir}");
        }

        private static void Build(string name, bool empty, System.Action<GameObject> addComponents)
        {
            var go = empty
                ? new GameObject(name)
                : GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;

            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                Object.DestroyImmediate(col);
            }

            go.AddComponent<NetworkObject>();
            addComponents(go);

            PrefabUtility.SaveAsPrefabAsset(go, $"{Dir}/{name}.prefab");
            Object.DestroyImmediate(go);
        }
    }
}

using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;
using KeepersDomain.Net;

namespace KeepersDomain.EditorTools
{
    /// One-time setup: builds the runtime assets GameBootstrap / Prims load
    /// from Resources (the network prefabs + the shared URP material every
    /// procedural primitive tints). Same "a Tools menu item generates the
    /// asset from code" pattern as DungeonPack > Setup Props. Re-run after a
    /// fresh clone if things are magenta or NetSession logs a missing
    /// prefab. Safe to re-run — overwrites in place.
    public static class NetPrefabSetup
    {
        private const string Dir = "Assets/Resources/Net";
        private const string SharedDir = "Assets/Resources/Shared";

        [MenuItem("Tools/Net/Setup Netcode Prefabs")]
        public static void Setup()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(SharedDir);

            // The URP material Prims.Tint clones for every CreatePrimitive
            // primitive — a real asset so its shader variants ship in the
            // player build (a runtime `new Material(Shader.Find(...))` is
            // magenta in a build).
            var primMatPath = $"{SharedDir}/M_Prim.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(primMatPath) == null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "M_Prim" };
                AssetDatabase.CreateAsset(mat, primMatPath);
            }

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

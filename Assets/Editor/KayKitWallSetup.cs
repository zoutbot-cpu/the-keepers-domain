using System.IO;
using UnityEditor;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.EditorTools
{
    /// One-time batch-mode setup for the imported KayKit wall meshes: builds
    /// the shared material, wraps each source mesh into an autotile-shape
    /// prefab under Assets/Prefabs/Walls, and writes the WallMeshCatalog
    /// data asset DungeonGrid loads at runtime (Resources/Dungeon). Not
    /// needed again once those assets exist — run via
    /// `Unity -batchmode -quit -executeMethod KeepersDomain.EditorTools.KayKitWallSetup.Run`.
    public static class KayKitWallSetup
    {
        private const string ModelsDir = "Assets/Art/KayKit/Dungeon/Models";
        private const string TextureDir = "Assets/Art/KayKit/Dungeon/Textures";
        private const string MaterialsDir = "Assets/Art/KayKit/Dungeon/Materials";
        private const string PrefabsDir = "Assets/Prefabs/Walls";
        private const string ResourcesDir = "Assets/Resources/Dungeon";

        private static readonly string[] SourceMeshNames = { "wall", "wall_corner", "wall_Tsplit", "wall_crossing", "wall_endcap" };

        [MenuItem("Tools/KayKit/Setup Dungeon Walls")]
        public static void Run()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            RestoreDefaultAxisConversion();

            var material = BuildMaterial();

            var straight = BuildWallPrefab("wall", "Wall_Straight", material, out var footprint);
            var corner = BuildWallPrefab("wall_corner", "Wall_Corner", material, out _);
            var tJunction = BuildWallPrefab("wall_Tsplit", "Wall_TJunction", material, out _);
            var cross = BuildWallPrefab("wall_crossing", "Wall_Cross", material, out _);
            var endCap = BuildWallPrefab("wall_endcap", "Wall_EndCap", material, out _);
            // Isolated (0 neighbors) is rare enough to just reuse the
            // straight piece rather than needing its own source mesh.
            var isolated = BuildWallPrefab("wall", "Wall_Isolated", material, out _);

            // KayKit dungeon modules are commonly authored on a 2-unit
            // grid; DungeonGrid's cells are 1 unit, so scale = 1 / footprint
            // brings the mesh's raw footprint down to fit one cell. Logged
            // so this can be sanity-checked against the actual imported
            // bounds rather than assumed.
            var scale = footprint > 0.01f ? 1f / footprint : 0.5f;
            Debug.Log($"[KayKitWallSetup] wall.fbx raw footprint (X/Z bounds) = {footprint:F3} -> prefab scale = {scale:F4}");

            BuildCatalog(isolated, endCap, straight, corner, tJunction, cross, scale);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[KayKitWallSetup] Done.");
        }

        /// These source meshes carry a real root-level rotation of their
        /// own (observed ~90 degrees about X on wall.fbx, sign-flips if
        /// bakeAxisConversion is toggled — not something to fight via
        /// import settings). DungeonGrid.RefreshVisual now composes its
        /// autotile Y-rotation on top of each prefab's own authored
        /// localRotation instead of overwriting it, so this just makes
        /// sure the import setting is left at Unity's default (false) —
        /// a stray true from an earlier attempt would otherwise persist.
        private static void RestoreDefaultAxisConversion()
        {
            foreach (var meshName in SourceMeshNames)
            {
                var path = $"{ModelsDir}/{meshName}.fbx";
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                {
                    Debug.LogError($"[KayKitWallSetup] Could not get ModelImporter for {path}");
                    continue;
                }

                if (importer.bakeAxisConversion)
                {
                    importer.bakeAxisConversion = false;
                    importer.SaveAndReimport();
                }
            }
        }

        private static Material BuildMaterial()
        {
            Directory.CreateDirectory(MaterialsDir);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/dungeon_texture.png");
            if (texture == null)
            {
                Debug.LogError($"[KayKitWallSetup] Could not load texture at {TextureDir}/dungeon_texture.png");
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[KayKitWallSetup] Could not find URP/Lit shader — is URP installed/active?");
            }

            var path = $"{MaterialsDir}/M_DungeonWalls.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            material.SetTexture("_BaseMap", texture);
            material.SetFloat("_Smoothness", 0.15f);
            // These source meshes' front-facing normals don't reliably
            // line up with "outward from the wall" once DungeonGrid spins
            // them per-tile (their authored orientation isn't a simple
            // world-up-facing default — see DungeonGrid.RefreshVisual's
            // rotation composition) — back-face culling was silently
            // hiding whole walls depending on rotation. Double-sided is a
            // trivial cost for this low-poly a pack and removes the
            // failure mode entirely rather than chasing exact winding per
            // shape/rotation.
            material.SetFloat("_Cull", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// Instantiates sourceMeshName.fbx, points every renderer at
        /// sharedMaterial, and saves the result as a prefab under
        /// Assets/Prefabs/Walls/prefabName.prefab. footprint is the raw
        /// (unscaled) max(X,Z) render-bounds size of the source mesh, only
        /// meaningfully reported for the "wall" straight piece (the others
        /// pass it through unused) — good enough to derive one shared scale
        /// factor for every piece, since KayKit's modules all share one
        /// grid unit.
        private static GameObject BuildWallPrefab(string sourceMeshName, string prefabName, Material sharedMaterial, out float footprint)
        {
            Directory.CreateDirectory(PrefabsDir);
            var sourcePath = $"{ModelsDir}/{sourceMeshName}.fbx";
            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            footprint = 0f;
            if (sourceAsset == null)
            {
                Debug.LogError($"[KayKitWallSetup] Could not load source mesh at {sourcePath}");
                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourceAsset);
            var renderers = instance.GetComponentsInChildren<MeshRenderer>();
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    materials[i] = sharedMaterial;
                }

                renderer.sharedMaterials = materials;
            }

            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }

                footprint = Mathf.Max(bounds.size.x, bounds.size.z);
            }

            var prefabPath = $"{PrefabsDir}/{prefabName}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            Object.DestroyImmediate(instance);
            return prefab;
        }

        private static void BuildCatalog(GameObject isolated, GameObject endCap, GameObject straight,
            GameObject corner, GameObject tJunction, GameObject cross, float scale)
        {
            Directory.CreateDirectory(ResourcesDir);
            var path = $"{ResourcesDir}/WallMeshCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<WallMeshCatalog>(path);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<WallMeshCatalog>();
                AssetDatabase.CreateAsset(catalog, path);
            }

            var so = new SerializedObject(catalog);
            so.FindProperty("_isolated").objectReferenceValue = isolated;
            so.FindProperty("_endCap").objectReferenceValue = endCap;
            so.FindProperty("_straight").objectReferenceValue = straight;
            so.FindProperty("_corner").objectReferenceValue = corner;
            so.FindProperty("_tJunction").objectReferenceValue = tJunction;
            so.FindProperty("_cross").objectReferenceValue = cross;
            so.FindProperty("_prefabScale").floatValue = scale;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
        }
    }
}

using System.IO;
using UnityEditor;
using UnityEngine;

namespace KeepersDomain.EditorTools
{
    /// One-time setup for the bespoke dungeon_pack's water/lava tiles
    /// (Assets/Art/DungeonPack/Liquids/...) — a flat 1x1 quad each, exactly
    /// matching a grid cell's footprint natively (no scaling needed, unlike
    /// the wall blocks). Per the pack's own LIQUID_TILES_README.txt: water
    /// is alpha-blended, lava is opaque with an emissive crack map. Both
    /// get simple runtime animation (see LiquidAnimator) rather than the
    /// README's fancier dual-noise-scroll/UV-distortion shader technique —
    /// a deliberate simplification (see LiquidAnimator's own header) to
    /// keep this on stock URP/Lit rather than authoring a custom shader.
    public static class DungeonPackLiquidSetup
    {
        private const string WaterDir = "Assets/Art/DungeonPack/Liquids/Water";
        private const string LavaDir = "Assets/Art/DungeonPack/Liquids/Lava";
        private const string WaterMaterialPath = "Assets/Art/DungeonPack/Liquids/Water/M_Water.mat";
        private const string LavaMaterialPath = "Assets/Art/DungeonPack/Liquids/Lava/M_Lava.mat";
        private const string WaterPrefabPath = "Assets/Resources/Dungeon/Tile_Water.prefab";
        private const string LavaPrefabPath = "Assets/Resources/Dungeon/Tile_Lava.prefab";

        [MenuItem("Tools/DungeonPack/Setup Liquids")]
        public static void Run()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            FixNormalImport($"{WaterDir}/water_tile.obj");
            FixNormalImport($"{LavaDir}/lava_tile.obj");

            var waterMaterial = BuildWaterMaterial();
            var lavaMaterial = BuildLavaMaterial();

            BuildPrefab($"{WaterDir}/water_tile.obj", waterMaterial, WaterPrefabPath, "water_tile");
            BuildPrefab($"{LavaDir}/lava_tile.obj", lavaMaterial, LavaPrefabPath, "lava_tile");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[DungeonPackLiquidSetup] Done.");
        }

        /// Same issue every dungeon_pack .obj has (0 "vn" lines) — see
        /// DungeonPackWallSetup's own note. Without this, the mesh renders
        /// solid black under a Lit shader.
        private static void FixNormalImport(string objPath)
        {
            var importer = AssetImporter.GetAtPath(objPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[DungeonPackLiquidSetup] Could not get ModelImporter for {objPath}");
                return;
            }

            if (importer.importNormals != ModelImporterNormals.Calculate)
            {
                importer.importNormals = ModelImporterNormals.Calculate;
                importer.SaveAndReimport();
            }
        }

        private static Material BuildWaterMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{WaterDir}/water_tile_diffuse.png");
            var material = LoadOrCreateMaterial(WaterMaterialPath, shader);

            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", 0.75f);
            material.SetFloat("_Cull", 0f);

            // Alpha-blended per the pack's README (water_base.png ships a
            // real alpha channel) — this is the standard URP/Lit
            // "Transparent, Alpha blend mode" property set the Surface
            // Options GUI would otherwise configure for you.
            material.SetFloat("_Surface", 1f); // Transparent
            material.SetFloat("_Blend", 0f); // Alpha
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material BuildLavaMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{LavaDir}/lava_tile_diffuse.png");
            var emissiveTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{LavaDir}/lava_emissive.png");
            var material = LoadOrCreateMaterial(LavaMaterialPath, shader);

            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", 0.2f);
            material.SetFloat("_Cull", 0f);
            material.SetFloat("_Surface", 0f); // Opaque

            material.SetTexture("_EmissionMap", emissiveTexture);
            material.SetColor("_EmissionColor", Color.white);
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material LoadOrCreateMaterial(string path, Shader shader)
        {
            if (shader == null)
            {
                Debug.LogError("[DungeonPackLiquidSetup] Could not find URP/Lit shader — is URP installed/active?");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            return material;
        }

        private static void BuildPrefab(string sourcePath, Material material, string prefabPath, string debugLabel)
        {
            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (sourceAsset == null)
            {
                Debug.LogError($"[DungeonPackLiquidSetup] Could not load source mesh at {sourcePath}");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourceAsset);
            var renderers = instance.GetComponentsInChildren<MeshRenderer>();
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    materials[i] = material;
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

                Debug.Log($"[DungeonPackLiquidSetup] {debugLabel} bounds — size: {bounds.size}, center Y: {bounds.center.y:F3}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath));
            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            Object.DestroyImmediate(instance);
        }
    }
}

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KeepersDomain.EditorTools
{
    /// One-time setup for the bespoke dungeon_pack wall meshes (Assets/Art/
    /// DungeonPack/Walls/...) — builds a URP material per source .mtl slot
    /// (with emission for the gold/mana-crystal veins' glow maps), wraps
    /// each source mesh into a prefab, and drops it under Resources so
    /// DungeonGrid can load it with no scene wiring (it's built entirely
    /// procedurally by GameBootstrap). No per-shape catalog/autotiling
    /// here: every variant is one plain wall block, no corner/T-junction
    /// pieces exist in this pack (unlike the earlier, abandoned KayKit
    /// attempt).
    public static class DungeonPackWallSetup
    {
        /// One material this variant's mesh needs — SourceMaterialName
        /// must match the name Unity's OBJ importer gives the
        /// auto-generated material for that slot, which (with
        /// materialName: 0 in the .obj.meta) is exactly the .mtl's
        /// "newmtl" identifier (material_0, material_1, ...) — used to
        /// map each renderer's default material to the right replacement
        /// when a mesh has more than one (see BuildPrefab).
        private class MaterialSpec
        {
            public string SourceMaterialName;
            public string DiffuseName;
            public string EmissiveName; // null if this slot has no glow map
            public string MaterialPath;
        }

        private class WallVariant
        {
            public string SourceDir;
            public string ObjName;
            public string PrefabPath;
            public MaterialSpec[] Materials;
        }

        private static readonly WallVariant[] Variants =
        {
            SingleMaterial(
                sourceDir: "Assets/Art/DungeonPack/Walls",
                objName: "dungeon_wall_block",
                diffuseName: "dungeon_wall_block_diffuse",
                emissiveName: null,
                materialPath: "Assets/Art/DungeonPack/Walls/M_StoneWall.mat",
                prefabPath: "Assets/Resources/Dungeon/Wall_Stone.prefab"),
            SingleMaterial(
                sourceDir: "Assets/Art/DungeonPack/Walls/Gold",
                objName: "dungeon_wall_block_gold",
                diffuseName: "dungeon_wall_block_gold_diffuse",
                emissiveName: "dungeon_wall_block_gold_emissive",
                materialPath: "Assets/Art/DungeonPack/Walls/Gold/M_GoldWall.mat",
                prefabPath: "Assets/Resources/Dungeon/Wall_Gold.prefab"),
            SingleMaterial(
                sourceDir: "Assets/Art/DungeonPack/Walls/GoldRegen",
                objName: "dungeon_wall_block_gold_regen",
                diffuseName: "dungeon_wall_block_gold_regen_diffuse",
                emissiveName: "dungeon_wall_block_gold_regen_emissive",
                materialPath: "Assets/Art/DungeonPack/Walls/GoldRegen/M_GoldRegenWall.mat",
                prefabPath: "Assets/Resources/Dungeon/Wall_GoldRegen.prefab"),
            SingleMaterial(
                sourceDir: "Assets/Art/DungeonPack/Walls/ManaCrystal",
                objName: "dungeon_wall_block_mana_crystal",
                diffuseName: "dungeon_wall_block_mana_crystal_diffuse",
                emissiveName: "dungeon_wall_block_mana_crystal_emissive",
                materialPath: "Assets/Art/DungeonPack/Walls/ManaCrystal/M_ManaCrystalWall.mat",
                prefabPath: "Assets/Resources/Dungeon/Wall_ManaCrystal.prefab"),
            SingleMaterial(
                sourceDir: "Assets/Art/DungeonPack/Walls/Bedrock",
                objName: "dungeon_wall_block_bedrock",
                diffuseName: "dungeon_wall_block_bedrock_diffuse",
                emissiveName: null,
                materialPath: "Assets/Art/DungeonPack/Walls/Bedrock/M_BedrockWall.mat",
                prefabPath: "Assets/Resources/Dungeon/Wall_Bedrock.prefab"),
            new WallVariant
            {
                // Reinforced walls — the pack's only multi-material wall
                // mesh: grey brick body + a stone cap ring + a glowing
                // blue orb set into it. No dedicated "_emissive" file
                // ships for the orb, so its own diffuse doubles as the
                // emission map — it's already a bright, self-lit-looking
                // radial gradient, so that reads fine as a glow.
                SourceDir = "Assets/Art/DungeonPack/Walls/Claimed",
                ObjName = "dungeon_wall_claimed",
                PrefabPath = "Assets/Resources/Dungeon/Wall_Reinforced.prefab",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", DiffuseName = "claimed_wall_orb_blue", EmissiveName = "claimed_wall_orb_blue", MaterialPath = "Assets/Art/DungeonPack/Walls/Claimed/M_ReinforcedOrb.mat" },
                    new MaterialSpec { SourceMaterialName = "material_1", DiffuseName = "claimed_wall_grey_brick", EmissiveName = null, MaterialPath = "Assets/Art/DungeonPack/Walls/Claimed/M_ReinforcedBrick.mat" },
                    new MaterialSpec { SourceMaterialName = "material_2", DiffuseName = "claimed_wall_cap", EmissiveName = null, MaterialPath = "Assets/Art/DungeonPack/Walls/Claimed/M_ReinforcedCap.mat" },
                },
            },
        };

        private static WallVariant SingleMaterial(string sourceDir, string objName, string diffuseName,
            string emissiveName, string materialPath, string prefabPath)
        {
            return new WallVariant
            {
                SourceDir = sourceDir,
                ObjName = objName,
                PrefabPath = prefabPath,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", DiffuseName = diffuseName, EmissiveName = emissiveName, MaterialPath = materialPath },
                },
            };
        }

        [MenuItem("Tools/DungeonPack/Setup Walls")]
        public static void Run()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            foreach (var variant in Variants)
            {
                FixNormalImport(variant);
                var materials = BuildMaterials(variant);
                BuildPrefab(variant, materials);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[DungeonPackWallSetup] Done.");
        }

        /// None of this pack's .obj files carry vertex normals (0 "vn"
        /// lines, checked directly against the files) — Unity's default
        /// "Import" normal mode logs "Can't import normals, because mesh
        /// 'default' doesn't have any" and the resulting mesh has none,
        /// which renders solid black under a Lit shader (no valid normal
        /// for the lighting calculation to use). Switching to Calculate
        /// generates proper per-face/smoothed normals instead.
        private static void FixNormalImport(WallVariant variant)
        {
            var path = $"{variant.SourceDir}/{variant.ObjName}.obj";
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[DungeonPackWallSetup] Could not get ModelImporter for {path}");
                return;
            }

            if (importer.importNormals != ModelImporterNormals.Calculate)
            {
                importer.importNormals = ModelImporterNormals.Calculate;
                importer.SaveAndReimport();
            }
        }

        private static Dictionary<string, Material> BuildMaterials(WallVariant variant)
        {
            var result = new Dictionary<string, Material>();
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[DungeonPackWallSetup] Could not find URP/Lit shader — is URP installed/active?");
            }

            foreach (var spec in variant.Materials)
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{variant.SourceDir}/{spec.DiffuseName}.png");
                if (texture == null)
                {
                    Debug.LogError($"[DungeonPackWallSetup] Could not load texture at {variant.SourceDir}/{spec.DiffuseName}.png");
                }

                var material = AssetDatabase.LoadAssetAtPath<Material>(spec.MaterialPath);
                if (material == null)
                {
                    material = new Material(shader);
                    Directory.CreateDirectory(Path.GetDirectoryName(spec.MaterialPath));
                    AssetDatabase.CreateAsset(material, spec.MaterialPath);
                }
                else
                {
                    material.shader = shader;
                }

                material.SetTexture("_BaseMap", texture);
                // Explicit rather than trusting the shader's own default —
                // M_ReinforcedCap turned up with this at black (r:0,g:0,
                // b:0) despite nothing here ever setting it, likely a
                // stray Inspector edit — multiplying the base map by black
                // renders solid black regardless of lighting/normals.
                material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_Smoothness", 0.1f);
                // Double-sided from the start this time — the KayKit
                // attempt burned several rounds chasing a wall that was
                // only invisible because back-face culling silently
                // dropped it depending on orientation. Negligible cost
                // for one low-poly block.
                material.SetFloat("_Cull", 0f);

                if (spec.EmissiveName != null)
                {
                    var emissiveTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{variant.SourceDir}/{spec.EmissiveName}.png");
                    material.SetTexture("_EmissionMap", emissiveTexture);
                    material.SetColor("_EmissionColor", Color.white);
                    material.EnableKeyword("_EMISSION");
                    material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }

                EditorUtility.SetDirty(material);
                result[spec.SourceMaterialName] = material;
            }

            return result;
        }

        private static void BuildPrefab(WallVariant variant, Dictionary<string, Material> materialsByName)
        {
            var sourcePath = $"{variant.SourceDir}/{variant.ObjName}.obj";
            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (sourceAsset == null)
            {
                Debug.LogError($"[DungeonPackWallSetup] Could not load source mesh at {sourcePath}");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourceAsset);
            var renderers = instance.GetComponentsInChildren<MeshRenderer>();
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    // The source material's own name (e.g. "material_0")
                    // is how a multi-material mesh like the reinforced
                    // wall maps each renderer back to the right
                    // replacement — see MaterialSpec.SourceMaterialName.
                    var sourceName = materials[i] != null ? materials[i].name : null;
                    if (sourceName != null && materialsByName.TryGetValue(sourceName, out var replacement))
                    {
                        materials[i] = replacement;
                    }
                    else
                    {
                        Debug.LogWarning($"[DungeonPackWallSetup] {variant.ObjName}: no material mapped for slot '{sourceName}' on renderer '{renderer.name}'");
                    }
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

                Debug.Log($"[DungeonPackWallSetup] {variant.ObjName}.obj bounds — size: {bounds.size}, min Y: {bounds.min.y:F3}, renderers: {renderers.Length}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(variant.PrefabPath));
            PrefabUtility.SaveAsPrefabAsset(instance, variant.PrefabPath);
            Object.DestroyImmediate(instance);
        }
    }
}

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KeepersDomain.EditorTools
{
    /// One-time setup for the bespoke dungeon_pack's decorative props —
    /// landmark structures (Assets/Art/DungeonPack/Props/...) as well as
    /// per-room decoration meshes that live alongside their own room's
    /// other art instead (Assets/Art/DungeonPack/Lair/NestBed,
    /// .../TrainingRoom/TrainingDummy, .../Library/BookcaseModule,
    /// .../Tavern/BaconBeaconMachine, .../Tavern/InnBar). Unlike the wall/
    /// floor sets, these ship as flat per-slot Kd colors with no diffuse
    /// textures at all (checked directly against each .mtl), so this
    /// builds one solid-color URP material per slot instead of wiring up a
    /// texture. Wraps each source mesh into a prefab and drops it under
    /// Resources so the owning script can load it with no scene wiring —
    /// see ThroneRoom, LairManager, TrainingRoomManager, LibraryManager,
    /// TavernManager.
    public static class DungeonPackPropSetup
    {
        private class MaterialSpec
        {
            public string SourceMaterialName; // matches Unity's auto-named material for that .mtl slot (materialName: 0)
            public Color Color;
        }

        private class PropVariant
        {
            public string SourceDir;
            public string ObjName;
            public string PrefabPath;
            public string MaterialFolder;
            public MaterialSpec[] Materials;

            // Every dungeon_pack .obj so far ships with 0 "vn" lines and
            // needs normals recalculated on import (see FixNormalImport) —
            // but Portal.obj (hand-authored separately, its .mtl header
            // says "Blender 5.2.0 LTS", unlike the rest of the pack) does
            // carry real authored normals, so forcing Calculate would
            // discard them for no reason. Default false keeps every
            // existing entry's behavior unchanged.
            public bool HasAuthoredNormals;
        }

        private static readonly PropVariant[] Variants =
        {
            new PropVariant
            {
                // Kd values copied directly from throne_centerpiece.mtl —
                // dark stone body + red/gold accent colors, no texture.
                SourceDir = "Assets/Art/DungeonPack/Props/Throne",
                ObjName = "throne_centerpiece",
                PrefabPath = "Assets/Resources/Dungeon/Prop_Throne.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Props/Throne/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.22745098f, 0.22745098f, 0.25882353f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.15686275f, 0.15686275f, 0.18039216f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.72549020f, 0.10196078f, 0.12549020f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.88627451f, 0.73725490f, 0.37647059f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.50196078f, 0.06274510f, 0.07843137f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.76862745f, 0.61176471f, 0.23529412f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.86274510f, 0.27450980f, 0.07843137f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(1.00000000f, 0.82352941f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.28235294f, 0.28235294f, 0.32156863f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(0.03921569f, 0.03921569f, 0.03921569f) },
                },
            },
            new PropVariant
            {
                // The user's own new asset ("Portal.blend") replacing
                // portal_stairway — same 11-color palette (checked byte-
                // for-byte against portal_stairway.mtl, just reordered),
                // but ships real authored normals unlike the rest of the
                // pack (see HasAuthoredNormals). Re-exported once already
                // to fix a version that floated above the ground (its own
                // Y bounds now start near 0, pivot-at-base like the rest
                // of the pack) — re-copy from Downloads/dungeon_pack/
                // props/Portal.obj if it needs updating again.
                SourceDir = "Assets/Art/DungeonPack/Props/Portal",
                ObjName = "Portal",
                PrefabPath = "Assets/Resources/Dungeon/Prop_Portal.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Props/Portal/Materials",
                HasAuthoredNormals = true,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.227451f, 0.227451f, 0.258824f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.156863f, 0.156863f, 0.180392f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.725490f, 0.101961f, 0.125490f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.886275f, 0.737255f, 0.376471f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.501961f, 0.062745f, 0.078431f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.768627f, 0.611765f, 0.235294f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.862745f, 0.274510f, 0.078431f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(1.000000f, 0.823529f, 0.352941f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.588235f, 0.235294f, 0.823529f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(0.054902f, 0.031373f, 0.086275f) },
                    new MaterialSpec { SourceMaterialName = "material_10", Color = new Color(0.705882f, 0.352941f, 0.901961f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from nest_bed.mtl — a wood-
                // frame/tan bed with a dark red blanket, no texture. Placed
                // by LairManager on a claimed Lair tile, on top of the
                // room's own carpet floor (see CarpetTiles) — see
                // BuildClaimedVisual.
                SourceDir = "Assets/Art/DungeonPack/Lair/NestBed",
                ObjName = "nest_bed",
                PrefabPath = "Assets/Resources/Dungeon/Prop_NestBed.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Lair/NestBed/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.58823529f, 0.43921569f, 0.21176471f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.42352941f, 0.30588235f, 0.13333333f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.54901961f, 0.13333333f, 0.16470588f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.40784314f, 0.08627451f, 0.11764706f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from training_dummy.mtl — a
                // wooden post/crossbar frame with a pale straw torso and a
                // red target mark, no texture. Placed by TrainingRoomManager
                // at each computed dummy position — see BuildDummyVisual.
                SourceDir = "Assets/Art/DungeonPack/TrainingRoom/TrainingDummy",
                ObjName = "training_dummy",
                PrefabPath = "Assets/Resources/Dungeon/Prop_TrainingDummy.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/TrainingRoom/TrainingDummy/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.25882353f, 0.16470588f, 0.09411765f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.42352941f, 0.27450980f, 0.14901961f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.69019608f, 0.58039216f, 0.37647059f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.54901961f, 0.44705882f, 0.27450980f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.29019608f, 0.19607843f, 0.10196078f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.87058824f, 0.82352941f, 0.73725490f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.65882353f, 0.11764706f, 0.10980392f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from bookcase_module.mtl — a
                // dark-wood shelf frame packed with many individually
                // colored book spines (17 slots), no texture. A single
                // "module" (X/Z footprint well under one tile, see
                // LibraryManager's own BuildBookcaseModule note) meant to be
                // placed once per bookcase-row tile rather than stretched
                // across the row, unlike the old primitive box it replaces.
                SourceDir = "Assets/Art/DungeonPack/Library/BookcaseModule",
                ObjName = "bookcase_module",
                PrefabPath = "Assets/Resources/Dungeon/Prop_BookcaseModule.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Library/BookcaseModule/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.21176471f, 0.13333333f, 0.07843137f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.17254902f, 0.10980392f, 0.06274510f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.43921569f, 0.30588235f, 0.18039216f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.33725490f, 0.21960784f, 0.12549020f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.58823529f, 0.47058824f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.87058824f, 0.82352941f, 0.70588235f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.27450980f, 0.39215686f, 0.19607843f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(0.23529412f, 0.47058824f, 0.27450980f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.15686275f, 0.35294118f, 0.50980392f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(0.17647059f, 0.17647059f, 0.21568627f) },
                    new MaterialSpec { SourceMaterialName = "material_10", Color = new Color(0.47058824f, 0.15686275f, 0.31372549f) },
                    new MaterialSpec { SourceMaterialName = "material_11", Color = new Color(0.54901961f, 0.15686275f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_12", Color = new Color(0.35294118f, 0.23529412f, 0.50980392f) },
                    new MaterialSpec { SourceMaterialName = "material_13", Color = new Color(0.19607843f, 0.27450980f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_14", Color = new Color(0.66666667f, 0.35294118f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_15", Color = new Color(0.50980392f, 0.27450980f, 0.19607843f) },
                    new MaterialSpec { SourceMaterialName = "material_16", Color = new Color(0.62745098f, 0.58823529f, 0.35294118f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from bacon_beacon.mtl — a
                // grey/brown machine body with bright pipe/gauge accents
                // (green, orange, gold), no texture. Near-square footprint
                // (~1.93 x 1.91 unscaled) matching TavernManager's 2x2
                // shrine slot almost exactly — replaces the old primitive
                // dais+tubes there, see BuildShrineVisual.
                SourceDir = "Assets/Art/DungeonPack/Tavern/BaconBeaconMachine",
                ObjName = "bacon_beacon",
                PrefabPath = "Assets/Resources/Dungeon/Prop_BaconBeaconMachine.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Tavern/BaconBeaconMachine/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.22745098f, 0.22745098f, 0.25098039f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.58823529f, 0.36078431f, 0.20392157f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.37647059f, 0.38431373f, 0.41568627f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.58823529f, 0.82352941f, 0.74509804f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.35294118f, 0.74509804f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.54901961f, 0.55686275f, 0.58823529f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.50196078f, 0.23529412f, 0.17254902f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(0.36078431f, 0.15686275f, 0.11764706f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.90196078f, 0.43137255f, 0.11764706f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(1.00000000f, 0.82352941f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_10", Color = new Color(0.76862745f, 0.35294118f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_11", Color = new Color(0.90980392f, 0.82352941f, 0.74509804f) },
                    new MaterialSpec { SourceMaterialName = "material_12", Color = new Color(0.43137255f, 0.29019608f, 0.15686275f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from inn_bar.mtl — mostly
                // wood-tone (browns/tans) with a couple of grey/stone
                // accents, no texture. A long, thin bar-counter shape
                // (~2.69 x 0.93 unscaled — nothing like the near-square
                // shrine slot above), placed once per Tavern room along its
                // south edge rather than tied to any existing tile slot —
                // see TavernManager.BuildInnBar. Purely decorative, no
                // gameplay effect.
                SourceDir = "Assets/Art/DungeonPack/Tavern/InnBar",
                ObjName = "inn_bar",
                PrefabPath = "Assets/Resources/Dungeon/Prop_InnBar.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Tavern/InnBar/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.43921569f, 0.29019608f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.65882353f, 0.47843137f, 0.27450980f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.25882353f, 0.16470588f, 0.09411765f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.58823529f, 0.47058824f, 0.23529412f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.58823529f, 0.41568627f, 0.23529412f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.27450980f, 0.50980392f, 0.31372549f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.87058824f, 0.81568627f, 0.69019608f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(0.43137255f, 0.29019608f, 0.13333333f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.66666667f, 0.76470588f, 0.74509804f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(0.47058824f, 0.30588235f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_10", Color = new Color(0.27450980f, 0.27450980f, 0.29019608f) },
                    new MaterialSpec { SourceMaterialName = "material_11", Color = new Color(0.43137255f, 0.42352941f, 0.43921569f) },
                    new MaterialSpec { SourceMaterialName = "material_12", Color = new Color(0.92156863f, 0.88235294f, 0.78431373f) },
                },
            },
        };

        [MenuItem("Tools/DungeonPack/Setup Props")]
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
            Debug.Log("[DungeonPackPropSetup] Done.");
        }

        /// Same issue every dungeon_pack .obj has (0 "vn" lines) — see
        /// DungeonPackWallSetup's own note. Without this, the mesh renders
        /// solid black under a Lit shader. Skipped for variants that ship
        /// their own real normals — see HasAuthoredNormals.
        private static void FixNormalImport(PropVariant variant)
        {
            if (variant.HasAuthoredNormals)
            {
                return;
            }

            var path = $"{variant.SourceDir}/{variant.ObjName}.obj";
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[DungeonPackPropSetup] Could not get ModelImporter for {path}");
                return;
            }

            if (importer.importNormals != ModelImporterNormals.Calculate)
            {
                importer.importNormals = ModelImporterNormals.Calculate;
                importer.SaveAndReimport();
            }
        }

        private static Dictionary<string, Material> BuildMaterials(PropVariant variant)
        {
            var result = new Dictionary<string, Material>();
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[DungeonPackPropSetup] Could not find URP/Lit shader — is URP installed/active?");
            }

            Directory.CreateDirectory(variant.MaterialFolder);
            foreach (var spec in variant.Materials)
            {
                var path = $"{variant.MaterialFolder}/M_{variant.ObjName}_{spec.SourceMaterialName}.mat";
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

                material.SetColor("_BaseColor", spec.Color);
                material.SetFloat("_Smoothness", 0.15f);
                EditorUtility.SetDirty(material);
                result[spec.SourceMaterialName] = material;
            }

            return result;
        }

        private static void BuildPrefab(PropVariant variant, Dictionary<string, Material> materialsByName)
        {
            var sourcePath = $"{variant.SourceDir}/{variant.ObjName}.obj";
            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (sourceAsset == null)
            {
                Debug.LogError($"[DungeonPackPropSetup] Could not load source mesh at {sourcePath}");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourceAsset);
            var renderers = instance.GetComponentsInChildren<MeshRenderer>();
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var sourceName = materials[i] != null ? materials[i].name : null;
                    if (sourceName != null && materialsByName.TryGetValue(sourceName, out var replacement))
                    {
                        materials[i] = replacement;
                    }
                    else
                    {
                        Debug.LogWarning($"[DungeonPackPropSetup] {variant.ObjName}: no material mapped for slot '{sourceName}' on renderer '{renderer.name}'");
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

                Debug.Log($"[DungeonPackPropSetup] {variant.ObjName}.obj bounds — size: {bounds.size}, min Y: {bounds.min.y:F3}, renderers: {renderers.Length}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(variant.PrefabPath));
            PrefabUtility.SaveAsPrefabAsset(instance, variant.PrefabPath);
            Object.DestroyImmediate(instance);
        }
    }
}

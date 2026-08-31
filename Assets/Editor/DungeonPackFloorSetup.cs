using UnityEditor;
using UnityEngine;

namespace KeepersDomain.EditorTools
{
    /// One-time setup for the bespoke dungeon_pack floor textures
    /// (Assets/Resources/Dungeon/Floors) — plain PNGs, no meshes involved
    /// (DungeonGrid's floor tiles are still Unity's own primitive cube,
    /// just textured now instead of flat-colored — see DungeonGrid.
    /// RefreshVisual's "isPlainFloor" branch). Builds one material for the
    /// Unclaimed look (floor_dirt) and one for Claimed (claimed_tile_1,
    /// DungeonGrid swaps in the other 3 variants per-tile at runtime via
    /// MaterialPropertyBlock — see ApplyTint's baseMapOverride). Both live
    /// directly under Resources since DungeonGrid loads them by path with
    /// no scene wiring (it's built entirely procedurally by GameBootstrap).
    public static class DungeonPackFloorSetup
    {
        private const string SourceDir = "Assets/Resources/Dungeon/Floors";

        [MenuItem("Tools/DungeonPack/Setup Floors")]
        public static void Run()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            BuildMaterial("M_FloorUnclaimed", "floor_dirt");
            BuildMaterial("M_FloorClaimed", "claimed_tile_1");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[DungeonPackFloorSetup] Done.");
        }

        private static void BuildMaterial(string materialName, string diffuseName)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{SourceDir}/{diffuseName}.png");
            if (texture == null)
            {
                Debug.LogError($"[DungeonPackFloorSetup] Could not load texture at {SourceDir}/{diffuseName}.png");
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[DungeonPackFloorSetup] Could not find URP/Lit shader — is URP installed/active?");
            }

            var path = $"{SourceDir}/{materialName}.mat";
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
            material.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(material);
        }
    }
}

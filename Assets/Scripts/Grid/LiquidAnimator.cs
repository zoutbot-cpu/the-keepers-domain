using UnityEngine;

namespace KeepersDomain.Grid
{
    /// Drives the dungeon_pack water/lava tiles' "no simulation needed"
    /// animation — see Assets/Art/DungeonPack/Liquids' own
    /// LIQUID_TILES_README.txt for the pack author's full technique
    /// (dual-noise UV-distortion scroll for water, slow crust drift +
    /// per-tile-phase-hashed emissive pulse for lava). This is a
    /// deliberately simpler stand-in: a single scrolling base-map layer for
    /// water/lava (the README explicitly warns this alone reads as "a
    /// conveyor belt" rather than true flow, since it's missing the
    /// second, differently-scrolling noise layer) plus a uniform emissive
    /// pulse for lava's glowing cracks (every lava tile pulses in unison,
    /// not per-tile-phase-offset — the README's hash trick needs either a
    /// custom shader or per-instance materials, and every liquid tile
    /// intentionally shares one material, same as every other dungeon_pack
    /// tile type, for the same batching reason). water_flow.png is
    /// imported but unused for now — revisit if the conveyor-belt look
    /// isn't good enough.
    public class LiquidAnimator : MonoBehaviour
    {
        private const float WaterScrollSpeedX = 0.02f;
        private const float WaterScrollSpeedY = 0.01f;

        // "Try 1/10th water's speed" — LIQUID_TILES_README.txt.
        private const float LavaScrollSpeedX = 0.002f;
        private const float LavaScrollSpeedY = 0.001f;

        // Matches the README's own suggested pulse formula exactly
        // (0.8 + 0.2 * sin(time * 0.6)), just without the per-tile phase
        // hash — see this class's own header.
        private const float LavaPulseSpeed = 0.6f;
        private const float LavaPulseBase = 0.8f;
        private const float LavaPulseAmplitude = 0.2f;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private Material _waterMaterial;
        private Material _lavaMaterial;

        /// Loads the same Tile_Water/Tile_Lava prefabs DungeonGrid itself
        /// loads (Resources.Load returns the same cached asset either
        /// way, so this needs no reference/coupling to DungeonGrid at
        /// all) purely to reach the one shared material every instance of
        /// that tile renders with (found via the prefab's own renderer,
        /// same pattern DungeonGrid.FindReinforcedOrbTemplate uses).
        public void Initialize()
        {
            _waterMaterial = FindMaterial(Resources.Load<GameObject>("Dungeon/Tile_Water"));
            _lavaMaterial = FindMaterial(Resources.Load<GameObject>("Dungeon/Tile_Lava"));
        }

        private static Material FindMaterial(GameObject prefab)
        {
            if (prefab == null)
            {
                return null;
            }

            var renderer = prefab.GetComponentInChildren<Renderer>();
            return renderer != null ? renderer.sharedMaterial : null;
        }

        private void Update()
        {
            if (_waterMaterial != null)
            {
                var offset = new Vector2(Time.time * WaterScrollSpeedX, Time.time * WaterScrollSpeedY);
                _waterMaterial.SetTextureOffset(BaseMapId, offset);
            }

            if (_lavaMaterial != null)
            {
                var offset = new Vector2(Time.time * LavaScrollSpeedX, Time.time * LavaScrollSpeedY);
                _lavaMaterial.SetTextureOffset(BaseMapId, offset);

                var pulse = LavaPulseBase + LavaPulseAmplitude * Mathf.Sin(Time.time * LavaPulseSpeed);
                _lavaMaterial.SetColor(EmissionColorId, Color.white * pulse);
            }
        }
    }
}

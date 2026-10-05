using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Grid
{
    /// Per-tile decoration children: resource-wall nuggets, wall torches,
    /// Chasm spikes and Holy / Unholy Ground stars. All coord-seeded so host
    /// and client build identical ones.
    public partial class DungeonGrid
    {
        [SerializeField] private Color _goldNuggetColor = new Color(0.85f, 0.7f, 0.15f);
        [SerializeField] private Color _regeneratingGoldNuggetColor = new Color(0.95f, 0.8f, 0.2f);
        [SerializeField] private Color _chasmSpikeColor = new Color(0.25f, 0.23f, 0.22f);
        private const int ChasmSpikeCount = 4;
        private const float ChasmSpikeHeight = 0.5f;
        private const float ChasmSpikeRadius = 0.06f;

        // An 8-pointed star — 4 bars through the tile's center, each
        // already double-ended (0°/180°, 45°/225°, ...), so 4 bars at
        // 0/45/90/135 degrees give all 8 points. Same cheap primitives-only
        // placeholder convention every other decoration in this class uses
        // (see RebuildWallDecoration's gold nuggets, BuildChasmSpikes).
        // Shared by Holy Ground (gold) and Unholy Ground (red) — see
        // BuildGroundStar.
        private static readonly float[] GroundStarAngles = { 0f, 45f, 90f, 135f };
        private const float GroundStarLength = 0.75f;
        private const float GroundStarThickness = 0.06f;

        // Sits proud of the floor tile beneath it (like Jail's grate
        // cross), so it never coplanar-z-fights with it.
        private const float GroundStarReliefOffset = 0.02f;

        // "add some gold in a random pattern" — RegeneratingGoldWall gets
        // more nuggets than plain GoldWall so it visually reads as the
        // richer, gold-heavier vein ("switch the amount of gold & rock").
        private const int GoldNuggetCount = 5;
        private const int RegeneratingGoldNuggetCount = 10;

        private void ClearWallDecoration(Vector2Int coord)
        {
            var existing = _wallDecorations[coord.x, coord.y];
            if (existing != null)
            {
                Destroy(existing);
                _wallDecorations[coord.x, coord.y] = null;
            }
        }

        /// Builds the "gold in a random pattern" child decoration for
        /// Gold/RegeneratingGoldWall (nothing for any other type, including
        /// ManaCrystalWall, which is just a color per the brief). The
        /// pattern is seeded from the tile's own coordinate so it's stable
        /// — this only runs once, when SetWallResourceType assigns the
        /// type, not on every RefreshVisual (which fires every hit and
        /// would otherwise re-randomize/flicker the nuggets). Skipped
        /// entirely once a dedicated dungeon_pack mesh exists for the
        /// type (see GetWallMeshPrefab) — its texture already bakes in
        /// gold/crystal clusters, so floating procedural nugget cubes on
        /// top would just look redundant.
        private void RebuildWallDecoration(Vector2Int coord, WallResourceType wallResourceType)
        {
            ClearWallDecoration(coord);

            if (wallResourceType == WallResourceType.GoldWall && _wallMeshGold != null)
            {
                return;
            }

            if (wallResourceType == WallResourceType.RegeneratingGoldWall && _wallMeshGoldRegen != null)
            {
                return;
            }

            if (wallResourceType != WallResourceType.GoldWall && wallResourceType != WallResourceType.RegeneratingGoldWall)
            {
                return;
            }

            var isRegenerating = wallResourceType == WallResourceType.RegeneratingGoldWall;
            var nuggetCount = isRegenerating ? RegeneratingGoldNuggetCount : GoldNuggetCount;
            var nuggetColor = isRegenerating ? _regeneratingGoldNuggetColor : _goldNuggetColor;

            var container = new GameObject("GoldNuggets");
            container.transform.SetParent(_visuals[coord.x, coord.y].transform, false);
            _wallDecorations[coord.x, coord.y] = container;

            var rng = new System.Random(coord.x * 92821 + coord.y * 68917 + 17);
            for (int i = 0; i < nuggetCount; i++)
            {
                var nugget = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nugget.name = $"Nugget_{i}";
                nugget.transform.SetParent(container.transform, false);
                nugget.transform.localPosition = new Vector3(
                    ((float)rng.NextDouble() - 0.5f) * 0.8f,
                    ((float)rng.NextDouble() - 0.5f) * 0.8f,
                    ((float)rng.NextDouble() - 0.5f) * 0.8f);
                var scale = 0.07f + (float)rng.NextDouble() * 0.05f;
                nugget.transform.localScale = Vector3.one * scale;
                Prims.Tint(nugget, nuggetColor);
                Destroy(nugget.GetComponent<Collider>());
            }
        }

        /// Torches stand only on Reinforced walls — a keeper's fortified
        /// frontier, not every bit of rock — against a face that borders a
        /// Floor tile. Called from RefreshVisual (and ApplyReplicatedTile
        /// on the client) for every tile it touches: a Floor tile checks its
        /// 4 neighbors, a wall checks itself. That covers both orders a
        /// torch can become possible in — a wall reinforced next to floor
        /// that's already dug (the usual case), or floor dug out next to an
        /// already-reinforced wall (pre-placed level walls). Idempotent and
        /// cheap: a wall that already has a decoration is skipped, and
        /// WallTorches' sparseness roll is a deterministic per-face hash.
        private void SyncTorchesAround(Vector2Int coord)
        {
            if (_tiles[coord.x, coord.y].Type == TileType.Floor)
            {
                foreach (var dir in GridDirections.Cardinal)
                {
                    var wallCoord = coord + dir;
                    if (InBounds(wallCoord))
                    {
                        ConsiderWallTorch(wallCoord);
                    }
                }
            }
            else
            {
                ConsiderWallTorch(coord);
            }
        }

        private bool IsTorchWall(TileState tile)
        {
            return tile.Type == TileType.Rock && tile.IsReinforced && !tile.HasRoom && !tile.IsBedrock
                && tile.WallResourceType == WallResourceType.None;
        }

        /// Places a torch on wallCoord's first eligible floor-facing side
        /// (each face rolls its own deterministic WallTorches sparseness
        /// check, so the result doesn't depend on refresh order).
        /// _wallDecorations is a single slot shared with gold nuggets /
        /// chasm spikes, so a wall that already has any decoration —
        /// including its torch — is skipped; digging the wall out clears
        /// the slot (CompleteDig).
        private void ConsiderWallTorch(Vector2Int wallCoord)
        {
            if (_wallDecorations[wallCoord.x, wallCoord.y] != null || !IsTorchWall(_tiles[wallCoord.x, wallCoord.y]))
            {
                return;
            }

            foreach (var dir in GridDirections.Cardinal)
            {
                var floorCoord = wallCoord + dir;
                if (!InBounds(floorCoord) || _tiles[floorCoord.x, floorCoord.y].Type != TileType.Floor)
                {
                    continue;
                }

                var torch = WallTorches.TryPlace(_visuals[wallCoord.x, wallCoord.y].transform, wallCoord, dir, _cellSize, _halfWalls);
                if (torch != null)
                {
                    _wallDecorations[wallCoord.x, wallCoord.y] = torch;
                    return;
                }
            }
        }

        /// "Spikes sticking up from the bottom" — a handful of thin,
        /// randomly-placed pointy cubes standing on the sunk Chasm floor
        /// (see SetTerrainFeature/ChasmPitDepth). World-positioned and
        /// parented to this component's own transform, same convention
        /// JailManager's pit structures use (its own dirt floor/prisoner
        /// visuals), rather than nested under the tile's own thin (0.15-tall)
        /// floor cube — nesting there would squash a spike's local offsets
        /// down by that same thin scale. Reuses the seeded-per-coord RNG
        /// pattern RebuildWallDecoration uses for gold nuggets and the same
        /// _wallDecorations slot, built once when the tile becomes a Chasm.
        private void BuildChasmSpikes(Vector2Int coord)
        {
            ClearWallDecoration(coord);

            var container = new GameObject("ChasmSpikes");
            container.transform.SetParent(transform, false);
            _wallDecorations[coord.x, coord.y] = container;

            var floorTopY = FloorSurfaceY - ChasmPitDepth;
            var worldPos = GridToWorld(coord);

            var rng = new System.Random(coord.x * 51239 + coord.y * 30097 + 7);
            for (int i = 0; i < ChasmSpikeCount; i++)
            {
                var spike = GameObject.CreatePrimitive(PrimitiveType.Cube);
                spike.name = $"Spike_{i}";
                spike.transform.SetParent(container.transform, false);
                spike.transform.position = new Vector3(
                    worldPos.x + ((float)rng.NextDouble() - 0.5f) * 0.6f,
                    floorTopY + ChasmSpikeHeight * 0.5f,
                    worldPos.z + ((float)rng.NextDouble() - 0.5f) * 0.6f);
                spike.transform.rotation = Quaternion.Euler(
                    ((float)rng.NextDouble() - 0.5f) * 20f,
                    (float)rng.NextDouble() * 360f,
                    ((float)rng.NextDouble() - 0.5f) * 20f);
                spike.transform.localScale = new Vector3(ChasmSpikeRadius, ChasmSpikeHeight, ChasmSpikeRadius);
                Prims.Tint(spike, _chasmSpikeColor);
                Destroy(spike.GetComponent<Collider>());
            }
        }

        /// An 8-pointed star centered on coord — 4 double-ended bars (see
        /// GroundStarAngles), world-positioned and parented to this
        /// component's own transform, same "don't nest under the tile's own
        /// thin floor cube" convention BuildChasmSpikes uses (nesting there
        /// would squash local offsets down by that cube's thin 0.15 Y-scale).
        /// Reuses the _wallDecorations slot, built once when the tile becomes
        /// Holy Ground (gold star on white) or Unholy Ground (red star on
        /// near-black).
        private void BuildGroundStar(Vector2Int coord, string containerName, Color color)
        {
            ClearWallDecoration(coord);

            var container = new GameObject(containerName);
            container.transform.SetParent(transform, false);
            _wallDecorations[coord.x, coord.y] = container;

            var worldPos = GridToWorld(coord);
            var starY = FloorSurfaceY + GroundStarReliefOffset;

            foreach (var angleDegrees in GroundStarAngles)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bar.name = $"StarBar_{angleDegrees}";
                bar.transform.SetParent(container.transform, false);
                bar.transform.position = new Vector3(worldPos.x, starY, worldPos.z);
                bar.transform.rotation = Quaternion.Euler(0f, angleDegrees, 0f);
                bar.transform.localScale = new Vector3(_cellSize * GroundStarLength, GroundStarThickness, GroundStarThickness);
                Prims.Tint(bar, color);
                Destroy(bar.GetComponent<Collider>());
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// Footprint rules every drag-placed room shares — the rectangle a
    /// drag covers, whether it can be built on, and whether it extends an
    /// existing room of the same type. Each room manager used to carry its
    /// own byte-identical copy of these.
    public static class RoomFootprint
    {
        // Top of an undug Rock tile — preview markers and props on a Rock
        // tile sit on this instead of the floor surface.
        public const float RockTopY = 0.5f;

        /// Every tile in the axis-aligned rectangle spanned by two drag
        /// corners (either order), plus its size and bottom-left origin.
        public static List<Vector2Int> Rect(Vector2Int startCoord, Vector2Int endCoord, out int width, out int height, out Vector2Int origin)
        {
            var minX = Mathf.Min(startCoord.x, endCoord.x);
            var maxX = Mathf.Max(startCoord.x, endCoord.x);
            var minY = Mathf.Min(startCoord.y, endCoord.y);
            var maxY = Mathf.Max(startCoord.y, endCoord.y);

            width = maxX - minX + 1;
            height = maxY - minY + 1;
            origin = new Vector2Int(minX, minY);

            var footprint = new List<Vector2Int>(width * height);
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    footprint.Add(new Vector2Int(x, y));
                }
            }

            return footprint;
        }

        public static List<Vector2Int> Rect(Vector2Int startCoord, Vector2Int endCoord)
        {
            return Rect(startCoord, endCoord, out _, out _, out _);
        }

        /// Whether every tile of footprint could become a room for ownerId
        /// right now — dug, Claimed, room-free Floor that keeper owns
        /// (DungeonGrid.CanBuildRoomOn, the one rule every room funnels
        /// through). Rooms need pre-dug floor: a Jail used to auto-dig undug
        /// Rock on placement, which quietly destroyed dungeon walls.
        public static bool CanPlace(DungeonGrid grid, List<Vector2Int> footprint, int ownerId)
        {
            foreach (var coord in footprint)
            {
                if (!grid.CanBuildRoomOn(coord, ownerId))
                {
                    return false;
                }
            }

            return true;
        }

        /// Whether footprint, combined with some single already-placed room
        /// in roomTiles, would exactly fill a rectangle — i.e. footprint
        /// extends an existing room rather than starting a fresh one. Only
        /// ever merges with one existing room at a time, and returns false
        /// (footprint becomes its own new room) if it doesn't cleanly
        /// complete a rectangle with any single one — including simply not
        /// being adjacent to one at all.
        public static bool TryFindMergeableRoom(Dictionary<string, List<Vector2Int>> roomTiles, List<Vector2Int> footprint,
            out string roomId, out Vector2Int mergedOrigin, out int mergedWidth, out int mergedHeight)
        {
            foreach (var entry in roomTiles)
            {
                var minX = int.MaxValue;
                var maxX = int.MinValue;
                var minY = int.MaxValue;
                var maxY = int.MinValue;

                foreach (var coord in entry.Value)
                {
                    minX = Mathf.Min(minX, coord.x);
                    maxX = Mathf.Max(maxX, coord.x);
                    minY = Mathf.Min(minY, coord.y);
                    maxY = Mathf.Max(maxY, coord.y);
                }

                foreach (var coord in footprint)
                {
                    minX = Mathf.Min(minX, coord.x);
                    maxX = Mathf.Max(maxX, coord.x);
                    minY = Mathf.Min(minY, coord.y);
                    maxY = Mathf.Max(maxY, coord.y);
                }

                var width = maxX - minX + 1;
                var height = maxY - minY + 1;

                if (width * height == entry.Value.Count + footprint.Count)
                {
                    roomId = entry.Key;
                    mergedOrigin = new Vector2Int(minX, minY);
                    mergedWidth = width;
                    mergedHeight = height;
                    return true;
                }
            }

            roomId = null;
            mergedOrigin = default;
            mergedWidth = 0;
            mergedHeight = 0;
            return false;
        }

        /// The walkable top surface of a tile — Rock top for undug Rock,
        /// the floor surface for everything else.
        public static float GroundTopY(DungeonGrid grid, Vector2Int coord)
        {
            return grid.GetTile(coord).Type == TileType.Rock ? RockTopY : grid.FloorSurfaceY;
        }
    }

    /// The flat green/red tile markers shown while dragging out a room —
    /// composed into each room manager (one instance per manager).
    public sealed class RoomPlacementPreview
    {
        private static readonly Color ValidColor = new Color(0.35f, 0.95f, 0.4f);
        private static readonly Color InvalidColor = new Color(0.95f, 0.25f, 0.25f);
        private const float Clearance = 0.02f;
        private const float Height = 0.08f;
        private const float FootprintScale = 0.8f;

        private readonly string _namePrefix;
        private readonly List<GameObject> _markers = new List<GameObject>();

        /// namePrefix names each marker GameObject "{namePrefix}Preview_x_y".
        public RoomPlacementPreview(string namePrefix)
        {
            _namePrefix = namePrefix;
        }

        /// Replaces any current preview with one marker per in-bounds tile
        /// of footprint, parented under parent, green if valid else red.
        public void Show(Transform parent, DungeonGrid grid, List<Vector2Int> footprint, bool valid)
        {
            Clear();

            var color = valid ? ValidColor : InvalidColor;
            foreach (var coord in footprint)
            {
                if (!grid.InBounds(coord))
                {
                    continue;
                }

                _markers.Add(CreateMarker(parent, grid, coord, color));
            }
        }

        public void Clear()
        {
            foreach (var marker in _markers)
            {
                if (marker != null)
                {
                    Object.Destroy(marker);
                }
            }
            _markers.Clear();
        }

        private GameObject CreateMarker(Transform parent, DungeonGrid grid, Vector2Int coord, Color color)
        {
            var cellSize = grid.CellSize;
            var worldPos = grid.GridToWorld(coord);
            var centerY = RoomFootprint.GroundTopY(grid, coord) + Clearance + Height * 0.5f;

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = $"{_namePrefix}Preview_{coord.x}_{coord.y}";
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = new Vector3(worldPos.x, centerY, worldPos.z);
            marker.transform.localScale = new Vector3(cellSize * FootprintScale, Height, cellSize * FootprintScale);
            Prims.Tint(marker, color);
            Object.Destroy(marker.GetComponent<Collider>());
            return marker;
        }
    }
}

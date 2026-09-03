using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.Rooms;

namespace KeepersDomain.LevelDesigner
{
    /// Shared "rebuild rooms from saved tile data" logic — originally
    /// LevelDesignerSession's own private RestoreRooms/ResolveRoomManager,
    /// pulled out here so GameBootstrap's "Start Game loads level1"
    /// gameplay path (see BuildWorld) can reconstruct rooms through the
    /// exact same IRestorableRoomManager dispatch instead of drifting out
    /// of sync with a second hand-copied version.
    public static class RoomReconstruction
    {
        /// Rebuilds every saved room's real decoration from its grouped
        /// tile footprint, dispatched to the right manager via
        /// ResolveRoomManager. A rectangular room gets one
        /// IRestorableRoomManager.RestoreRoom call per RoomId with the
        /// footprint's bounding rectangle; BridgeManager (whose "rooms" are
        /// single tiles, never rectangles) gets one call per footprint
        /// tile with start == end. Falls back to DungeonGrid.
        /// EditorPlaceRoomTile's bare placeholder-colored-cube tagging for
        /// anything that doesn't resolve to a known manager (an
        /// unrecognized/stale prefix, or RestoreRoom itself rejecting the
        /// footprint), so nothing is ever silently dropped. roomManagers
        /// may be null (treated as "nothing resolves") — every entry just
        /// falls back to the placeholder tag.
        public static void RestoreRooms(DungeonGrid grid, Dictionary<string, List<Vector2Int>> roomFootprints, Dictionary<string, int> roomOwners, Dictionary<RoomDesignTool, IRestorableRoomManager> roomManagers)
        {
            foreach (var entry in roomFootprints)
            {
                var roomId = entry.Key;
                var footprint = entry.Value;
                var manager = ResolveRoomManager(roomId, roomManagers);
                var owner = roomOwners.TryGetValue(roomId, out var o) ? o : 0;

                if (manager is BridgeManager)
                {
                    // A bridge isn't a rectangle — each tile is its own
                    // Bridge_{n} "room" (see BridgeManager's class header),
                    // so restore exactly the saved tiles. A bounding-box
                    // call could otherwise swallow an unbridged Water/Lava
                    // pool sitting between two of them.
                    var restoredAny = false;
                    foreach (var coord in footprint)
                    {
                        restoredAny |= manager.RestoreRoom(coord, coord, owner);
                    }
                    if (restoredAny)
                    {
                        continue;
                    }
                }
                else if (manager != null)
                {
                    var minX = int.MaxValue;
                    var maxX = int.MinValue;
                    var minY = int.MaxValue;
                    var maxY = int.MinValue;
                    foreach (var coord in footprint)
                    {
                        minX = Mathf.Min(minX, coord.x);
                        maxX = Mathf.Max(maxX, coord.x);
                        minY = Mathf.Min(minY, coord.y);
                        maxY = Mathf.Max(maxY, coord.y);
                    }

                    if (manager.RestoreRoom(new Vector2Int(minX, minY), new Vector2Int(maxX, maxY), owner))
                    {
                        continue;
                    }
                }

                foreach (var coord in footprint)
                {
                    grid.EditorPlaceRoomTile(coord, roomId);
                }
            }
        }

        /// The manager owning roomId, found by the same
        /// "{RoomDesignTool}_{index}" prefix convention every room
        /// manager's own roomId minting already uses (e.g. LairManager.
        /// GetCostPerTileForRoomId, BridgeManager's "Bridge_{n}") — null for
        /// anything that doesn't parse to a known RoomDesignTool, isn't in
        /// roomManagers, or roomManagers itself is null. "BaconBeacon" is
        /// special-cased as a legacy alias for RoomDesignTool.Tavern (its
        /// renamed successor, see TavernManager) so a level1.json saved
        /// before that rename still reconstructs its Tavern room(s)
        /// correctly instead of falling back to a placeholder cube.
        public static IRestorableRoomManager ResolveRoomManager(string roomId, Dictionary<RoomDesignTool, IRestorableRoomManager> roomManagers)
        {
            if (roomManagers == null)
            {
                return null;
            }

            var separatorIndex = roomId.LastIndexOf('_');
            if (separatorIndex <= 0)
            {
                return null;
            }

            var prefix = roomId.Substring(0, separatorIndex);
            if (prefix == "BaconBeacon")
            {
                prefix = nameof(RoomDesignTool.Tavern);
            }

            return Enum.TryParse<RoomDesignTool>(prefix, out var tool) && roomManagers.TryGetValue(tool, out var manager)
                ? manager
                : null;
        }

        /// Maps a saved tile's WallResourceType to the EditorWallVariant
        /// DungeonGrid.EditorPaintWall expects — shared by
        /// LevelDesignerSession.RestoreTile and GameBootstrap's own
        /// "restore a gameplay world from a save" tile loop.
        public static EditorWallVariant ToEditorWallVariant(WallResourceType wallResourceType)
        {
            switch (wallResourceType)
            {
                case WallResourceType.GoldWall:
                    return EditorWallVariant.GoldWall;
                case WallResourceType.RegeneratingGoldWall:
                    return EditorWallVariant.RegeneratingGoldWall;
                case WallResourceType.ManaCrystalWall:
                    return EditorWallVariant.ManaCrystalWall;
                default:
                    return EditorWallVariant.Plain;
            }
        }
    }
}

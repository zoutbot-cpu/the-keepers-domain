using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Grid
{
    /// Unconditional tile painters for the Level Designer (Editor*) and the
    /// in-game [Dev] Terrain tool (DevPaintTerrain) — they bypass the
    /// gameplay rules the Jobs methods enforce.
    public partial class DungeonGrid
    {
        // ---- Level-editor-only tile authoring ----
        // Unlike the gameplay-facing methods above (RequestDig,
        // SetWallResourceType, SetTerrainFeature, SetBedrock, ...), these
        // unconditionally overwrite a tile to the exact requested state
        // with no "must already be Rock" precondition — the level
        // designer needs to freely repaint any tile into any other type,
        // authoring finished level data rather than simulating how it got
        // that way (no dig jobs, no gold cost, no implings).

        /// Resets coord back to plain, undamaged, unowned Rock — the
        /// common baseline every other Editor* method below starts from,
        /// so painting a tile into a wall/terrain/floor variant can never
        /// leave stale flags (an old WallResourceType, RoomId, ownership,
        /// ...) behind from whatever it used to be.
        public void EditorResetToRock(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ClearWallDecoration(coord);
            _tiles[coord.x, coord.y] = TileState.Rock;
            RefreshVisual(coord);
        }

        /// Paints coord into one of the wall variants the level designer's
        /// Map Design menu offers (see EditorWallVariant) — resets to
        /// plain Rock first, then reuses the same guarded gameplay methods
        /// (SetWallResourceType/SetBedrock) where possible so their
        /// existing HP/decoration logic doesn't need duplicating. ownerId
        /// only matters for the Reinforced case (see TileState.OwnerId/
        /// ApplyOrbOwnerColor — a Reinforced wall's orb is tinted by its
        /// owner) and is otherwise ignored; defaults to -1 ("no owner"),
        /// same fallback every other Editor* ownership param uses.
        public void EditorPaintWall(Vector2Int coord, EditorWallVariant variant, int ownerId = -1)
        {
            if (!InBounds(coord))
            {
                return;
            }

            EditorResetToRock(coord);
            switch (variant)
            {
                case EditorWallVariant.Reinforced:
                {
                    ref var tile = ref _tiles[coord.x, coord.y];
                    tile.IsReinforced = true;
                    tile.Hp = TileState.ReinforcedMaxHp;
                    tile.OwnerId = ownerId;
                    RefreshVisual(coord);
                    break;
                }
                case EditorWallVariant.GoldWall:
                    SetWallResourceType(coord, WallResourceType.GoldWall);
                    break;
                case EditorWallVariant.RegeneratingGoldWall:
                    SetWallResourceType(coord, WallResourceType.RegeneratingGoldWall);
                    break;
                case EditorWallVariant.ManaCrystalWall:
                    SetWallResourceType(coord, WallResourceType.ManaCrystalWall);
                    break;
                case EditorWallVariant.Bedrock:
                    SetBedrock(coord);
                    break;
                // Plain: EditorResetToRock above already leaves it as
                // plain, undamaged Rock.
            }
        }

        /// Paints coord into Water/Lava/Chasm — resets to plain Rock first
        /// (unlike SetTerrainFeature, which refuses anything that isn't
        /// already Rock) so the level designer can freely repaint any tile,
        /// then reuses SetTerrainFeature itself for the actual conversion
        /// so Chasm's pit-depth/spike decoration logic doesn't need
        /// duplicating.
        public void EditorPaintTerrain(Vector2Int coord, TileType terrainType)
        {
            if (!InBounds(coord))
            {
                return;
            }

            EditorResetToRock(coord);
            SetTerrainFeature(coord, terrainType);
        }

        /// Digs coord directly into Floor, Claimed or not, skipping the
        /// dig-job/impling flow entirely (see BuilderJobBoard) — the level
        /// designer authors finished level state, not a simulation of how
        /// it got that way. ownerId is only meaningful when claimed is
        /// true (see TileState.OwnerId); an Unclaimed tile always ends up
        /// with ownerId -1 regardless of what's passed in.
        public void EditorPaintFloor(Vector2Int coord, bool claimed, int ownerId)
        {
            if (!InBounds(coord))
            {
                return;
            }

            EditorResetToRock(coord);
            ref var tile = ref _tiles[coord.x, coord.y];
            tile.Type = TileType.Floor;
            tile.Ownership = claimed ? TileOwnership.Claimed : TileOwnership.Unclaimed;
            tile.OwnerId = claimed ? ownerId : -1;
            tile.IsBuildable = true;
            RefreshVisual(coord);
        }

        /// The in-game "[Dev] Terrain" Build-menu tools (see
        /// BuildMode.PlaceWater/PlaceLava/PlaceChasm/PlaceHolyGround/PlaceUnholyGround/
        /// PlaceFloor/PlaceRock and IKeeperActions.SetTerrain) — repaints
        /// coord into any floor/terrain/rock type, the same free any-to-any
        /// conversion the Level Designer's Map Design menu does, so a dev can
        /// reshape a dug-out dungeon at runtime while the map generator
        /// doesn't exist yet. Refuses a room tile (a room manager still owns
        /// that tile's bookkeeping) and a Bedrock tile (the map border — and
        /// Bedrock is the separate SetBedrock "wall" tool's job). Routes
        /// through the same unconditional Editor* painters the Level Designer
        /// uses so decoration/pit-depth teardown doesn't need re-deriving.
        public void DevPaintTerrain(Vector2Int coord, TileType type)
        {
            if (!InBounds(coord))
            {
                return;
            }

            var tile = GetTile(coord);
            if (tile.HasRoom || tile.IsBedrock)
            {
                return;
            }

            switch (type)
            {
                case TileType.Rock:
                    EditorResetToRock(coord);
                    break;
                case TileType.Floor:
                    EditorPaintFloor(coord, claimed: false, ownerId: -1);
                    break;
                default:
                    EditorPaintTerrain(coord, type);
                    break;
            }
        }

        /// Reassigns coord's owner without touching anything else about
        /// it — deliberately narrower than EditorPaintFloor/EditorPaintWall,
        /// which each re-derive other tile state (Ownership/IsReinforced/
        /// HP/...) alongside ownerId. Used by the Level Designer's edit
        /// mode to let the author reassign who an already-placed Claimed
        /// floor tile or Reinforced wall belongs to. No-ops on anything
        /// that isn't already one of those two — an unowned tile type
        /// (plain Rock, Unclaimed floor, terrain, ...) has nothing
        /// meaningful to reassign. A room tile is Claimed Floor too, so
        /// this works for one — but see EditorReassignRoomOwner for
        /// reassigning a whole room's footprint at once, which is what
        /// the edit mode actually calls for a room tile.
        public void EditorReassignOwner(Vector2Int coord, int ownerId)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            var isReassignable = (tile.Type == TileType.Floor && tile.Ownership == TileOwnership.Claimed) || tile.IsReinforced;
            if (!isReassignable)
            {
                return;
            }

            // Reassigning a plain Claimed floor tile to the "Unclaimed"
            // pseudo-player (ownerId < 0) actually unclaims it — a claimed
            // floor tile with no owner isn't a state this game models. A
            // Reinforced wall stays a wall and just drops its orb owner
            // (see ApplyOrbOwnerColor's -1 fallback).
            if (ownerId < 0 && tile.Type == TileType.Floor)
            {
                tile.Ownership = TileOwnership.Unclaimed;
            }

            tile.OwnerId = ownerId;
            RefreshVisual(coord);
        }

        /// Reassigns every tile belonging to roomId to a new owner — a
        /// room has one owner conceptually, tracked per-tile the same as
        /// plain Claimed floor (no room manager tracks a per-room owner
        /// separately — see RestoreRoom/TryAssignRoom), so the Level
        /// Designer's edit mode reassigns a whole room at once rather than
        /// just whichever single tile was tapped. Same whole-grid-scan
        /// shape as RemoveRoomTiles. Returns how many tiles it actually
        /// reassigned.
        public int EditorReassignRoomOwner(string roomId, int ownerId)
        {
            var count = 0;
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    if (_tiles[x, y].RoomId == roomId)
                    {
                        _tiles[x, y].OwnerId = ownerId;
                        RefreshVisual(new Vector2Int(x, y));
                        count++;
                    }
                }
            }

            return count;
        }

        /// Whether the level designer's Rooms menu could stamp a room onto
        /// coord — anything except Water/Lava/Chasm/HolyGround (rooms only
        /// ever go on Floor, or plain Rock about to be dug for one — see
        /// EditorPlaceRoomTile) or a tile that already has a room.
        /// HolyGround is excluded the same way Water/Lava/Chasm are — it
        /// can never be Claimed (see TileType.HolyGround), which a room
        /// tile always implicitly is.
        public bool EditorCanPlaceRoomOn(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            var tile = GetTile(coord);
            return tile.Type != TileType.Water && tile.Type != TileType.Lava && tile.Type != TileType.Chasm
                && tile.Type != TileType.HolyGround && tile.Type != TileType.UnholyGround && !tile.HasRoom;
        }

        /// "If the area hasn't been dug out, just instantly dig it out,
        /// make the tile into Unclaimed tile, and place the room" — per
        /// the level-designer brief. A tile that's already Floor keeps
        /// whatever ownership it already has (only a freshly-dug tile is
        /// forced Unclaimed); no gold cost, no manager/economy wiring —
        /// this just tags the tile with roomId the same way TryAssignRoom
        /// does, which is enough for it to render as a room (see
        /// RefreshVisual's HasRoom branch).
        public bool EditorPlaceRoomTile(Vector2Int coord, string roomId)
        {
            if (!EditorCanPlaceRoomOn(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Floor)
            {
                ClearWallDecoration(coord);
                tile = TileState.Rock;
                tile.Type = TileType.Floor;
                tile.Ownership = TileOwnership.Unclaimed;
                tile.OwnerId = -1;
                tile.IsBuildable = true;
            }

            tile.RoomId = roomId;
            tile.Hp = TileState.RoomMaxHp;
            RefreshVisual(coord);
            return true;
        }
    }
}

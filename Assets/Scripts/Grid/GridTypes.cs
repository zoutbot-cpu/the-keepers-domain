using System;
using UnityEngine;

namespace KeepersDomain.Grid
{
    public static class GridDirections
    {
        public static readonly Vector2Int[] Cardinal =
        {
            Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right
        };
    }

    public enum TileType
    {
        Rock,
        Floor,

        /// Undeep — every creature can wade through except Imps, unless the
        /// tile has a Bridge (see DungeonGrid.IsWalkable/TryAssignBridgeRoom).
        Water,

        /// Undeep, but nothing is fire-resistant yet, so it's impassable to
        /// everyone (Imps included) until a Bridge is built on it.
        Lava,

        /// Deep — as deep as a Jail's pit, spikes at the bottom. Never
        /// walkable by anyone; a Bridge can never be built across it.
        Chasm,

        /// Walkable by everyone, same as Floor — but can never be Claimed
        /// (DungeonGrid.ClaimTile/CanBuildRoomOn/BordersClaimedTile are all
        /// already gated to Type == Floor, so this falls out of that same
        /// check for free rather than needing its own guard). Territory
        /// can't grow through it either, for the same reason.
        HolyGround
    }

    public enum TileOwnership
    {
        Unclaimed,
        Claimed
    }

    /// A Rock tile can optionally be a resource vein instead of a plain
    /// wall — mined (not dug — see BuilderJobBoard/ImplingAgent) for
    /// resources that go straight into the mining impling's inventory
    /// rather than just clearing the tile for its own sake. Mutually
    /// exclusive with IsReinforced (DungeonGrid.RequestReinforce rejects
    /// resource walls).
    public enum WallResourceType
    {
        None,
        GoldWall,
        RegeneratingGoldWall,
        ManaCrystalWall
    }

    /// What a mined resource wall drops into an impling's inventory (see
    /// ImplingInventory). Kept separate from WallResourceType since two
    /// wall types (GoldWall, RegeneratingGoldWall) both drop Gold.
    public enum ResourceType
    {
        None,
        Gold,
        ManaCrystal
    }

    /// Every wall variant the level designer's Map Design menu can paint
    /// (see DungeonGrid.EditorPaintWall) — Plain/Reinforced/Bedrock plus
    /// the three WallResourceType veins, unified into one list since the
    /// editor picks exactly one at a time regardless of which underlying
    /// TileState fields it maps to.
    public enum EditorWallVariant
    {
        Plain,
        Reinforced,
        GoldWall,
        RegeneratingGoldWall,
        ManaCrystalWall,
        Bedrock
    }

    [Serializable]
    public struct TileState
    {
        public const int RockMaxHp = 100;
        public const int ReinforcedMaxHp = 200;
        public const int GoldWallMaxHp = 200;
        public const int RegeneratingGoldWallMaxHp = 1000;
        public const int RegeneratingGoldWallRegenPerHit = 15;
        public const int ManaCrystalWallMaxHp = 100;

        /// Bedrock never actually takes dig damage (RequestDig/
        /// ApplyDigDamage both refuse it — see DungeonGrid.SetBedrock), so
        /// this only matters for display (Hp is set equal to it once and
        /// never changes) — any positive value works, kept simply higher
        /// than ReinforcedMaxHp to read as "more wall than reinforced."
        public const int BedrockMaxHp = 9999;

        /// HP a Floor tile gets once a room (Lair, Treasury, ...) is
        /// placed on it — see DungeonGrid.TryAssignRoom. Damaged by an
        /// unhappy creature's attack (DungeonGrid.ApplyRoomDamage) and
        /// restored by an impling's repair job (DungeonGrid.ApplyRoomRepair
        /// / BuilderJobBoard's RepairRoom jobs) — see design-doc.md's
        /// Happiness section and the Room tile repair section.
        public const int RoomMaxHp = 50;

        /// Resource yield per point of HP a hit actually removes — "drop
        /// default of 1 [resource] per hp lost" for every resource wall
        /// type. A single named constant since all three currently agree,
        /// but kept separate from the HP consts above in case a future
        /// wall type wants its own rate.
        public const int ResourceDropPerHp = 1;

        public TileType Type;
        public TileOwnership Ownership;
        public bool IsQueuedForDig;
        public bool IsQueuedForReinforce;
        public bool IsReinforced;

        /// Permanently unminable — RequestDig/RequestReinforce both refuse
        /// a Bedrock tile outright (see DungeonGrid), so it can never be
        /// queued for either. Mutually exclusive with IsReinforced/
        /// WallResourceType, same as those are with each other; darker than
        /// a reinforced wall (see DungeonGrid's own _bedrockColor). Placed
        /// today by a dev-only Build-menu tool, same as Water/Lava/Chasm.
        public bool IsBedrock;

        public bool IsQueuedForBuild;
        public WallResourceType WallResourceType;
        public bool IsUnreachable;
        public string RoomId;
        public int Hp;

        /// Which level-designer player owns a Claimed tile — -1 for
        /// "no owner" (every ordinary gameplay tile, and any Unclaimed
        /// tile). Only ever set by DungeonGrid's Editor* authoring methods
        /// (see EditorPaintFloor); gameplay's own ClaimTile/CarveRoom/
        /// CarveRect never touch it, so it stays -1 everywhere BuildWorld's
        /// single-dungeon prototype is concerned.
        public int OwnerId;

        /// Floor that's otherwise ordinary but explicitly off-limits to
        /// pathfinding — e.g. the Chaos Core's center tile, which stays
        /// Floor/Claimed for room purposes but sits under the raised orb
        /// pedestal, not something an impling should walk onto. See
        /// DungeonGrid.IsWalkable/SetBlocked.
        public bool IsBlocked;

        // Meaningless for Rock — only matters once a tile is Floor. Normal
        // dug-out floor defaults to true (see DungeonGrid.CompleteDig);
        // fixed feature rooms (Chaos Core, Portal room, Treasury, the
        // corridors to them) are carved with this explicitly false so a
        // Lair can never be placed on top of them, independent of
        // RoomId/HasRoom — which is tied to the shared purple room-tile
        // color and shouldn't be forced on tiles that already have their
        // own distinct visual.
        public bool IsBuildable;

        /// Extra downward Y offset (world units) applied to this tile's
        /// floor visual at render time — 0 for ordinary flush floor.
        /// Purely cosmetic (see DungeonGrid.RefreshVisual/SetPitDepth):
        /// IsWalkable/CanBuildRoomOn/pathfinding never look at this, so a
        /// sunk tile is exactly as walkable as any other Floor tile.
        /// JailManager uses this to sink its pit one full level below the
        /// surrounding ground.
        public float PitDepth;

        public bool HasRoom => !string.IsNullOrEmpty(RoomId);

        /// A reinforced Rock tile takes twice the hits to dig through; a
        /// resource wall has its own fixed max HP regardless of
        /// reinforcement (the two are mutually exclusive anyway — see
        /// RequestReinforce). Meaningless once the tile is Floor, unless a
        /// room's been placed on it (HasRoom), which has its own fixed
        /// RoomMaxHp independent of the Rock-wall cases below.
        public int MaxHp
        {
            get
            {
                if (HasRoom)
                {
                    return RoomMaxHp;
                }

                if (IsBedrock)
                {
                    return BedrockMaxHp;
                }

                switch (WallResourceType)
                {
                    case WallResourceType.GoldWall:
                        return GoldWallMaxHp;
                    case WallResourceType.RegeneratingGoldWall:
                        return RegeneratingGoldWallMaxHp;
                    case WallResourceType.ManaCrystalWall:
                        return ManaCrystalWallMaxHp;
                    default:
                        return IsReinforced ? ReinforcedMaxHp : RockMaxHp;
                }
            }
        }

        public static TileState Rock => new TileState
        {
            Type = TileType.Rock,
            Ownership = TileOwnership.Unclaimed,
            OwnerId = -1,
            IsQueuedForDig = false,
            RoomId = null,
            Hp = RockMaxHp,
            IsBuildable = false,
            WallResourceType = WallResourceType.None
        };
    }
}

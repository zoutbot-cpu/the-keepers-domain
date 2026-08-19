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
        Floor
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

    [Serializable]
    public struct TileState
    {
        public const int RockMaxHp = 100;
        public const int ReinforcedMaxHp = 200;
        public const int GoldWallMaxHp = 200;
        public const int RegeneratingGoldWallMaxHp = 1000;
        public const int RegeneratingGoldWallRegenPerHit = 15;
        public const int ManaCrystalWallMaxHp = 100;

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
        public bool IsQueuedForBuild;
        public WallResourceType WallResourceType;
        public bool IsUnreachable;
        public string RoomId;
        public int Hp;

        // Meaningless for Rock — only matters once a tile is Floor. Normal
        // dug-out floor defaults to true (see DungeonGrid.CompleteDig);
        // fixed feature rooms (Chaos Core, Portal room, Treasury, the
        // corridors to them) are carved with this explicitly false so a
        // Lair can never be placed on top of them, independent of
        // RoomId/HasRoom — which is tied to the shared purple room-tile
        // color and shouldn't be forced on tiles that already have their
        // own distinct visual.
        public bool IsBuildable;

        public bool HasRoom => !string.IsNullOrEmpty(RoomId);

        /// A reinforced Rock tile takes twice the hits to dig through; a
        /// resource wall has its own fixed max HP regardless of
        /// reinforcement (the two are mutually exclusive anyway — see
        /// RequestReinforce). Meaningless once the tile is Floor.
        public int MaxHp
        {
            get
            {
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
            IsQueuedForDig = false,
            RoomId = null,
            Hp = RockMaxHp,
            IsBuildable = false,
            WallResourceType = WallResourceType.None
        };
    }
}

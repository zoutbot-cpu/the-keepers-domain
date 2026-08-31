using System;
using System.Collections.Generic;
using KeepersDomain.Grid;

namespace KeepersDomain.LevelDesigner
{
    /// Everything the level designer's Save/Load menu writes to/reads from
    /// disk (see LevelFileIO) — map size, the player roster, every
    /// non-default tile, and every placed creature. Plain, JsonUtility-
    /// serializable data — LevelDesignerSession is what actually knows how
    /// to build one of these from (or apply one back onto) a live
    /// DungeonGrid/player roster (see BuildLevelData/ApplyLevelData).
    [Serializable]
    public class LevelData
    {
        public int MapWidth;
        public int MapHeight;
        public bool Multiplayer;
        public List<LevelPlayerData> Players = new List<LevelPlayerData>();
        public List<LevelTileData> Tiles = new List<LevelTileData>();
        public List<LevelCreatureData> Creatures = new List<LevelCreatureData>();
        public List<LevelStructureData> Structures = new List<LevelStructureData>();
    }

    [Serializable]
    public class LevelPlayerData
    {
        public bool IsAI;
        public int ColorIndex;
        public int StartingGold;
        public int StartingMana;
    }

    /// One non-default tile — plain, untouched Rock (what every tile
    /// starts as after DungeonGrid.Initialize) is never stored, since
    /// there's nothing to restore for it. See LevelDesignerSession.
    /// IsDefaultRock/BuildLevelData.
    [Serializable]
    public class LevelTileData
    {
        public int X;
        public int Y;
        public TileType Type;
        public TileOwnership Ownership;
        public int OwnerId;
        public bool IsReinforced;
        public bool IsBedrock;
        public WallResourceType WallResourceType;
        public string RoomId;
    }

    [Serializable]
    public class LevelCreatureData
    {
        public EditorCreatureKind Kind;
        public int X;
        public int Y;
        public int OwnerId;
    }

    /// A Throne Room or Portal Room — see StructureKind's own header for why
    /// these are saved separately from LevelTileData despite also
    /// covering a footprint of tiles (that footprint is saved too, as
    /// ordinary LevelTileData entries; this is only the extra "there's a
    /// ThroneRoom/Portal structure centered here" fact those tiles alone
    /// don't carry).
    [Serializable]
    public class LevelStructureData
    {
        public StructureKind Kind;
        public int X;
        public int Y;
        public int OwnerId;
    }
}

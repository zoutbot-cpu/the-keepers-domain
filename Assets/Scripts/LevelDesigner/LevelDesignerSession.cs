using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.UI;

namespace KeepersDomain.LevelDesigner
{
    /// Every creature species the level designer's Creatures menu can
    /// place. Matches CreatureNames' own species list (Implings/Gremlin/
    /// Warlock/MazeRattler/BeanCounter/Elf) — Elf included even though it's
    /// never recruited through the Portal in ordinary play (see
    /// ElfSpawner's own header), since a level author should still be able
    /// to seed one directly.
    public enum EditorCreatureKind
    {
        Imp,
        Gremlin,
        Warlock,
        MazeRattler,
        BeanCounter,
        Elf
    }

    /// The two fixed-footprint "structure" landmarks the level designer's
    /// Rooms menu can place — unlike Lair/Treasury/etc. (a drag-sized,
    /// RoomId-tagged footprint with no live component behind it — see
    /// EditorPlaceRoomTile), these reuse the real ChaosCore/Portal
    /// components directly (see PlaceStructure), since both are already
    /// self-contained (Initialize just needs a coord + the grid) and
    /// building their platform/staircase visuals from scratch here would
    /// only duplicate that code.
    public enum StructureKind
    {
        CoreRoom,
        PortalRoom
    }

    /// One structure placed via the level designer's Rooms menu — see
    /// StructureKind's own header for why this reuses the live
    /// ChaosCore/Portal components rather than being pure authored data
    /// like PlacedCreature/EditorPlaceRoomTile.
    public struct PlacedStructure
    {
        public StructureKind Kind;
        public Vector2Int Coord;
        public int OwnerId;
        public GameObject Visual;
    }

    /// One creature placed via the level designer's Creatures menu —
    /// authored level data (kind, coord, owning player) plus its visual
    /// marker, not a live AI agent. None of the gameplay systems a real
    /// agent needs (BuilderJobBoard, the room managers, ...) exist in the
    /// editor, so there's nothing for one to actually do yet; a real game
    /// session would read this data to spawn the real thing later.
    public struct PlacedCreature
    {
        public EditorCreatureKind Kind;
        public Vector2Int Coord;
        public int OwnerId;
        public GameObject Visual;
    }

    /// Owns the level currently being authored: map size, the player
    /// roster, and every creature placed on it so far. One instance per
    /// Level Designer session — see GameBootstrap.BuildLevelDesignerWorld.
    public class LevelDesignerSession : MonoBehaviour
    {
        // Same ranges LevelDesignerPropertiesMenu seeds its own starting
        // player count from — kept as this session's own copy (not a
        // shared reference) since the properties screen only ever needs a
        // starting default, while this session is what actually enforces
        // the range for the rest of the editing session (e.g. Menu 1's
        // player-count stepper).
        public const int SingleplayerMinPlayers = 1;
        public const int SingleplayerMaxPlayers = 4;
        public const int MultiplayerMinPlayers = 2;
        public const int MultiplayerMaxPlayers = 4;

        public const int StartingGoldDefault = 1000;
        public const int StartingManaDefault = 500;

        private static readonly Dictionary<EditorCreatureKind, Color> SpeciesColors = new Dictionary<EditorCreatureKind, Color>
        {
            { EditorCreatureKind.Imp, new Color(0.8f, 0.2f, 0.2f) },
            { EditorCreatureKind.Gremlin, new Color(0.3f, 0.65f, 0.3f) },
            { EditorCreatureKind.Warlock, new Color(0.35f, 0.15f, 0.55f) },
            { EditorCreatureKind.MazeRattler, new Color(0.55f, 0.5f, 0.35f) },
            { EditorCreatureKind.BeanCounter, new Color(0.6f, 0.45f, 0.2f) },
            { EditorCreatureKind.Elf, new Color(0.25f, 0.55f, 0.4f) }
        };

        private const float CreatureScale = 0.33f;
        private const float OwnerRingDiameter = 0.5f;
        private const float OwnerRingThickness = 0.02f;

        // Matches GameBootstrap's own ChaosCoreRoomHalfSize/
        // PortalRoomHalfSize exactly, so a structure placed here looks
        // and sizes identically to the fixed starting one BuildWorld
        // carves — 5x5 for the Core Room, 3x3 for the Portal Room.
        private const int CoreRoomHalfSize = 2;
        private const int PortalRoomHalfSize = 1;

        private DungeonGrid _grid;
        private readonly List<LevelDesignerPlayer> _players = new List<LevelDesignerPlayer>();
        private readonly List<PlacedCreature> _creatures = new List<PlacedCreature>();
        private readonly List<PlacedStructure> _structures = new List<PlacedStructure>();

        public int MapWidth { get; private set; }
        public int MapHeight { get; private set; }
        public bool Multiplayer { get; private set; }

        public IReadOnlyList<LevelDesignerPlayer> Players => _players;
        public IReadOnlyList<PlacedCreature> Creatures => _creatures;
        public IReadOnlyList<PlacedStructure> Structures => _structures;

        public void Initialize(DungeonGrid grid, LevelDesignerProperties properties)
        {
            _grid = grid;
            MapWidth = properties.MapWidth;
            MapHeight = properties.MapHeight;
            Multiplayer = properties.Multiplayer;

            SetPlayerCount(properties.PlayerCount);
        }

        /// Same idea as Initialize, but seeded from a previously saved
        /// LevelData instead of fresh LevelDesignerPropertiesMenu input —
        /// see GameBootstrap.LoadLevelDesignerWorld. Only sets up the
        /// player roster/map info; restoring the grid's actual tiles and
        /// placed creatures is ApplyLevelData's job, called separately
        /// once this session exists.
        public void InitializeFromSave(DungeonGrid grid, LevelData data)
        {
            _grid = grid;
            MapWidth = data.MapWidth;
            MapHeight = data.MapHeight;
            Multiplayer = data.Multiplayer;

            _players.Clear();
            foreach (var playerData in data.Players)
            {
                _players.Add(new LevelDesignerPlayer
                {
                    IsAI = playerData.IsAI,
                    ColorIndex = playerData.ColorIndex,
                    StartingGold = playerData.StartingGold,
                    StartingMana = playerData.StartingMana
                });
            }

            RefreshGridOwnerColors();
        }

        public void GetPlayerCountRange(out int min, out int max)
        {
            min = Multiplayer ? MultiplayerMinPlayers : SingleplayerMinPlayers;
            max = Multiplayer ? MultiplayerMaxPlayers : SingleplayerMaxPlayers;
        }

        /// Adds/removes trailing player entries to match count — existing
        /// entries (color/gold/mana/AI toggle) are left untouched rather
        /// than reset, so nudging the count on Menu 1 doesn't discard
        /// what's already been configured for the players that remain.
        public void SetPlayerCount(int count)
        {
            GetPlayerCountRange(out var min, out var max);
            count = Mathf.Clamp(count, min, max);

            while (_players.Count < count)
            {
                var index = _players.Count;
                _players.Add(new LevelDesignerPlayer
                {
                    // First player defaults to human, every added one
                    // after that to AI — matches the "1 human + N AI"
                    // framing the singleplayer mode's own hint text uses.
                    IsAI = index > 0,
                    ColorIndex = index % LevelDesignerColors.Palette.Length,
                    StartingGold = StartingGoldDefault,
                    StartingMana = StartingManaDefault
                });
            }

            while (_players.Count > count)
            {
                _players.RemoveAt(_players.Count - 1);
            }

            RefreshGridOwnerColors();
        }

        private void RefreshGridOwnerColors()
        {
            var colors = new Color[_players.Count];
            for (int i = 0; i < _players.Count; i++)
            {
                colors[i] = _players[i].Color;
            }

            _grid.EditorOwnerColors = colors;
        }

        /// Places a creature marker — authored level data (see
        /// PlacedCreature's own header), not a live AI agent. Visual is a
        /// species-colored capsule (same primitive every other placeholder
        /// creature/impling visual in this prototype already uses — see
        /// ImplingSpawner.SpawnImpling) standing on a thin owner-colored
        /// disc, so both species and owner are readable at a glance
        /// without needing to click each one. ownerId < 0 (no owner
        /// selected) just skips the disc.
        public void PlaceCreature(EditorCreatureKind kind, Vector2Int coord, int ownerId)
        {
            var worldPos = _grid.GridToWorld(coord);

            var root = new GameObject($"{kind}_{coord.x}_{coord.y}");
            root.transform.SetParent(transform, false);
            root.transform.position = worldPos;

            if (ownerId >= 0 && ownerId < _players.Count)
            {
                var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = "Owner";
                ring.transform.SetParent(root.transform, false);
                ring.transform.localPosition = new Vector3(0f, 0.03f, 0f);
                ring.transform.localScale = new Vector3(OwnerRingDiameter, OwnerRingThickness, OwnerRingDiameter);
                ring.GetComponent<Renderer>().material.color = _players[ownerId].Color;
                Destroy(ring.GetComponent<Collider>());
            }

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = Vector3.one * CreatureScale;
            body.transform.localPosition = Vector3.up * CreatureScale;
            body.GetComponent<Renderer>().material.color = SpeciesColors.TryGetValue(kind, out var color) ? color : Color.white;
            Destroy(body.GetComponent<Collider>());

            _creatures.Add(new PlacedCreature { Kind = kind, Coord = coord, OwnerId = ownerId, Visual = root });
        }

        /// Places a Core Room or Portal Room — unlike an ordinary room tool
        /// (see EditorPlaceRoomTile), this carves a fixed-size footprint
        /// (matching GameBootstrap's own starting layout) as Claimed floor
        /// owned by ownerId (or Unclaimed if no owner is selected, same
        /// fallback the Claimed Tile tool uses), then drops the real
        /// ChaosCore/Portal component on top to build its actual
        /// platform/staircase visual — see StructureKind's own header for
        /// why this reuses the live components instead of authoring plain
        /// tile data.
        public void PlaceStructure(StructureKind kind, Vector2Int center, int ownerId)
        {
            var halfSize = kind == StructureKind.CoreRoom ? CoreRoomHalfSize : PortalRoomHalfSize;
            var claimed = ownerId >= 0 && ownerId < _players.Count;

            for (int x = -halfSize; x <= halfSize; x++)
            {
                for (int y = -halfSize; y <= halfSize; y++)
                {
                    _grid.EditorPaintFloor(center + new Vector2Int(x, y), claimed, ownerId);
                }
            }

            var structureGO = new GameObject($"{kind}_{center.x}_{center.y}");
            structureGO.transform.SetParent(transform, false);

            if (kind == StructureKind.CoreRoom)
            {
                var chaosCore = structureGO.AddComponent<ChaosCore>();
                chaosCore.Initialize(center, _grid);
            }
            else
            {
                var portal = structureGO.AddComponent<Portal>();
                portal.Initialize(center, _grid);
            }

            _structures.Add(new PlacedStructure { Kind = kind, Coord = center, OwnerId = ownerId, Visual = structureGO });
        }

        /// Snapshots the current map/players/creatures into a LevelData
        /// ready for LevelFileIO.Save — skips every tile that's still
        /// plain, untouched Rock (see IsDefaultRock) so a mostly-empty map
        /// doesn't save MapWidth*MapHeight entries for nothing.
        public LevelData BuildLevelData()
        {
            var data = new LevelData
            {
                MapWidth = MapWidth,
                MapHeight = MapHeight,
                Multiplayer = Multiplayer
            };

            foreach (var player in _players)
            {
                data.Players.Add(new LevelPlayerData
                {
                    IsAI = player.IsAI,
                    ColorIndex = player.ColorIndex,
                    StartingGold = player.StartingGold,
                    StartingMana = player.StartingMana
                });
            }

            for (int x = 0; x < MapWidth; x++)
            {
                for (int y = 0; y < MapHeight; y++)
                {
                    var coord = new Vector2Int(x, y);
                    var tile = _grid.GetTile(coord);
                    if (IsDefaultRock(tile))
                    {
                        continue;
                    }

                    data.Tiles.Add(new LevelTileData
                    {
                        X = x,
                        Y = y,
                        Type = tile.Type,
                        Ownership = tile.Ownership,
                        OwnerId = tile.OwnerId,
                        IsReinforced = tile.IsReinforced,
                        IsBedrock = tile.IsBedrock,
                        WallResourceType = tile.WallResourceType,
                        RoomId = tile.RoomId
                    });
                }
            }

            foreach (var creature in _creatures)
            {
                data.Creatures.Add(new LevelCreatureData
                {
                    Kind = creature.Kind,
                    X = creature.Coord.x,
                    Y = creature.Coord.y,
                    OwnerId = creature.OwnerId
                });
            }

            foreach (var structure in _structures)
            {
                data.Structures.Add(new LevelStructureData
                {
                    Kind = structure.Kind,
                    X = structure.Coord.x,
                    Y = structure.Coord.y,
                    OwnerId = structure.OwnerId
                });
            }

            return data;
        }

        private static bool IsDefaultRock(TileState tile)
        {
            return tile.Type == TileType.Rock && !tile.IsBedrock && !tile.IsReinforced && tile.WallResourceType == WallResourceType.None;
        }

        /// Restores every saved tile/creature onto this session's grid —
        /// call after InitializeFromSave has already sized the grid and
        /// set up the player roster. Replays through the same Editor*
        /// methods the live editing tools use (see
        /// LevelDesignerInteractionController) rather than writing tile
        /// state directly, so decorations (gold nuggets, chasm spikes, ...)
        /// get rebuilt correctly too instead of just the raw flags.
        public void ApplyLevelData(LevelData data)
        {
            foreach (var tileData in data.Tiles)
            {
                RestoreTile(new Vector2Int(tileData.X, tileData.Y), tileData);
            }

            foreach (var structureData in data.Structures)
            {
                // Re-paints its own footprint on top of what the tile loop
                // above already restored — redundant but harmless (same
                // EditorPaintFloor call either way), simpler than special-
                // casing which tiles "belong" to a structure.
                PlaceStructure(structureData.Kind, new Vector2Int(structureData.X, structureData.Y), structureData.OwnerId);
            }

            foreach (var creatureData in data.Creatures)
            {
                PlaceCreature(creatureData.Kind, new Vector2Int(creatureData.X, creatureData.Y), creatureData.OwnerId);
            }
        }

        private void RestoreTile(Vector2Int coord, LevelTileData data)
        {
            switch (data.Type)
            {
                case TileType.Water:
                case TileType.Lava:
                case TileType.Chasm:
                case TileType.HolyGround:
                    _grid.EditorPaintTerrain(coord, data.Type);
                    break;
                case TileType.Floor:
                    _grid.EditorPaintFloor(coord, data.Ownership == TileOwnership.Claimed, data.OwnerId);
                    if (!string.IsNullOrEmpty(data.RoomId))
                    {
                        // EditorPlaceRoomTile only forces Unclaimed on a
                        // tile that isn't already Floor (see its own
                        // header) — since EditorPaintFloor just made it
                        // Floor with the exact saved ownership, that
                        // ownership survives the room tag being applied.
                        _grid.EditorPlaceRoomTile(coord, data.RoomId);
                    }
                    break;
                case TileType.Rock:
                    if (data.IsBedrock)
                    {
                        _grid.EditorPaintWall(coord, EditorWallVariant.Bedrock);
                    }
                    else if (data.IsReinforced)
                    {
                        _grid.EditorPaintWall(coord, EditorWallVariant.Reinforced);
                    }
                    else if (data.WallResourceType != WallResourceType.None)
                    {
                        _grid.EditorPaintWall(coord, ToEditorWallVariant(data.WallResourceType));
                    }
                    break;
            }
        }

        private static EditorWallVariant ToEditorWallVariant(WallResourceType wallResourceType)
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

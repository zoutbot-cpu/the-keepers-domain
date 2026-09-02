using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Implings;
using KeepersDomain.Input;
using KeepersDomain.Monsters;
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
    /// EditorPlaceRoomTile), these reuse the real ThroneRoom/Portal
    /// components directly (see PlaceStructure), since both are already
    /// self-contained (Initialize just needs a coord + the grid) and
    /// building their platform/staircase visuals from scratch here would
    /// only duplicate that code.
    public enum StructureKind
    {
        ThroneRoom,
        PortalRoom
    }

    /// One structure placed via the level designer's Rooms menu — see
    /// StructureKind's own header for why this reuses the live
    /// ThroneRoom/Portal components rather than being pure authored data
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

        // Matches GameBootstrap's own ThroneRoomHalfSize/
        // PortalRoomHalfSize exactly, so a structure placed here looks
        // and sizes identically to the fixed starting one BuildWorld
        // carves — 5x5 for the Throne Room, 3x3 for the Portal Room.
        private const int ThroneRoomHalfSize = 2;
        private const int PortalRoomHalfSize = 1;

        private DungeonGrid _grid;
        private readonly List<LevelDesignerPlayer> _players = new List<LevelDesignerPlayer>();
        private readonly List<PlacedCreature> _creatures = new List<PlacedCreature>();
        private readonly List<PlacedStructure> _structures = new List<PlacedStructure>();

        // The 8 player-buildable room managers (see GameBootstrap.
        // CreateLevelDesignerRoomManagers), keyed by which Rooms-menu tool
        // owns each — used by ApplyLevelData to rebuild a saved room's real
        // decoration instead of DungeonGrid.EditorPlaceRoomTile's bare
        // placeholder cube. Null only if this session predates that wiring
        // (shouldn't happen outside old/edge-case call sites) — every
        // dispatch site below falls back to today's placeholder-tagging
        // behavior when a lookup misses, so a null/empty dictionary just
        // means every room loads as a placeholder, not a crash.
        private Dictionary<RoomDesignTool, IRestorableRoomManager> _roomManagers;

        public int MapWidth { get; private set; }
        public int MapHeight { get; private set; }
        public bool Multiplayer { get; private set; }

        public IReadOnlyList<LevelDesignerPlayer> Players => _players;
        public IReadOnlyList<PlacedCreature> Creatures => _creatures;
        public IReadOnlyList<PlacedStructure> Structures => _structures;

        public void Initialize(DungeonGrid grid, LevelDesignerProperties properties, Dictionary<RoomDesignTool, IRestorableRoomManager> roomManagers)
        {
            _grid = grid;
            _roomManagers = roomManagers;
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
        public void InitializeFromSave(DungeonGrid grid, LevelData data, Dictionary<RoomDesignTool, IRestorableRoomManager> roomManagers)
        {
            _grid = grid;
            _roomManagers = roomManagers;
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

        /// Public so LevelDesignerMenuBar can call this the moment a
        /// player's color swatch is changed — without it, DungeonGrid's
        /// cached OwnerColors array (and every already-painted Claimed
        /// tile's baked-in tint) stays stale until the level is reloaded,
        /// which re-populates it via InitializeFromSave. Sets
        /// TintFloorByOwner true every time (harmless if already true) —
        /// this is the one and only place that opts a grid into Claimed-
        /// floor owner-tinting at all; ordinary gameplay never calls this,
        /// so its floor stays untinted regardless of OwnerColors.
        public void RefreshGridOwnerColors()
        {
            var colors = new Color[_players.Count];
            for (int i = 0; i < _players.Count; i++)
            {
                colors[i] = _players[i].Color;
            }

            _grid.OwnerColors = colors;
            _grid.TintFloorByOwner = true;
            _grid.RefreshAllVisuals();
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
            var visual = BuildCreatureVisual(kind, coord, ownerId);
            _creatures.Add(new PlacedCreature { Kind = kind, Coord = coord, OwnerId = ownerId, Visual = visual });
        }

        /// The actual capsule+ring GameObject build PlaceCreature uses —
        /// pulled out so SetCreatureOwner (edit mode) can rebuild the same
        /// visual for a reassigned creature without duplicating this.
        private GameObject BuildCreatureVisual(EditorCreatureKind kind, Vector2Int coord, int ownerId)
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

            return root;
        }

        /// Finds the index (into _creatures) of a placed creature at
        /// coord, if any — used by the Level Designer's edit mode to
        /// select/reassign an already-placed creature.
        public bool TryFindCreatureAt(Vector2Int coord, out int index)
        {
            for (int i = 0; i < _creatures.Count; i++)
            {
                if (_creatures[i].Coord == coord)
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }

        /// Reassigns the creature at index to a new owner, rebuilding its
        /// visual (the owner-ring disc) via the same BuildCreatureVisual
        /// PlaceCreature itself uses, so the two always stay consistent.
        public void SetCreatureOwner(int index, int ownerId)
        {
            var creature = _creatures[index];
            if (creature.Visual != null)
            {
                Destroy(creature.Visual);
            }

            creature.OwnerId = ownerId;
            creature.Visual = BuildCreatureVisual(creature.Kind, creature.Coord, ownerId);
            _creatures[index] = creature;
        }

        /// Deletes the placed creature at coord (see the Level Designer's
        /// Remove tool) — tears down its visual marker and drops it from
        /// the authored roster. No-ops if nothing's there.
        public bool RemoveCreatureAt(Vector2Int coord)
        {
            if (!TryFindCreatureAt(coord, out var index))
            {
                return false;
            }

            var creature = _creatures[index];
            if (creature.Visual != null)
            {
                Destroy(creature.Visual);
            }
            _creatures.RemoveAt(index);
            return true;
        }

        /// Scans every live creature agent currently in the scene (each
        /// species' own static All registry — ImplingAgent.All,
        /// GremlinAgent.All, ...) and adds one PlaceCreature marker per
        /// instance, so a snapshot taken via BuildLevelData (see
        /// GameBootstrap.SaveStartingLevelAsLevel1) actually captures
        /// what's alive on the map instead of only whatever was placed
        /// through this session's own interactive tool. Each agent's owning
        /// player is read straight off its Creature.OwnerId (see
        /// Creature.SetOwner) — in ordinary single-player gameplay that's 0
        /// for everything, the same value SaveStartingLevelAsLevel1 records
        /// for the Throne Room/Portal Room structures. Coord is derived from
        /// each agent's world Position (none of them expose a grid coord
        /// directly) via _grid.WorldToGrid.
        public void CaptureLiveCreatures()
        {
            foreach (var agent in ImplingAgent.All)
            {
                PlaceCreature(EditorCreatureKind.Imp, _grid.WorldToGrid(agent.Position), agent.Creature.OwnerId);
            }

            foreach (var agent in GremlinAgent.All)
            {
                PlaceCreature(EditorCreatureKind.Gremlin, _grid.WorldToGrid(agent.Position), agent.Creature.OwnerId);
            }

            foreach (var agent in WarlockAgent.All)
            {
                PlaceCreature(EditorCreatureKind.Warlock, _grid.WorldToGrid(agent.Position), agent.Creature.OwnerId);
            }

            foreach (var agent in MazeRattlerAgent.All)
            {
                PlaceCreature(EditorCreatureKind.MazeRattler, _grid.WorldToGrid(agent.Position), agent.Creature.OwnerId);
            }

            foreach (var agent in BeanCounterAgent.All)
            {
                PlaceCreature(EditorCreatureKind.BeanCounter, _grid.WorldToGrid(agent.Position), agent.Creature.OwnerId);
            }

            foreach (var agent in ElfAgent.All)
            {
                PlaceCreature(EditorCreatureKind.Elf, _grid.WorldToGrid(agent.Position), agent.Creature.OwnerId);
            }
        }

        /// Places a Throne Room or Portal Room — unlike an ordinary room tool
        /// (see EditorPlaceRoomTile), this carves a fixed-size footprint
        /// (matching GameBootstrap's own starting layout) as Claimed floor
        /// owned by ownerId (or Unclaimed if no owner is selected, same
        /// fallback the Claimed Tile tool uses), then drops the real
        /// ThroneRoom/Portal component on top to build its actual
        /// platform/staircase visual — see StructureKind's own header for
        /// why this reuses the live components instead of authoring plain
        /// tile data.
        public void PlaceStructure(StructureKind kind, Vector2Int center, int ownerId)
        {
            var halfSize = StructureHalfSize(kind);
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

            if (kind == StructureKind.ThroneRoom)
            {
                var throneRoom = structureGO.AddComponent<ThroneRoom>();
                throneRoom.Initialize(center, _grid);
            }
            else
            {
                var portal = structureGO.AddComponent<Portal>();
                portal.Initialize(center, _grid);
            }

            _structures.Add(new PlacedStructure { Kind = kind, Coord = center, OwnerId = ownerId, Visual = structureGO });
        }

        /// Finds the index (into _structures) of a placed structure at
        /// coord, if any — used by the Level Designer's edit mode to
        /// select/reassign an already-placed Core/Portal Room.
        public bool TryFindStructureAt(Vector2Int coord, out int index)
        {
            for (int i = 0; i < _structures.Count; i++)
            {
                if (_structures[i].Coord == coord)
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }

        /// Reassigns the structure at index to a new owner — re-claims
        /// its whole footprint (same halfSize/EditorPaintFloor loop
        /// PlaceStructure itself uses) so the floor underneath stays
        /// consistent with the structure's own recorded OwnerId. ThroneRoom/
        /// Portal's own visual doesn't currently re-tint itself after
        /// Initialize runs (ThroneRoom.PlayerColor is a bare field, only
        /// read once while building its throne — see its own comment), so
        /// a reassigned Throne Room's orb keeps showing whatever color it
        /// was built with until the level reloads — a real fix needs
        /// ThroneRoom/Portal to support rebuilding their visual post-hoc,
        /// out of scope here.
        public void SetStructureOwner(int index, int ownerId)
        {
            var structure = _structures[index];
            var halfSize = StructureHalfSize(structure.Kind);
            var claimed = ownerId >= 0 && ownerId < _players.Count;

            for (int x = -halfSize; x <= halfSize; x++)
            {
                for (int y = -halfSize; y <= halfSize; y++)
                {
                    _grid.EditorPaintFloor(structure.Coord + new Vector2Int(x, y), claimed, ownerId);
                }
            }

            structure.OwnerId = ownerId;
            _structures[index] = structure;
        }

        private static int StructureHalfSize(StructureKind kind)
        {
            return kind == StructureKind.ThroneRoom ? ThroneRoomHalfSize : PortalRoomHalfSize;
        }

        /// Deletes the placed structure whose fixed footprint covers coord
        /// (see the Level Designer's Remove tool) — unlike TryFindStructureAt
        /// (edit mode, exact-centre only) a tap anywhere on the Throne/Portal
        /// Room's tiles counts, since the author is pointing at the whole
        /// object, not one tile of it. Destroys the live ThroneRoom/Portal
        /// GameObject (its platform/staircase visuals are all parented to
        /// it, so they go with it) and resets the footprint back to plain
        /// Rock. No-ops if no structure covers coord.
        public bool RemoveStructureAt(Vector2Int coord)
        {
            for (int i = 0; i < _structures.Count; i++)
            {
                var structure = _structures[i];
                var halfSize = StructureHalfSize(structure.Kind);
                if (Mathf.Abs(coord.x - structure.Coord.x) > halfSize || Mathf.Abs(coord.y - structure.Coord.y) > halfSize)
                {
                    continue;
                }

                if (structure.Visual != null)
                {
                    Destroy(structure.Visual);
                }

                for (int x = -halfSize; x <= halfSize; x++)
                {
                    for (int y = -halfSize; y <= halfSize; y++)
                    {
                        _grid.EditorResetToRock(structure.Coord + new Vector2Int(x, y));
                    }
                }

                _structures.RemoveAt(i);
                return true;
            }

            return false;
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
            // Room tiles are deferred rather than tagged immediately (see
            // RestoreTile) — collected here, grouped by their saved
            // RoomId, so RestoreRooms can rebuild each one's real
            // decoration in a single call once every tile's ownership is
            // painted, instead of the bare placeholder cube every room
            // used to load as.
            var roomFootprints = new Dictionary<string, List<Vector2Int>>();
            var roomOwners = new Dictionary<string, int>();

            foreach (var tileData in data.Tiles)
            {
                RestoreTile(new Vector2Int(tileData.X, tileData.Y), tileData, roomFootprints, roomOwners);
            }

            RestoreRooms(roomFootprints, roomOwners);

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

        private void RestoreTile(Vector2Int coord, LevelTileData data, Dictionary<string, List<Vector2Int>> roomFootprints, Dictionary<string, int> roomOwners)
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
                        if (!roomFootprints.TryGetValue(data.RoomId, out var footprint))
                        {
                            footprint = new List<Vector2Int>();
                            roomFootprints[data.RoomId] = footprint;
                            roomOwners[data.RoomId] = data.OwnerId;
                        }
                        footprint.Add(coord);
                    }
                    break;
                case TileType.Rock:
                    if (data.IsBedrock)
                    {
                        _grid.EditorPaintWall(coord, EditorWallVariant.Bedrock);
                    }
                    else if (data.IsReinforced)
                    {
                        _grid.EditorPaintWall(coord, EditorWallVariant.Reinforced, data.OwnerId);
                    }
                    else if (data.WallResourceType != WallResourceType.None)
                    {
                        _grid.EditorPaintWall(coord, ToEditorWallVariant(data.WallResourceType));
                    }
                    break;
            }
        }

        /// Rebuilds every saved room's real decoration from its grouped
        /// tile footprint (see RestoreTile/ApplyLevelData) — thin wrapper
        /// over the shared RoomReconstruction.RestoreRooms (also used by
        /// GameBootstrap's "Start Game loads level1" gameplay path), so
        /// the two don't drift out of sync with separate copies.
        private void RestoreRooms(Dictionary<string, List<Vector2Int>> roomFootprints, Dictionary<string, int> roomOwners)
        {
            RoomReconstruction.RestoreRooms(_grid, roomFootprints, roomOwners, _roomManagers);
        }

        private static EditorWallVariant ToEditorWallVariant(WallResourceType wallResourceType)
        {
            return RoomReconstruction.ToEditorWallVariant(wallResourceType);
        }
    }
}

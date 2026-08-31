using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.CameraControl;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Rooms;
using KeepersDomain.Implings;
using KeepersDomain.Monsters;
using KeepersDomain.UI;

namespace KeepersDomain.Core
{
    /// Builds the entire Phase 1 prototype procedurally on Play. Nothing needs
    /// to be hand-wired in the scene — this is the single source of truth for
    /// how the systems (grid, camera, rooms, implings) plug together.
    public static class GameBootstrap
    {
        // +50% over the original 24x24 prototype size, to give ongoing
        // development more room to work with.
        private const int GridWidth = 36;
        private const int GridHeight = 36;
        private const float CellSize = 1f;

        // 5x5 room, so the 3x3 Throne Room structure sits centered with a
        // 1-tile walkable margin around it.
        private const int ThroneRoomHalfSize = 2;

        // 3x3 room around the portal — bigger than a single tile so the
        // staircase reads as sitting in an actual room, not just a corridor cell.
        private const int PortalRoomHalfSize = 1;

        // 3x3 Treasury room, mirroring the Portal's "own room off a
        // one-tile corridor" shape but placed on Throne Room's north side so
        // it doesn't collide with the Portal's east-side layout.
        private const int TreasuryRoomHalfSize = 1;

        // Starting Library, chained off Treasury's east side via its own
        // one-tile corridor (see the corridor/origin math in Init()) —
        // pre-filled with Library tiles rather than left as empty claimed
        // floor.
        private const int LibraryRoomWidth = 5;
        private const int LibraryRoomHeight = 4;

        // Starting Training Room, chained further east off the Library's
        // own east side the same one-tile-corridor way — pre-filled with
        // Training Room tiles.
        private const int TrainingRoomStartWidth = 4;
        private const int TrainingRoomStartHeight = 3;

        // 1000 to start — generous on purpose, not yet balanced (per-cost
        // tuning is a later pass). +500 on top of that for now to make
        // testing easier; back it out once real costs exist to test against.
        private const int StartingGold = 1500;

        // How many Gremlins this map's Portal starts with in its
        // recruitable pool — per-map pool data doesn't exist yet (see
        // Portal.SeedPool), so this is just seeded directly for now.
        private const int StartingGremlinPoolCount = 10;

        // Same idea, for Warlocks — 10 to start, per the brief.
        private const int StartingWarlockPoolCount = 10;

        // Same idea again, for Maze Rattlers — 5 to start, per the brief.
        private const int StartingMazeRattlerPoolCount = 5;

        // Same idea again, for Bean Counters — 5 to start, matching Maze
        // Rattler's own starting count (no design-brief value exists yet).
        private const int StartingBeanCounterPoolCount = 5;

        // Starting Lair/Slime Hatchery/Tavern, each their own 4x4
        // room chained off Throne Room the same one-tile-corridor way as
        // Treasury/Library/Training Room — see CarveStartingUtilityRooms.
        // 4x4 satisfies Tavern's own MinFootprintSize exactly and
        // clears Slime Hatchery's smaller MinFootprintSize with room to
        // spare; Lair has no minimum at all.
        private const int StartingUtilityRoomSize = 4;

        // Resource-wall scatter density — rolled once per Rock tile at
        // level-gen (ScatterResourceWalls). A fixed seed keeps the layout
        // reproducible across Play sessions, which is worth more than true
        // randomness for a prototype that's still being debugged.
        private const int ResourceScatterSeed = 918273;
        private const float ManaCrystalWallChance = 0.025f;
        private const float RegeneratingGoldWallChance = 0.012f;
        private const float GoldWallChance = 0.04f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            // ShowMainMenu clears any stray camera itself (see its own
            // comment) — no need to do it again here.
            ShowMainMenu();
        }

        /// Called from BottomMenuBar's own "Main Menu" button — tears down
        /// the entire running game (every root object BuildWorld created:
        /// grid, camera, managers, every creature) and shows the main menu
        /// again, same as a fresh launch. Nothing here needs special
        /// per-system cleanup beyond that: every creature agent already
        /// removes itself from its own static roster in OnDestroy (see e.g.
        /// ImplingAgent.OnDestroy), and nothing else in this prototype holds
        /// state that outlives its GameObject.
        public static void ReturnToMainMenu()
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                Object.Destroy(root);
            }

            ShowMainMenu();
        }

        /// First thing the player sees — the dungeon isn't built at all yet
        /// (see BuildWorld), just a logo and Start/Quit over a plain
        /// background, so a camera still needs to exist for the clear color.
        /// Real world-building only starts once Start is pressed.
        private static void ShowMainMenu()
        {
            // Clears out whatever camera the previous screen was using
            // (BuildWorld's iso camera on a "Main Menu" bounce-back, or this
            // same menu's own camera on a "Back" from Level Designer
            // properties) before creating a fresh one below.
            RemoveStrayCameras();

            var menuCameraGO = new GameObject("Menu Camera");
            menuCameraGO.tag = "MainCamera";
            var menuCamera = menuCameraGO.AddComponent<Camera>();
            menuCamera.orthographic = true;
            menuCamera.clearFlags = CameraClearFlags.SolidColor;
            menuCamera.backgroundColor = new Color(0.05f, 0.05f, 0.07f);

            var menu = CreateComponent<MainMenu>("MainMenu");
            menu.Initialize(StartGame, ShowLevelDesignerProperties);
        }

        /// The Start Game button's actual callback — loads "level1" if
        /// it exists (see BuildWorld's own header for what changes when
        /// it does) rather than always generating a fresh procedural map,
        /// so a level saved/edited via the Level Designer is what
        /// gameplay actually starts on. Falls back to BuildWorld's
        /// from-scratch generation (which auto-saves its own output as
        /// "level1" — see SaveStartingLevelAsLevel1) only on a truly
        /// fresh install with no save yet.
        private static void StartGame()
        {
            BuildWorld(LevelFileIO.Load("level1"));
        }

        /// Reached via the main menu's "Level Designer" button — collects
        /// the up-front properties (player count, map size) the actual
        /// editor world (see BuildLevelDesignerWorld) gets created with, or
        /// lets the player skip that and load a previously saved level
        /// straight in instead (see LevelDesignerPropertiesMenu's own Load
        /// Existing Level list). Reuses the menu camera ShowMainMenu
        /// already created rather than making its own.
        private static void ShowLevelDesignerProperties()
        {
            var propertiesMenu = CreateComponent<LevelDesignerPropertiesMenu>("LevelDesignerPropertiesMenu");
            propertiesMenu.Initialize(ShowMainMenu, BuildLevelDesignerWorld, LoadLevelDesignerWorld);
        }

        /// Builds the Level Designer's own world — a blank map at the
        /// chosen size (all Rock, Bedrock border) plus its 6-menu editor
        /// UI. Lighter than BuildWorld's gameplay setup — no
        /// BuilderJobBoard, no creature spawners, no starting gold/gold
        /// costs — but the 8 player-buildable room managers (see
        /// CreateLevelDesignerRoomManagers) ARE created here, gold-free,
        /// purely so the Rooms menu tool and a loaded save can place real
        /// room decorations (carpet, nest, bookcases, dummies, coop,
        /// shrine, bench, pit/fence) instead of DungeonGrid.
        /// EditorPlaceRoomTile's bare placeholder-colored cube. Everything
        /// else (terrain/wall/floor/structure/creature tools) still
        /// authors tile data directly through DungeonGrid/
        /// LevelDesignerSession, since no other gameplay job-queue/economy
        /// system exists at level-design time.
        private static void BuildLevelDesignerWorld(LevelDesignerProperties properties)
        {
            RemoveStrayCameras();
            CreateSun();

            var grid = CreateComponent<DungeonGrid>("DungeonGrid");
            grid.Initialize(properties.MapWidth, properties.MapHeight, CellSize);

            // Border Bedrock — every tile starts as plain Rock (see
            // DungeonGrid.Initialize), so SetBedrock's "must be plain
            // Rock" guard is already satisfied for all of them.
            for (int x = 0; x < properties.MapWidth; x++)
            {
                grid.SetBedrock(new Vector2Int(x, 0));
                grid.SetBedrock(new Vector2Int(x, properties.MapHeight - 1));
            }
            for (int y = 0; y < properties.MapHeight; y++)
            {
                grid.SetBedrock(new Vector2Int(0, y));
                grid.SetBedrock(new Vector2Int(properties.MapWidth - 1, y));
            }

            var roomManagers = CreateLevelDesignerRoomManagers(grid);

            var session = CreateComponent<LevelDesignerSession>("LevelDesignerSession");
            session.Initialize(grid, properties, roomManagers);

            // Only on a brand-new blank map — LoadLevelDesignerWorld
            // restores exactly what was saved instead, no extras injected.
            CreateLevelDesignerTestRooms(grid);

            SetUpLevelDesignerWorld(grid, session, initialLevelName: null, roomManagers);
        }

        /// Creates and wires the 8 player-buildable room managers (every
        /// RoomDesignTool value except None — BridgeManager isn't one of
        /// them, see RoomDesignTool's own comment) for the Level Designer,
        /// shared by BuildLevelDesignerWorld and LoadLevelDesignerWorld.
        /// Same Initialize wiring BuildWorld uses, minus anything
        /// gameplay-only that the Level Designer has no business running:
        /// - No starting gold/PlaceStartingTreasury call — rooms placed
        ///   here are always gold-free anyway (see each manager's own
        ///   RestoreRoom).
        /// - SlimeHatcheryManager gets simulateBreeding: false so placing/
        ///   loading a Hatchery never starts spawning live SlimeAgents
        ///   while the map is just being edited.
        /// - JailManager gets a null BuilderJobBoard (the Level Designer
        ///   has no dig-job queue, and BuilderJobBoard.Update auto-queues
        ///   real reinforce jobs across the whole grid, which the Level
        ///   Designer must never do in the background) — safe because its
        ///   only use is guarded (see JailManager.Initialize's own
        ///   comment).
        /// - ConversionClassManager gets null JailManager-linked prisoner
        ///   release and null creature spawners — already all null-safe
        ///   internally, and none of that behavior is reachable without a
        ///   live gameplay loop feeding it.
        /// Returned as a RoomDesignTool -> manager lookup, the same shape
        /// both LevelDesignerInteractionController's live Rooms tool and
        /// LevelDesignerSession's save/load path need.
        private static Dictionary<RoomDesignTool, IRestorableRoomManager> CreateLevelDesignerRoomManagers(DungeonGrid grid)
        {
            var lairManager = CreateComponent<LairManager>("LairManager");
            var treasuryManager = CreateComponent<TreasuryManager>("TreasuryManager");
            treasuryManager.Initialize(grid, lairManager);
            lairManager.Initialize(grid, treasuryManager);

            var slimeHatcheryManager = CreateComponent<SlimeHatcheryManager>("SlimeHatcheryManager");
            slimeHatcheryManager.Initialize(grid, lairManager, treasuryManager, simulateBreeding: false);

            var tavernManager = CreateComponent<TavernManager>("TavernManager");
            tavernManager.Initialize(grid, lairManager, treasuryManager);

            var trainingRoomManager = CreateComponent<TrainingRoomManager>("TrainingRoomManager");
            trainingRoomManager.Initialize(grid, lairManager, treasuryManager);

            var libraryManager = CreateComponent<LibraryManager>("LibraryManager");
            libraryManager.Initialize(grid, lairManager, treasuryManager);

            var jailManager = CreateComponent<JailManager>("JailManager");
            jailManager.Initialize(grid, jobBoard: null, lairManager, treasuryManager);

            var conversionClassManager = CreateComponent<ConversionClassManager>("ConversionClassManager");
            conversionClassManager.Initialize(grid, lairManager, treasuryManager, jailManager,
                gremlinSpawner: null, warlockSpawner: null, mazeRattlerSpawner: null, elfSpawner: null);

            return new Dictionary<RoomDesignTool, IRestorableRoomManager>
            {
                { RoomDesignTool.Lair, lairManager },
                { RoomDesignTool.Treasury, treasuryManager },
                { RoomDesignTool.SlimeHatchery, slimeHatcheryManager },
                { RoomDesignTool.Tavern, tavernManager },
                { RoomDesignTool.TrainingRoom, trainingRoomManager },
                { RoomDesignTool.Library, libraryManager },
                { RoomDesignTool.Jail, jailManager },
                { RoomDesignTool.ConversionClass, conversionClassManager },
            };
        }

        // 1-tile floor ringed by 1 tile of wall (3x3 footprint per room),
        // 1-tile gaps between rings so they read as 3 separate structures
        // rather than fusing together — see CreateLevelDesignerTestRooms.
        private const int TestRoomSpacing = 4;

        /// A quick side-by-side comparison of wall rendering, dropped at
        /// the center of every fresh Level Designer map: a Claimed room
        /// with Reinforced walls, an Unclaimed room with Reinforced walls,
        /// and a Claimed room with plain walls — enough to see the
        /// Reinforced-only KayKit autotiling (see DungeonGrid.
        /// RefreshVisual's needsWallMesh/IsWallNeighbor) against ordinary
        /// cube walls without having to hand-paint anything first.
        private static void CreateLevelDesignerTestRooms(DungeonGrid grid)
        {
            var center = new Vector2Int(grid.Width / 2, grid.Height / 2);
            var claimedReinforcedCenter = center + new Vector2Int(-TestRoomSpacing, 0);
            var unclaimedReinforcedCenter = center;
            var claimedPlainCenter = center + new Vector2Int(TestRoomSpacing, 0);

            CreateTestRoom(grid, claimedReinforcedCenter, claimed: true, ownerId: 0, EditorWallVariant.Reinforced);
            CreateTestRoom(grid, unclaimedReinforcedCenter, claimed: false, ownerId: -1, EditorWallVariant.Reinforced);
            CreateTestRoom(grid, claimedPlainCenter, claimed: true, ownerId: 0, EditorWallVariant.Plain);
        }

        /// One 1x1 floor tile plus the 8 tiles ringing it, painted as
        /// wallVariant — see CreateLevelDesignerTestRooms.
        private static void CreateTestRoom(DungeonGrid grid, Vector2Int center, bool claimed, int ownerId, EditorWallVariant wallVariant)
        {
            grid.EditorPaintFloor(center, claimed, ownerId);

            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    if (x == 0 && y == 0)
                    {
                        continue;
                    }

                    grid.EditorPaintWall(center + new Vector2Int(x, y), wallVariant);
                }
            }
        }

        /// Reached from the Level Designer's own Save/Load menu — tears
        /// down whatever's currently running (same full-scene teardown
        /// ReturnToMainMenu uses) and rebuilds the editor world from a
        /// previously saved LevelData instead of a blank map: every
        /// non-default tile, the saved player roster, and every placed
        /// creature are restored by LevelDesignerSession.ApplyLevelData.
        private static void LoadLevelDesignerWorld(string levelName, LevelData data)
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                Object.Destroy(root);
            }

            RemoveStrayCameras();
            CreateSun();

            var grid = CreateComponent<DungeonGrid>("DungeonGrid");
            grid.Initialize(data.MapWidth, data.MapHeight, CellSize);

            var roomManagers = CreateLevelDesignerRoomManagers(grid);

            var session = CreateComponent<LevelDesignerSession>("LevelDesignerSession");
            session.InitializeFromSave(grid, data, roomManagers);
            session.ApplyLevelData(data);

            SetUpLevelDesignerWorld(grid, session, levelName, roomManagers);
        }

        /// Shared by BuildLevelDesignerWorld/LoadLevelDesignerWorld once
        /// each has its own grid+session ready (blank vs. restored from a
        /// save) — camera, the interaction controller, and the 6-menu
        /// editor UI are identical either way.
        private static void SetUpLevelDesignerWorld(DungeonGrid grid, LevelDesignerSession session, string initialLevelName, Dictionary<RoomDesignTool, IRestorableRoomManager> roomManagers)
        {
            // Unlike BuildWorld's fixed 22.5 pan margin (tuned for the
            // gameplay grid's own fixed size), the editor's pan bounds
            // scale with the actual map footprint, padded enough to reach
            // every edge tile comfortably.
            var panMargin = Mathf.Max(grid.Width, grid.Height) * CellSize * 0.5f + 10f;
            var camera = CreateIsoCamera(grid, panMargin);

            var interactionController = CreateComponent<LevelDesignerInteractionController>("LevelDesignerInteractionController");
            interactionController.Initialize(camera, grid, session, roomManagers);

            var menuBar = CreateComponent<LevelDesignerMenuBar>("LevelDesignerMenuBar");
            menuBar.Initialize(session, interactionController, LoadLevelDesignerWorld, initialLevelName);
        }

        /// Everything that used to run directly out of Init() — deferred
        /// until the player presses Start on the main menu (see
        /// StartGame/ShowMainMenu), so the prototype no longer drops
        /// straight into the dungeon on launch. data null (the original,
        /// still-default behavior) means generate a fresh procedural map
        /// from scratch, same as always; non-null (see StartGame, which
        /// loads "level1") means reconstruct the saved map instead —
        /// tiles/walls/terrain restored directly, rooms restored through
        /// the same IRestorableRoomManager machinery the Level Designer's
        /// own load path uses (see RoomReconstruction), Core/Portal Room
        /// read from data.Structures, creatures spawned as real live
        /// agents (not the Level Designer's inert markers — this is
        /// actual gameplay). Every manager/spawner's own wiring (which
        /// references which, BuilderJobBoard, Portal pool seeding) never
        /// differs between the two — only how the world's initial shape
        /// gets populated does, so only those specific spots below branch
        /// on data; everything else runs unconditionally exactly as
        /// before.
        private static void BuildWorld(LevelData data = null)
        {
            // Clears out the menu camera created by ShowMainMenu — the real
            // iso camera below replaces it.
            RemoveStrayCameras();
            CreateSun();

            var grid = CreateComponent<DungeonGrid>("DungeonGrid");
            grid.Initialize(data != null ? data.MapWidth : GridWidth, data != null ? data.MapHeight : GridHeight, CellSize);
            // Placeholder until a real player-color selection screen
            // exists — green for now. Visible on the Reinforced wall
            // orb (DungeonGrid.PlayerColor) and ThroneRoom's own fallback
            // orb (ThroneRoom.PlayerColor, set below), kept in sync.
            grid.PlayerColor = Color.green;

            // Drives the dungeon_pack water/lava tiles' scroll/pulse —
            // see LiquidAnimator's own header for what it does and
            // doesn't attempt versus the pack's full README technique.
            // Independent of grid — finds its own shared materials via
            // Resources.Load, so it doesn't need a reference passed in.
            var liquidAnimator = CreateComponent<LiquidAnimator>("LiquidAnimator");
            liquidAnimator.Initialize();

            // Only populated (and only meaningful) when data != null —
            // see RestoreWorldTiles, called below.
            Dictionary<string, List<Vector2Int>> roomFootprints = null;
            Dictionary<string, int> roomOwners = null;

            Vector2Int throneRoomCenter;
            Vector2Int portalCoord;

            // Declared here (rather than only inside the else branch
            // below) so they're still in scope for the PlaceStartingX
            // calls further down, each individually guarded by
            // `data == null` — left at their default, unused value when
            // data != null, since that branch reconstructs every room
            // through RestoreWorldTiles + the RoomReconstruction dispatch
            // near the bottom of this method instead.
            var treasuryCoord = default(Vector2Int);
            var libraryRoomOrigin = default(Vector2Int);
            var libraryRoomEndCoord = default(Vector2Int);
            var trainingRoomStartOrigin = default(Vector2Int);
            var trainingRoomStartEndCoord = default(Vector2Int);
            var lairRoomOrigin = default(Vector2Int);
            var lairRoomEndCoord = default(Vector2Int);
            var hatcheryRoomOrigin = default(Vector2Int);
            var hatcheryRoomEndCoord = default(Vector2Int);
            var tavernRoomOrigin = default(Vector2Int);
            var tavernRoomEndCoord = default(Vector2Int);

            if (data != null)
            {
                RestoreWorldTiles(grid, data, out roomFootprints, out roomOwners);

                var fallbackThroneRoomCenter = new Vector2Int(grid.Width / 2, grid.Height / 2);
                throneRoomCenter = FindStructureCoordOrDefault(data, StructureKind.ThroneRoom, fallbackThroneRoomCenter);
                portalCoord = FindStructureCoordOrDefault(data, StructureKind.PortalRoom,
                    fallbackThroneRoomCenter + new Vector2Int(ThroneRoomHalfSize + PortalRoomHalfSize + 2, 0));
            }
            else
            {
                // Throne Room sits at the grid center; the portal gets its own
                // room to the east, joined by a single one-tile corridor.
                throneRoomCenter = new Vector2Int(GridWidth / 2, GridHeight / 2);
                var corridorCoord = throneRoomCenter + new Vector2Int(ThroneRoomHalfSize + 1, 0);
                portalCoord = corridorCoord + new Vector2Int(PortalRoomHalfSize + 1, 0);

                // Both rooms carve buildable by default, then re-carve just the
                // footprint that actually has a fixed structure on it (Throne
                // Room's 3x3 platform, the Portal's single staircase tile) back
                // to unbuildable — the walkable margin around each stays open
                // for the player's very first Lair. Without at least one
                // buildable tile from the start, there'd be no way to ever place
                // a first Lair (and so no first impling) since nothing exists
                // yet to dig new floor either.
                grid.CarveRoom(throneRoomCenter, ThroneRoomHalfSize);
                grid.CarveRoom(throneRoomCenter, 1, isBuildable: false);
                grid.CarveRoom(corridorCoord, 0, isBuildable: false);
                grid.CarveRoom(portalCoord, PortalRoomHalfSize);
                grid.CarveRoom(portalCoord, 0, isBuildable: false);

                // Treasury sits north of Throne Room, its own room off a
                // single-tile corridor. Carved buildable (unlike Throne
                // Room/Portal's fixed structure tiles) since the starting
                // Treasury is placed the same way a player-built one is — see
                // TreasuryManager.TryPlaceTreasury below — rather than being a
                // permanent landmark; only the corridor stays unbuildable, so
                // a room can never block the one path between the two rooms.
                var treasuryCorridorCoord = throneRoomCenter + new Vector2Int(0, ThroneRoomHalfSize + 1);
                treasuryCoord = treasuryCorridorCoord + new Vector2Int(0, TreasuryRoomHalfSize + 1);
                grid.CarveRoom(treasuryCoord, TreasuryRoomHalfSize);
                grid.CarveRoom(treasuryCorridorCoord, 0, isBuildable: false);

                // Library chains off Treasury's east side via its own one-tile
                // corridor — carved buildable (unlike Throne Room/Portal's fixed
                // structure tiles), same as Treasury itself, since it's about
                // to become a real, sellable Library room below rather than a
                // permanent landmark; only the corridor stays unbuildable.
                var libraryCorridorCoord = treasuryCoord + new Vector2Int(TreasuryRoomHalfSize + 1, 0);
                libraryRoomOrigin = libraryCorridorCoord + new Vector2Int(1, -2);
                libraryRoomEndCoord = libraryRoomOrigin + new Vector2Int(LibraryRoomWidth - 1, LibraryRoomHeight - 1);
                grid.CarveRect(libraryRoomOrigin, LibraryRoomWidth, LibraryRoomHeight);
                grid.CarveRoom(libraryCorridorCoord, 0, isBuildable: false);

                // Training Room chains further east off the Library's own east
                // side, same one-tile-corridor pattern.
                var trainingRoomStartCorridorCoord = libraryRoomOrigin + new Vector2Int(LibraryRoomWidth, 1);
                trainingRoomStartOrigin = trainingRoomStartCorridorCoord + new Vector2Int(1, -1);
                trainingRoomStartEndCoord = trainingRoomStartOrigin + new Vector2Int(TrainingRoomStartWidth - 1, TrainingRoomStartHeight - 1);
                grid.CarveRect(trainingRoomStartOrigin, TrainingRoomStartWidth, TrainingRoomStartHeight);
                grid.CarveRoom(trainingRoomStartCorridorCoord, 0, isBuildable: false);

                CarveStartingUtilityRooms(grid, throneRoomCenter,
                    out lairRoomOrigin, out lairRoomEndCoord,
                    out hatcheryRoomOrigin, out hatcheryRoomEndCoord,
                    out tavernRoomOrigin, out tavernRoomEndCoord);

                // Scatter resource-wall veins into whatever's still Rock now
                // that every starting room/corridor is carved to Floor — those
                // tiles are automatically skipped (ScatterResourceWalls only
                // ever touches Rock). Not needed when loading a save — every
                // resource-wall tile is already captured per-tile (see
                // LevelTileData.WallResourceType) and restored by
                // RestoreWorldTiles above.
                ScatterResourceWalls(grid);
            }

            var throneRoom = CreateComponent<ThroneRoom>("ThroneRoom");
            throneRoom.PlayerColor = grid.PlayerColor;
            throneRoom.Initialize(throneRoomCenter, grid);

            var portal = CreateComponent<Portal>("Portal");
            portal.Initialize(portalCoord, grid);

            var jobBoard = CreateComponent<BuilderJobBoard>("BuilderJobBoard");
            jobBoard.Initialize(grid);

            // LairManager and TreasuryManager each need a reference to the
            // other (Treasury subscribes to LairManager.RoomSold to clean
            // up a sold Treasury's gold/visuals; Lair charges its placement
            // cost out of TreasuryManager's reserves — see
            // LairManager.TryPlaceLair) — both components are created first,
            // then wired up in whichever order, since C# events and plain
            // field assignment don't require the other side's Initialize to
            // have run yet.
            var lairManager = CreateComponent<LairManager>("LairManager");
            var treasuryManager = CreateComponent<TreasuryManager>("TreasuryManager");
            treasuryManager.Initialize(grid, lairManager);
            lairManager.Initialize(grid, treasuryManager);

            if (data == null)
            {
                // The starting Treasury is placed exactly like a player-built
                // one — PlaceStartingTreasury, not a direct tile-registration
                // loop — so it's a real, sellable room from the moment the game
                // starts, not a permanent landmark. Unlike TryPlaceTreasury,
                // this skips the gold cost — it's terrain generation, not a
                // purchase, and there'd be no gold to pay it with yet anyway.
                // When data != null, the Treasury (like every other room) is
                // reconstructed instead by the RoomReconstruction dispatch
                // below, once every room manager exists.
                treasuryManager.PlaceStartingTreasury(
                    treasuryCoord - new Vector2Int(TreasuryRoomHalfSize, TreasuryRoomHalfSize),
                    treasuryCoord + new Vector2Int(TreasuryRoomHalfSize, TreasuryRoomHalfSize));
            }

            // Starting gold, spread across every tile the starting Treasury
            // just registered (AddGold, not Deposit — Deposit targets one
            // specific tile and caps at GoldCapacityPerTile, which silently
            // dropped everything past 500 when StartingGold grew past a
            // single tile's capacity). Read from the save's own player data
            // when loading one, falling back to the StartingGold constant
            // if that's somehow missing.
            var startingGold = data != null && data.Players.Count > 0 ? data.Players[0].StartingGold : StartingGold;
            treasuryManager.AddGold(startingGold);

            // Slime Hatchery/Tavern get a starting instance too (see
            // PlaceStartingHatchery/PlaceStartingTavern below, once the
            // utility-room footprints are carved) on top of being
            // player-placeable like Lair/Treasury — both subscribe to
            // LairManager.RoomSold the same way TreasuryManager does, and
            // charge their own per-tile cost out of TreasuryManager same as
            // LairManager, so they need both to exist first.
            var slimeHatcheryManager = CreateComponent<SlimeHatcheryManager>("SlimeHatcheryManager");
            slimeHatcheryManager.Initialize(grid, lairManager, treasuryManager);

            var tavernManager = CreateComponent<TavernManager>("TavernManager");
            tavernManager.Initialize(grid, lairManager, treasuryManager);

            // Training Room follows the same "player-placed, subscribes to
            // RoomSold" wiring as Hatchery/Tavern — placement and visuals
            // only for now, see TrainingRoomManager's own header comment.
            var trainingRoomManager = CreateComponent<TrainingRoomManager>("TrainingRoomManager");
            trainingRoomManager.Initialize(grid, lairManager, treasuryManager);

            // Library follows the same "player-placed, subscribes to
            // RoomSold" wiring as Hatchery/Tavern/Training Room — placement
            // and visuals only for now, see LibraryManager's own header
            // comment.
            var libraryManager = CreateComponent<LibraryManager>("LibraryManager");
            libraryManager.Initialize(grid, lairManager, treasuryManager);

            if (data == null)
            {
                // Both starting rooms are placed exactly like the starting
                // Treasury — PlaceStartingLibrary/PlaceStartingTrainingRoom, not
                // a direct tile-registration loop — so each is a real, sellable
                // room from the moment the game starts, filling the claimed
                // floor carved above with actual Library/Training Room tiles
                // rather than leaving it empty. Same as PlaceStartingTreasury,
                // both skip their usual gold cost — terrain generation, not a
                // purchase.
                libraryManager.PlaceStartingLibrary(libraryRoomOrigin, libraryRoomEndCoord);
                trainingRoomManager.PlaceStartingTrainingRoom(trainingRoomStartOrigin, trainingRoomStartEndCoord);
            }

            // Jail follows the same "player-placed, subscribes to
            // RoomSold" wiring as Hatchery/Tavern/Training Room/Library —
            // placement and visuals only for now (no Maze Rattler/capture
            // mechanic exists yet, see JailManager's own header comment).
            // No starting instance — unlike Library/Training Room, there's
            // no reason yet to force one into the starting domain.
            var jailManager = CreateComponent<JailManager>("JailManager");
            jailManager.Initialize(grid, jobBoard, lairManager, treasuryManager);

            if (data == null)
            {
                // Same "real, sellable room from the moment the game starts"
                // treatment as Treasury/Library/Training Room — placed into the
                // three utility-room footprints carved above (see
                // CarveStartingUtilityRooms), no gold cost since this is
                // terrain generation, not a purchase. The starting Lair is left
                // unclaimed, same as a player-placed one — nothing auto-claims
                // it — but its mere existence satisfies Gremlin's "at least one
                // free Lair" join requirement from the very start.
                lairManager.PlaceStartingLair(lairRoomOrigin, lairRoomEndCoord);
                slimeHatcheryManager.PlaceStartingHatchery(hatcheryRoomOrigin, hatcheryRoomEndCoord);
                tavernManager.PlaceStartingTavern(tavernRoomOrigin, tavernRoomEndCoord);
            }

            var implingSpawner = CreateComponent<ImplingSpawner>("ImplingSpawner");
            implingSpawner.Initialize(jobBoard, grid, treasuryManager, throneRoom, slimeHatcheryManager, tavernManager);

            // First non-Imp creature — recruited out of the Portal's pool
            // (see Portal.SeedPool/TryTakeFromPool), not placed freely; see
            // GremlinAgent/GremlinSpawner's own header comments for its
            // join requirements and priority-list AI.
            var gremlinSpawner = CreateComponent<GremlinSpawner>("GremlinSpawner");
            gremlinSpawner.Initialize(grid, portal, lairManager, slimeHatcheryManager, trainingRoomManager, tavernManager, treasuryManager);
            portal.SeedPool(GremlinAgent.CreatureKind, StartingGremlinPoolCount);

            // Second non-Imp creature, and the first "intelligent" one — see
            // WarlockAgent/WarlockSpawner's own header comments for its
            // extra join requirements (a Lair tile, a 3x3+ Library, and
            // Hatchery/Tavern capacity) on top of pool availability.
            var warlockSpawner = CreateComponent<WarlockSpawner>("WarlockSpawner");
            warlockSpawner.Initialize(grid, portal, lairManager, libraryManager, slimeHatcheryManager, tavernManager, trainingRoomManager, treasuryManager);
            portal.SeedPool(WarlockAgent.CreatureKind, StartingWarlockPoolCount);

            // Third non-Imp creature — see MazeRattlerAgent/MazeRattlerSpawner's
            // own header comments for its join requirement (a placed Jail,
            // 5 Maze Rattlers per Jail room) and its "haunt the prisoners"
            // idle-tier behavior.
            var mazeRattlerSpawner = CreateComponent<MazeRattlerSpawner>("MazeRattlerSpawner");
            mazeRattlerSpawner.Initialize(grid, portal, lairManager, jailManager, tavernManager, trainingRoomManager, treasuryManager);
            portal.SeedPool(MazeRattlerAgent.CreatureKind, StartingMazeRattlerPoolCount);

            // Elf is never recruited through the Portal (see ElfSpawner's
            // own header) — only ever created as Conversion Class's
            // torment-failure outcome — so it's created here with no
            // SeedPool call, just wired up so ConversionClassManager has
            // something to call SpawnElf on.
            var elfSpawner = CreateComponent<ElfSpawner>("ElfSpawner");
            elfSpawner.Initialize(grid, portal, lairManager, tavernManager, treasuryManager);

            // Conversion Class follows the same "player-placed, subscribes
            // to RoomSold" wiring as every other room — see
            // ConversionClassManager's own header for the bench/wall-board
            // visuals and the torment mechanic it owns.
            var conversionClassManager = CreateComponent<ConversionClassManager>("ConversionClassManager");
            conversionClassManager.Initialize(grid, lairManager, treasuryManager, jailManager, gremlinSpawner, warlockSpawner, mazeRattlerSpawner, elfSpawner);

            if (data != null && roomFootprints != null)
            {
                // Every room manager but Bridge now exists — reconstruct
                // every saved room (Treasury included, in place of
                // PlaceStartingTreasury above) through the same
                // IRestorableRoomManager dispatch the Level Designer's own
                // load path uses (see RoomReconstruction), so a loaded
                // game gets the exact same real decoration a fresh one
                // does, not a placeholder.
                var roomManagers = new Dictionary<RoomDesignTool, IRestorableRoomManager>
                {
                    { RoomDesignTool.Lair, lairManager },
                    { RoomDesignTool.Treasury, treasuryManager },
                    { RoomDesignTool.SlimeHatchery, slimeHatcheryManager },
                    { RoomDesignTool.Tavern, tavernManager },
                    { RoomDesignTool.TrainingRoom, trainingRoomManager },
                    { RoomDesignTool.Library, libraryManager },
                    { RoomDesignTool.Jail, jailManager },
                    { RoomDesignTool.ConversionClass, conversionClassManager },
                };
                RoomReconstruction.RestoreRooms(grid, roomFootprints, roomOwners, roomManagers);
            }

            // Bridge follows the same "player-placed, subscribes to
            // RoomSold" wiring as every other room manager — see
            // BridgeManager's own header for its line-paint placement and
            // Lava-decay mechanic.
            var bridgeManager = CreateComponent<BridgeManager>("BridgeManager");
            bridgeManager.Initialize(grid, lairManager, treasuryManager);

            // Fourth non-Imp creature — see BeanCounterAgent/
            // BeanCounterSpawner's own header comments for its join
            // requirement (a placed Conversion Class) and its "teach"
            // idle-tier behavior.
            var beanCounterSpawner = CreateComponent<BeanCounterSpawner>("BeanCounterSpawner");
            beanCounterSpawner.Initialize(grid, portal, lairManager, conversionClassManager, jailManager, tavernManager, treasuryManager);
            portal.SeedPool(BeanCounterAgent.CreatureKind, StartingBeanCounterPoolCount);

            // Every spawner now exists — restore each saved creature as a
            // real live agent (not the Level Designer's inert marker, see
            // RoomReconstruction's own header — this is actual gameplay),
            // or fall back to the fixed starting-Impling spawn when
            // there's no save to restore from.
            if (data != null)
            {
                RestoreWorldCreatures(data, implingSpawner, gremlinSpawner, warlockSpawner, mazeRattlerSpawner, beanCounterSpawner, elfSpawner);
            }
            else
            {
                SpawnStartingImplings(implingSpawner, throneRoomCenter);
            }

            // Pan margin scaled by the same +50% as GridWidth/GridHeight
            // (15f base -> 22.5f), so the camera can still reach the whole
            // enlarged map instead of the old grid's bounds.
            var camera = CreateIsoCamera(grid, 22.5f);

            var minionGrabController = CreateComponent<MinionGrabController>("MinionGrabController");
            minionGrabController.Initialize(camera, grid, trainingRoomManager, jailManager);

            var interactionController = CreateComponent<TileInteractionController>("TileInteractionController");
            interactionController.Initialize(camera, grid, jobBoard, lairManager, treasuryManager, slimeHatcheryManager, tavernManager, trainingRoomManager, libraryManager, jailManager, conversionClassManager, bridgeManager, implingSpawner, minionGrabController);

            var bottomMenuBar = CreateComponent<BottomMenuBar>("BottomMenuBar");
            bottomMenuBar.Initialize(grid, jobBoard, interactionController, treasuryManager, throneRoom, tavernManager, trainingRoomManager, libraryManager, jailManager, conversionClassManager, gremlinSpawner, warlockSpawner, mazeRattlerSpawner, beanCounterSpawner);

            SaveStartingLevelAsLevel1(grid, throneRoomCenter, portalCoord);
        }

        /// Snapshots the freshly-built starting world into a "level1" save
        /// file via the same LevelData/LevelFileIO the Level Designer's own
        /// Save menu uses, so it shows up in that menu's Load list ready to
        /// tweak. Only writes it once — BuildWorld (and so this) reruns
        /// every time Start Game is pressed, and overwriting "level1" on
        /// every launch would clobber any edits saved back onto it from
        /// the Level Designer since; skip entirely once the file exists.
        /// A throwaway LevelDesignerSession does the actual snapshotting
        /// (reusing BuildLevelData rather than duplicating its tile-
        /// scanning logic) — but only ever reads from grid, it never calls
        /// PlaceStructure/PlaceCreature against it, since those mutate
        /// live tile state (EditorPaintFloor et al.) and would corrupt the
        /// layout BuildWorld just finished carving. The Throne Room/Portal
        /// structures are appended to the result by hand afterward
        /// instead, from the same coords BuildWorld itself used.
        private static void SaveStartingLevelAsLevel1(DungeonGrid grid, Vector2Int throneRoomCenter, Vector2Int portalCoord)
        {
            if (LevelFileIO.Load("level1") != null)
            {
                return;
            }

            var session = CreateComponent<LevelDesignerSession>("StartingLevelSnapshot");
            // roomManagers: null — this snapshot-only session never calls
            // ApplyLevelData (see this method's own header), so nothing
            // here ever dispatches through _roomManagers.
            session.Initialize(grid, new LevelDesignerProperties
            {
                Multiplayer = false,
                PlayerCount = 1,
                MapWidth = grid.Width,
                MapHeight = grid.Height
            }, roomManagers: null);

            // Scans every live creature agent (Implings at this point in
            // BuildWorld — see this method's own header for why no other
            // species has a live instance yet) into the session's own
            // _creatures list, the same list PlaceCreature appends to, so
            // BuildLevelData's tile-scanning-only capture below doesn't
            // silently save an empty Creatures list despite a populated
            // starting world.
            session.CaptureLiveCreatures();

            var data = session.BuildLevelData();

            // OwnerId 0, not -1 — CarveRoom claims both footprints as
            // Ownership.Claimed but leaves TileState.OwnerId at its
            // default (0), so that's what the tile-scanning loop above
            // actually captured into data.Tiles for every one of these
            // tiles. LevelDesignerSession.PlaceStructure re-paints its
            // whole footprint as Claimed floor using THIS OwnerId on
            // every load (see its own comment) — recording -1 here made
            // it re-paint the footprint Unclaimed on every load, silently
            // undoing the correct Claimed/OwnerId=0 state the tile loop
            // had just restored a moment earlier.
            data.Structures.Add(new LevelStructureData { Kind = StructureKind.ThroneRoom, X = throneRoomCenter.x, Y = throneRoomCenter.y, OwnerId = 0 });
            data.Structures.Add(new LevelStructureData { Kind = StructureKind.PortalRoom, X = portalCoord.x, Y = portalCoord.y, OwnerId = 0 });

            LevelFileIO.Save("level1", data);
            Object.Destroy(session.gameObject);
        }

        /// GameBootstrap owns the one true camera/listener for this prototype.
        /// Whatever scene happens to be loaded may already have its own default
        /// Main Camera (and AudioListener) — clear those out first so Unity's
        /// "multiple audio listeners" warning can't spam the console.
        private static void RemoveStrayCameras()
        {
            foreach (var existingCamera in Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude))
            {
                Object.Destroy(existingCamera.gameObject);
            }
        }

        private static void CreateSun()
        {
            // Near-straight-down key light so every tile across the map is
            // lit the same amount — a shallower angle throws long wall
            // shadows that streak unevenly across the floor. Kept a few
            // degrees off vertical (and slightly off-axis) so the KayKit
            // wall meshes still get a touch of face shading instead of
            // reading perfectly flat.
            var lightGO = new GameObject("Sun");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.6f;
            light.shadowStrength = 0.45f;
            lightGO.transform.rotation = Quaternion.Euler(78f, -18f, 0f);

            // Set explicitly rather than trusting the scene file's own
            // Render Settings — Prototype.unity's Ambient Mode is left at
            // Skybox with no Skybox Material assigned, which leaves
            // surfaces with no ambient/fill light at all. That's invisible
            // on the flat-topped placeholder cubes (their top face still
            // catches the Sun directly), but it reads as solid black on
            // the KayKit wall meshes' vertical faces wherever they don't
            // point straight at the Sun. Flat ambient is the simplest fix
            // that doesn't depend on a skybox existing — pushed brighter
            // here so shadowed/side faces stay clearly readable and the
            // whole dungeon sits at a higher overall light level.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.68f);
        }

        /// panMargin is the caller's own choice rather than a fixed
        /// constant — BuildWorld's gameplay grid is always the same fixed
        /// size, but the level designer's map varies a lot (12 to 256 per
        /// side — see LevelDesignerPropertiesMenu), so its own caller
        /// (BuildLevelDesignerWorld) scales the margin to the actual map
        /// footprint instead of reusing gameplay's fixed 22.5.
        private static Camera CreateIsoCamera(DungeonGrid grid, float panMargin)
        {
            var cameraGO = new GameObject("Main Camera");
            cameraGO.tag = "MainCamera";
            var camera = cameraGO.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 10f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.05f, 0.07f);
            cameraGO.AddComponent<AudioListener>();

            var target = new Vector3(grid.Width * grid.CellSize * 0.5f, 0f, grid.Height * grid.CellSize * 0.5f);
            var rotation = Quaternion.Euler(45f, 45f, 0f);
            const float distance = 20f;
            cameraGO.transform.rotation = rotation;
            cameraGO.transform.position = target - rotation * Vector3.forward * distance;

            var isoCam = cameraGO.AddComponent<IsoCameraController>();
            var camPos = cameraGO.transform.position;
            isoCam.SetPanBounds(new Vector2(camPos.x - panMargin, camPos.z - panMargin), new Vector2(camPos.x + panMargin, camPos.z + panMargin));

            return camera;
        }

        /// 3 utility rooms off Throne Room, each behind its own single-tile
        /// corridor the same way Portal/Treasury are: Lair to the west,
        /// Slime Hatchery to the south, and Tavern further west
        /// beyond the Lair (there were only two cardinal sides left free,
        /// so the third chains off the first rather than bordering the
        /// Throne Room directly). Only carves Floor here — filling each with its
        /// actual room happens later in Init(), once the relevant manager
        /// exists (see LairManager.PlaceStartingLair and friends), same
        /// staging Library/Training Room already use.
        private static void CarveStartingUtilityRooms(DungeonGrid grid, Vector2Int throneRoomCenter,
            out Vector2Int lairRoomOrigin, out Vector2Int lairRoomEndCoord,
            out Vector2Int hatcheryRoomOrigin, out Vector2Int hatcheryRoomEndCoord,
            out Vector2Int tavernRoomOrigin, out Vector2Int tavernRoomEndCoord)
        {
            var westCorridor = throneRoomCenter + new Vector2Int(-(ThroneRoomHalfSize + 1), 0);
            lairRoomOrigin = westCorridor + new Vector2Int(-StartingUtilityRoomSize, -StartingUtilityRoomSize / 2);
            lairRoomEndCoord = lairRoomOrigin + new Vector2Int(StartingUtilityRoomSize - 1, StartingUtilityRoomSize - 1);
            grid.CarveRect(lairRoomOrigin, StartingUtilityRoomSize, StartingUtilityRoomSize);
            grid.CarveRoom(westCorridor, 0, isBuildable: false);

            var southCorridor = throneRoomCenter + new Vector2Int(0, -(ThroneRoomHalfSize + 1));
            hatcheryRoomOrigin = southCorridor + new Vector2Int(-StartingUtilityRoomSize / 2, -StartingUtilityRoomSize);
            hatcheryRoomEndCoord = hatcheryRoomOrigin + new Vector2Int(StartingUtilityRoomSize - 1, StartingUtilityRoomSize - 1);
            grid.CarveRect(hatcheryRoomOrigin, StartingUtilityRoomSize, StartingUtilityRoomSize);
            grid.CarveRoom(southCorridor, 0, isBuildable: false);

            var farWestCorridor = lairRoomOrigin + new Vector2Int(-1, StartingUtilityRoomSize / 2);
            tavernRoomOrigin = farWestCorridor + new Vector2Int(-StartingUtilityRoomSize, -StartingUtilityRoomSize / 2);
            tavernRoomEndCoord = tavernRoomOrigin + new Vector2Int(StartingUtilityRoomSize - 1, StartingUtilityRoomSize - 1);
            grid.CarveRect(tavernRoomOrigin, StartingUtilityRoomSize, StartingUtilityRoomSize);
            grid.CarveRoom(farWestCorridor, 0, isBuildable: false);
        }

        /// One roll per Rock tile, in order, deciding whether it becomes a
        /// resource vein and which kind — a plain sequential pass rather
        /// than picking N random coords, since it's O(width*height) either
        /// way at this grid size and this way needs no separate "did I
        /// already pick this tile" bookkeeping.
        private static void ScatterResourceWalls(DungeonGrid grid)
        {
            var rng = new System.Random(ResourceScatterSeed);

            for (int x = 0; x < grid.Width; x++)
            {
                for (int y = 0; y < grid.Height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    if (grid.GetTile(coord).Type != TileType.Rock)
                    {
                        continue;
                    }

                    var roll = rng.NextDouble();
                    if (roll < ManaCrystalWallChance)
                    {
                        grid.SetWallResourceType(coord, WallResourceType.ManaCrystalWall);
                    }
                    else if (roll < ManaCrystalWallChance + RegeneratingGoldWallChance)
                    {
                        grid.SetWallResourceType(coord, WallResourceType.RegeneratingGoldWall);
                    }
                    else if (roll < ManaCrystalWallChance + RegeneratingGoldWallChance + GoldWallChance)
                    {
                        grid.SetWallResourceType(coord, WallResourceType.GoldWall);
                    }
                }
            }
        }

        /// 4 starting implings, one on each corner of the Throne Room's 3x3
        /// platform — those tiles are still plain walkable Floor underneath
        /// (the platform's just a visual overlay, see ThroneRoom.Initialize),
        /// so SpawnImplingAt — the same mana-summon the Impling menu's
        /// button uses — works directly here without needing a Lair first
        /// (implings are mana-conjured, not Lair-dependent). Goes through
        /// ImplingSpawner rather than a direct instantiate so these
        /// implings reserve their upkeep mana exactly like any other spawn
        /// (see ImplingSpawner.SpawnImpling).
        private static void SpawnStartingImplings(ImplingSpawner implingSpawner, Vector2Int throneRoomCenter)
        {
            var offset = ThroneRoom.PlatformHalfSize;
            implingSpawner.SpawnImplingAt(throneRoomCenter + new Vector2Int(-offset, -offset));
            implingSpawner.SpawnImplingAt(throneRoomCenter + new Vector2Int(offset, -offset));
            implingSpawner.SpawnImplingAt(throneRoomCenter + new Vector2Int(-offset, offset));
            implingSpawner.SpawnImplingAt(throneRoomCenter + new Vector2Int(offset, offset));
        }

        /// BuildWorld's "data != null" tile-restoration step — same shape
        /// as LevelDesignerSession.RestoreTile/ApplyLevelData's two-pass
        /// approach (paint terrain/wall/floor immediately, defer RoomId-
        /// tagged tiles into grouped footprints for RoomReconstruction to
        /// restore afterward, once room managers exist), just written
        /// directly against a plain LevelData rather than through a
        /// LevelDesignerSession instance — this is populating a real
        /// gameplay grid, not an authoring session. Untouched (still-
        /// default) Rock tiles need no restoration — they were never
        /// saved (see BuildLevelData's own IsDefaultRock skip) and
        /// DungeonGrid.Initialize already defaults every tile to plain
        /// Rock.
        private static void RestoreWorldTiles(DungeonGrid grid, LevelData data, out Dictionary<string, List<Vector2Int>> roomFootprints, out Dictionary<string, int> roomOwners)
        {
            roomFootprints = new Dictionary<string, List<Vector2Int>>();
            roomOwners = new Dictionary<string, int>();

            foreach (var tileData in data.Tiles)
            {
                var coord = new Vector2Int(tileData.X, tileData.Y);
                switch (tileData.Type)
                {
                    case TileType.Water:
                    case TileType.Lava:
                    case TileType.Chasm:
                    case TileType.HolyGround:
                        grid.EditorPaintTerrain(coord, tileData.Type);
                        break;
                    case TileType.Floor:
                        grid.EditorPaintFloor(coord, tileData.Ownership == TileOwnership.Claimed, tileData.OwnerId);
                        if (!string.IsNullOrEmpty(tileData.RoomId))
                        {
                            if (!roomFootprints.TryGetValue(tileData.RoomId, out var footprint))
                            {
                                footprint = new List<Vector2Int>();
                                roomFootprints[tileData.RoomId] = footprint;
                                roomOwners[tileData.RoomId] = tileData.OwnerId;
                            }
                            footprint.Add(coord);
                        }
                        break;
                    case TileType.Rock:
                        if (tileData.IsBedrock)
                        {
                            grid.EditorPaintWall(coord, EditorWallVariant.Bedrock);
                        }
                        else if (tileData.IsReinforced)
                        {
                            grid.EditorPaintWall(coord, EditorWallVariant.Reinforced, tileData.OwnerId);
                        }
                        else if (tileData.WallResourceType != WallResourceType.None)
                        {
                            grid.EditorPaintWall(coord, RoomReconstruction.ToEditorWallVariant(tileData.WallResourceType));
                        }
                        break;
                }
            }
        }

        /// The saved coord of the first Structure of kind in data, or
        /// fallback if none is saved (shouldn't happen for a level1 born
        /// from SaveStartingLevelAsLevel1, which always appends both — but
        /// don't hard-crash BuildWorld over a hand-edited/stale save
        /// that's missing one).
        private static Vector2Int FindStructureCoordOrDefault(LevelData data, StructureKind kind, Vector2Int fallback)
        {
            foreach (var structure in data.Structures)
            {
                if (structure.Kind == kind)
                {
                    return new Vector2Int(structure.X, structure.Y);
                }
            }

            return fallback;
        }

        /// BuildWorld's "data != null" creature-restoration step — unlike
        /// the Level Designer's PlaceCreature (an inert visual marker),
        /// this spawns each saved creature as a real live agent via the
        /// matching spawner's existing "spawn one at this coord, no cost/
        /// join-requirement checks" primitive, since this is actual
        /// gameplay. EditorCreatureKind maps 1:1 onto the 6 species (see
        /// LevelDesignerSession.CaptureLiveCreatures' own header).
        private static void RestoreWorldCreatures(LevelData data, ImplingSpawner implingSpawner, GremlinSpawner gremlinSpawner, WarlockSpawner warlockSpawner, MazeRattlerSpawner mazeRattlerSpawner, BeanCounterSpawner beanCounterSpawner, ElfSpawner elfSpawner)
        {
            foreach (var creatureData in data.Creatures)
            {
                var coord = new Vector2Int(creatureData.X, creatureData.Y);
                switch (creatureData.Kind)
                {
                    case EditorCreatureKind.Imp:
                        implingSpawner.SpawnImplingAt(coord);
                        break;
                    case EditorCreatureKind.Gremlin:
                        gremlinSpawner.SpawnGremlin(coord);
                        break;
                    case EditorCreatureKind.Warlock:
                        warlockSpawner.SpawnWarlock(coord);
                        break;
                    case EditorCreatureKind.MazeRattler:
                        mazeRattlerSpawner.SpawnMazeRattler(coord);
                        break;
                    case EditorCreatureKind.BeanCounter:
                        beanCounterSpawner.SpawnBeanCounter(coord);
                        break;
                    case EditorCreatureKind.Elf:
                        elfSpawner.SpawnElf(coord);
                        break;
                }
            }
        }

        private static T CreateComponent<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            return go.AddComponent<T>();
        }
    }
}

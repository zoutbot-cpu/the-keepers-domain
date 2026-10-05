using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.CameraControl;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Net;
using KeepersDomain.Rooms;
using KeepersDomain.Implings;
using KeepersDomain.Monsters;
using KeepersDomain.UI;

namespace KeepersDomain.Core
{
    /// Builds and loads Level Designer worlds — the editor grid, its
    /// gold-free room managers and the LevelDesignerSession.
    internal static class LevelDesignerBootstrap
    {
        /// Reached via the main menu's "Level Designer" button — collects
        /// the up-front properties (player count, map size) the actual
        /// editor world (see BuildLevelDesignerWorld) gets created with, or
        /// lets the player skip that and load a previously saved level
        /// straight in instead (see LevelDesignerPropertiesMenu's own Load
        /// Existing Level list). Reuses the menu camera ShowMainMenu
        /// already created rather than making its own.
        internal static void ShowLevelDesignerProperties()
        {
            var propertiesMenu = SceneSetup.CreateComponent<LevelDesignerPropertiesMenu>("LevelDesignerPropertiesMenu");
            propertiesMenu.Initialize(GameBootstrap.ShowMainMenu, BuildLevelDesignerWorld, LoadLevelDesignerWorld, GenerateLevelDesignerWorld);
        }

        /// Level Properties menu "Generate Map" — runs the seed-based
        /// generator (see MapGenerator) at the chosen size / player count and
        /// drops the result straight into the Level Designer, so it can be
        /// inspected, tweaked, and saved under its own name (then played via
        /// the lobby's map picker, or exported as level1).
        private static void GenerateLevelDesignerWorld(LevelDesignerProperties properties, int seed)
        {
            var data = MapGenerator.Generate(new MapGenSettings
            {
                Seed = seed,
                PlayerCount = properties.PlayerCount,
                MapWidth = properties.MapWidth,
                MapHeight = properties.MapHeight,
                Multiplayer = properties.Multiplayer
            });

            LoadLevelDesignerWorld($"generated-{seed}", data);
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
            SceneSetup.RemoveStrayCameras();
            SceneSetup.CreateSun();

            var grid = SceneSetup.CreateComponent<DungeonGrid>("DungeonGrid");
            grid.Initialize(properties.MapWidth, properties.MapHeight, WorldBuilder.CellSize);

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

            var session = SceneSetup.CreateComponent<LevelDesignerSession>("LevelDesignerSession");
            session.Initialize(grid, properties, roomManagers);

            // Only on a brand-new blank map — LoadLevelDesignerWorld
            // restores exactly what was saved instead, no extras injected.
            CreateLevelDesignerTestRooms(grid);

            SetUpLevelDesignerWorld(grid, session, initialLevelName: null, roomManagers);
        }

        /// Creates and wires the nine player-buildable room managers (every
        /// RoomDesignTool value except None) for the Level Designer, shared
        /// by BuildLevelDesignerWorld and LoadLevelDesignerWorld. Same
        /// Initialize wiring BuildWorld uses, minus anything gameplay-only
        /// that the Level Designer has no business running:
        /// - No starting gold/PlaceStartingTreasury call — rooms placed
        ///   here are always gold-free anyway (see each manager's own
        ///   RestoreRoom).
        /// - SlimeHatcheryManager gets simulateBreeding: false so placing/
        ///   loading a Hatchery never starts spawning live SlimeAgents
        ///   while the map is just being edited.
        /// - BridgeManager gets simulateDecay: false for the same reason —
        ///   a restored Lava bridge must not decay while a map is being
        ///   edited. It has no Rooms-menu button (a bridge is a line, not a
        ///   rectangle) — it's here only so a saved bridge tile still
        ///   reconstructs (see BridgeManager.RestoreRoom).
        /// - ConversionClassManager gets null JailManager-linked prisoner
        ///   release and null creature spawners — already all null-safe
        ///   internally, and none of that behavior is reachable without a
        ///   live gameplay loop feeding it.
        /// Returned as a RoomDesignTool -> manager lookup, the same shape
        /// both LevelDesignerInteractionController's live Rooms tool and
        /// LevelDesignerSession's save/load path need.
        internal static Dictionary<RoomDesignTool, IRestorableRoomManager> CreateLevelDesignerRoomManagers(DungeonGrid grid, int ownerId = 0)
        {
            var lairManager = SceneSetup.CreateComponent<LairManager>("LairManager");
            var treasuryManager = SceneSetup.CreateComponent<TreasuryManager>("TreasuryManager");
            treasuryManager.Initialize(grid, lairManager, ownerId);
            lairManager.Initialize(grid, treasuryManager, ownerId);

            var slimeHatcheryManager = SceneSetup.CreateComponent<SlimeHatcheryManager>("SlimeHatcheryManager");
            slimeHatcheryManager.Initialize(grid, lairManager, treasuryManager, simulateBreeding: false, ownerId: ownerId);

            var tavernManager = SceneSetup.CreateComponent<TavernManager>("TavernManager");
            tavernManager.Initialize(grid, lairManager, treasuryManager, ownerId);

            var trainingRoomManager = SceneSetup.CreateComponent<TrainingRoomManager>("TrainingRoomManager");
            trainingRoomManager.Initialize(grid, lairManager, treasuryManager, ownerId);

            var libraryManager = SceneSetup.CreateComponent<LibraryManager>("LibraryManager");
            libraryManager.Initialize(grid, lairManager, treasuryManager, ownerId);

            var jailManager = SceneSetup.CreateComponent<JailManager>("JailManager");
            jailManager.Initialize(grid, lairManager, treasuryManager, ownerId);

            var conversionClassManager = SceneSetup.CreateComponent<ConversionClassManager>("ConversionClassManager");
            conversionClassManager.Initialize(grid, lairManager, treasuryManager, jailManager,
                gremlinSpawner: null, warlockSpawner: null, mazeRattlerSpawner: null, elfSpawner: null, ownerId: ownerId);

            var bridgeManager = SceneSetup.CreateComponent<BridgeManager>("BridgeManager");
            bridgeManager.Initialize(grid, lairManager, treasuryManager, ownerId: ownerId, simulateDecay: false);

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
                { RoomDesignTool.Bridge, bridgeManager },
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

            SceneSetup.RemoveStrayCameras();
            SceneSetup.CreateSun();

            var grid = SceneSetup.CreateComponent<DungeonGrid>("DungeonGrid");
            grid.Initialize(data.MapWidth, data.MapHeight, WorldBuilder.CellSize);

            var roomManagers = CreateLevelDesignerRoomManagers(grid);

            var session = SceneSetup.CreateComponent<LevelDesignerSession>("LevelDesignerSession");
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
            var panMargin = Mathf.Max(grid.Width, grid.Height) * WorldBuilder.CellSize * 0.5f + 10f;
            var camera = SceneSetup.CreateIsoCamera(grid, panMargin);

            var interactionController = SceneSetup.CreateComponent<LevelDesignerInteractionController>("LevelDesignerInteractionController");
            interactionController.Initialize(camera, grid, session, roomManagers);

            var menuBar = SceneSetup.CreateComponent<LevelDesignerMenuBar>("LevelDesignerMenuBar");
            var jailManager = (JailManager)roomManagers[RoomDesignTool.Jail];
            menuBar.Initialize(session, interactionController, grid, jailManager, LoadLevelDesignerWorld, initialLevelName);
        }
    }
}

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
            // The long-lived NetworkManager + Unity Gaming Services wrapper.
            // Idle unless Host/Join is pressed — offline "Start Game" never
            // touches it. Wired here so the callbacks survive a
            // Main Menu <-> game bounce.
            NetSession.Create();
            NetSession.Instance.OnHostReady = OnHostReady;
            NetSession.Instance.OnClientReady = BuildClientWorld;
            NetSession.Instance.OnDisconnected = ReturnToMainMenu;

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
            KeeperContext.All = null;
            StanceRegistry.Current = null;

            // NetSession + its NetworkManager are DontDestroyOnLoad, so
            // they're not in the active scene's roots — Leave() shuts the
            // transport down without tearing the objects out.
            if (NetSession.Instance != null && NetSession.Instance.IsNetworked)
            {
                NetSession.Instance.Leave();
            }

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
            menu.Initialize(StartGame, ShowLevelDesignerProperties, HostGame, JoinGame);
        }

        /// Main Menu "Host Game" — spins up a Relay session (join code) and,
        /// once it's live (NetSession.OnHostReady -> OnHostReady below),
        /// builds the authoritative world exactly as offline Start Game does.
        private static void HostGame()
        {
            NetSession.Instance.StartHost();
        }

        /// Main Menu "Join Game" — connects to the host's session by code.
        /// The render-only client world is built from NetGame's client-side
        /// OnNetworkSpawn (-> NetSession.OnClientReady -> BuildClientWorld).
        private static void JoinGame(string joinCode)
        {
            NetSession.Instance.JoinByCode(joinCode);
        }

        /// NetSession.OnHostReady — the transport is up and we're the host.
        /// Build the world (same level1-or-fresh path Start Game uses), then
        /// spawn the one session-lifetime networked object and bind it to
        /// the grid so tile changes replicate.
        private static void OnHostReady()
        {
            BuildWorld(LevelFileIO.Load("level1"));

            var grid = Object.FindAnyObjectByType<DungeonGrid>();
            var prefab = Resources.Load<GameObject>("Net/NetGame");
            var netGameGo = Object.Instantiate(prefab);
            var netObj = netGameGo.GetComponent<NetworkObject>();
            netObj.Spawn(destroyWithScene: true);
            netGameGo.GetComponent<NetGame>().HostBind(grid);

            CreateComponent<NetHud>("NetHud").Initialize(isHost: true);
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

            var bridgeManager = CreateComponent<BridgeManager>("BridgeManager");
            bridgeManager.Initialize(grid, lairManager, treasuryManager, ownerId: 0, simulateDecay: false);

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
            var jailManager = (JailManager)roomManagers[RoomDesignTool.Jail];
            menuBar.Initialize(session, interactionController, grid, jailManager, LoadLevelDesignerWorld, initialLevelName);
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
        /// The joined client's world (Milestone 1a) — deliberately thin: it
        /// RENDERS, it never simulates. No KeeperContext, no job boards, no
        /// room managers, no spawners, no StanceRegistry. Just a grid the
        /// host's NetGame fills via a snapshot + live tile deltas (see
        /// DungeonGrid.ApplyReplicatedTile), a local pan/zoom camera, and
        /// the cosmetic liquid animator. Creature ghosts, HUD state and
        /// client commands arrive in later milestones. Called synchronously
        /// from NetGame's client-side OnNetworkSpawn, so the grid exists
        /// before NetGame requests the snapshot on the next line there.
        private static void BuildClientWorld()
        {
            KeeperContext.All = null;
            StanceRegistry.Current = null;

            RemoveStrayCameras();
            CreateSun();

            var netGame = NetGame.Instance;
            var width = netGame != null ? Mathf.Max(1, netGame.MapWidth.Value) : GridWidth;
            var height = netGame != null ? Mathf.Max(1, netGame.MapHeight.Value) : GridHeight;

            var grid = CreateComponent<DungeonGrid>("DungeonGrid");
            grid.Initialize(width, height, CellSize);

            // Placeholder 2-colour palette so owner-tinted floor / orbs /
            // rings read on the client until the real roster syncs (M1b).
            grid.OwnerColors = new[] { LevelDesignerColors.Palette[0], LevelDesignerColors.Palette[1] };
            grid.PlayerColor = grid.OwnerColors[0];
            grid.TintFloorByOwner = true;

            var liquidAnimator = CreateComponent<LiquidAnimator>("LiquidAnimator");
            liquidAnimator.Initialize();

            var panMargin = 22.5f;
            var mapCenter = grid.GridToWorld(new Vector2Int(width / 2, height / 2));
            CreateIsoCamera(grid, panMargin, mapCenter);

            CreateComponent<NetHud>("NetHud").Initialize(isHost: false);
        }

        private static void BuildWorld(LevelData data = null)
        {
            // Drop any stale KeeperContext references from a previous
            // session (a "Main Menu -> Start Game" bounce) before anything
            // can read the static registry.
            KeeperContext.All = null;

            // One combat-stance table per game (see design-doc.md's Combat
            // section) — every keeper defaults to Aggressive toward every
            // other, so combat only actually happens on a multi-keeper
            // level. A stance-editing UI would call StanceRegistry.Set.
            StanceRegistry.Current = new StanceRegistry();

            // "Finish off enemies" starts off every game — the player opts
            // in via BottomMenuBar's Settings menu.
            KeepersDomain.Creatures.Combatant.AllowFinishOffEnemies = false;

            // Clears out the menu camera created by ShowMainMenu — the real
            // iso camera below replaces it.
            RemoveStrayCameras();
            CreateSun();

            var grid = CreateComponent<DungeonGrid>("DungeonGrid");
            grid.Initialize(data != null ? data.MapWidth : GridWidth, data != null ? data.MapHeight : GridHeight, CellSize);

            // One PlayerSpec per keeper — a single default for a
            // from-scratch map, one per entry for a loaded roster.
            var specs = SynthesizePlayerSpecs(data);

            if (specs.Length > 1)
            {
                // A multi-player level: every owner past 0 has real
                // ownership in the save (tiles/walls/rooms/creatures all
                // keep their OwnerId), but gameplay used to populate only a
                // single-entry OwnerColors array via the PlayerColor setter
                // — so DungeonGrid.ResolveOwnerColor collapsed every other
                // owner's Reinforced-wall orbs and CreatureHealthRing
                // collapsed their rings onto that one color, and
                // TintFloorByOwner stayed false so their claimed floor was
                // untinted too. Populate the real per-owner palette
                // (mirroring LevelDesignerSession.RefreshGridOwnerColors) so
                // each roster stays visually its own.
                var ownerColors = new Color[specs.Length];
                for (int i = 0; i < ownerColors.Length; i++)
                {
                    ownerColors[i] = specs[i].Color;
                }
                // PlayerColor first (fallback for -1/out-of-range owners),
                // then override the array it just collapsed to one entry —
                // the grid has no tiles yet, so the setter's
                // RefreshAllVisuals is a no-op and per-tile RefreshVisual
                // during RestoreWorldTiles picks up the full array.
                grid.PlayerColor = ownerColors[0];
                grid.OwnerColors = ownerColors;
                grid.TintFloorByOwner = true;
            }
            else
            {
                // Single player — green placeholder for a fresh map (see
                // SynthesizePlayerSpecs), or the one loaded player's own
                // color. Visible on the Reinforced wall orb and ThroneRoom's
                // fallback orb; TintFloorByOwner stays off so plain claimed
                // floor renders exactly as before.
                grid.PlayerColor = specs[0].Color;
            }

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

            // One Throne/Portal coord per keeper. Fresh: only [0] is set
            // (and carved). Loaded: resolved per player from data.Structures.
            var throneCoords = new Vector2Int[specs.Length];
            var portalCoords = new Vector2Int[specs.Length];

            // Declared here (rather than only inside the else branch
            // below) so they're still in scope for the PlaceStartingX
            // calls further down, run only on the fresh (data == null)
            // path — left at their default, unused value when data != null,
            // since that branch reconstructs every room through
            // RestoreWorldTiles + RestoreWorldRoomsPerOwner instead.
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

                for (int i = 0; i < specs.Length; i++)
                {
                    var fallback = SpreadFallbackCoord(grid, i);
                    throneCoords[i] = FindStructureCoordOrDefault(data, StructureKind.ThroneRoom, fallback, preferredOwnerId: i);
                    portalCoords[i] = FindStructureCoordOrDefault(data, StructureKind.PortalRoom,
                        fallback + new Vector2Int(ThroneRoomHalfSize + PortalRoomHalfSize + 2, 0), preferredOwnerId: i);
                }
            }
            else
            {
                // Throne Room sits at the grid center; the portal gets its own
                // room to the east, joined by a single one-tile corridor.
                var throneRoomCenter = new Vector2Int(GridWidth / 2, GridHeight / 2);
                var corridorCoord = throneRoomCenter + new Vector2Int(ThroneRoomHalfSize + 1, 0);
                var portalCoord = corridorCoord + new Vector2Int(PortalRoomHalfSize + 1, 0);
                throneCoords[0] = throneRoomCenter;
                portalCoords[0] = portalCoord;

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

            // Build one full gameplay stack per keeper (see
            // BuildKeeperContext) — job board, Portal + recruit pools,
            // Throne mana, the nine room managers, the six spawners, all
            // owner-scoped. Fresh game = exactly one.
            var contexts = new KeeperContext[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                var keeperParent = new GameObject($"Keeper P{i + 1}").transform;
                contexts[i] = BuildKeeperContext(grid, specs[i], throneCoords[i], portalCoords[i], keeperParent);
            }
            KeeperContext.All = contexts;

            if (data != null)
            {
                // Reconstruct every saved room through its owner's managers
                // (see RestoreWorldRoomsPerOwner), then hand each keeper its
                // starting gold — after that keeper's Treasury tiles exist
                // (rebuilt just now), same ordering rationale the single-
                // manager version used.
                if (roomFootprints != null)
                {
                    RestoreWorldRoomsPerOwner(grid, roomFootprints, roomOwners, contexts);
                }

                for (int i = 0; i < contexts.Length; i++)
                {
                    contexts[i].Treasury.AddGold(specs[i].StartingGold);
                }
            }
            else
            {
                // Fresh map: the local keeper (owner 0) gets the starting
                // domain — Treasury/Library/Training Room/Lair/Hatchery/
                // Tavern placed via each manager's PlaceStartingX (real,
                // sellable rooms, gold-free — terrain generation, not a
                // purchase), same coords as before. Then its starting gold,
                // after the Treasury tiles exist.
                var c0 = contexts[0];
                c0.Treasury.PlaceStartingTreasury(
                    treasuryCoord - new Vector2Int(TreasuryRoomHalfSize, TreasuryRoomHalfSize),
                    treasuryCoord + new Vector2Int(TreasuryRoomHalfSize, TreasuryRoomHalfSize));
                c0.Library.PlaceStartingLibrary(libraryRoomOrigin, libraryRoomEndCoord);
                c0.TrainingRoom.PlaceStartingTrainingRoom(trainingRoomStartOrigin, trainingRoomStartEndCoord);
                c0.Lair.PlaceStartingLair(lairRoomOrigin, lairRoomEndCoord);
                c0.SlimeHatchery.PlaceStartingHatchery(hatcheryRoomOrigin, hatcheryRoomEndCoord);
                c0.Tavern.PlaceStartingTavern(tavernRoomOrigin, tavernRoomEndCoord);
                c0.Treasury.AddGold(specs[0].StartingGold);
            }

            // Floor authored as Unclaimed in the Level Designer is loaded
            // straight in — it never gets dug, so it never fires
            // FloorNeedsClaim and imps would otherwise ignore it forever.
            // Queue a claim job for every such tile on every keeper's board;
            // each board's own frontier rule still gates when its imps act.
            QueuePreplacedClaimJobs(grid, contexts);

            // Restore each saved creature as a real live agent through its
            // own keeper's spawner (see RestoreWorldCreatures), or spawn the
            // fixed four starting Implings for the local keeper on a fresh
            // map.
            if (data != null)
            {
                RestoreWorldCreatures(data, contexts);
            }
            else
            {
                SpawnStartingImplings(contexts[0].ImplingSpawner, throneCoords[0]);
            }

            const int localPlayerIndex = 0;

            // Pan margin: 22.5f for a freshly generated map (the +50%-scaled
            // gameplay grid — 15f base -> 22.5f — kept exactly as tuned), but
            // a loaded level can be any size up to the Level Designer's 256,
            // so scale to the actual footprint the same way
            // SetUpLevelDesignerWorld does. Opens centered on the local
            // player's Throne Room (see CreateIsoCamera's focusGroundPoint).
            var panMargin = data != null
                ? Mathf.Max(grid.Width, grid.Height) * CellSize * 0.5f + 10f
                : 22.5f;
            var camera = CreateIsoCamera(grid, panMargin, grid.GridToWorld(throneCoords[localPlayerIndex]));

            // Input / grab hand / HUD are built once and bound to the local
            // keeper's context; the debug player switcher (BottomMenuBar,
            // only shown when contexts.Length > 1) repoints all three plus
            // the camera through LocalPlayerController.SetActivePlayer.
            var minionGrabController = CreateComponent<MinionGrabController>("MinionGrabController");
            minionGrabController.Initialize(camera, grid, contexts, localPlayerIndex);

            var interactionController = CreateComponent<TileInteractionController>("TileInteractionController");
            interactionController.Initialize(camera, grid, contexts, minionGrabController, localPlayerIndex);

            var localPlayerController = CreateComponent<LocalPlayerController>("LocalPlayerController");
            var bottomMenuBar = CreateComponent<BottomMenuBar>("BottomMenuBar");
            bottomMenuBar.Initialize(grid, contexts, interactionController, localPlayerController, localPlayerIndex);
            localPlayerController.Initialize(camera, grid, contexts, interactionController, minionGrabController, bottomMenuBar, localPlayerIndex);

            SaveStartingLevelAsLevel1(grid, contexts[0].ThroneCoord, contexts[0].PortalCoord);
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

            // Pin the snapshot's economy to what the fresh build actually
            // used (StartingGold constant, 100 starting mana) rather than
            // LevelDesignerSession's own StartingGoldDefault/
            // StartingManaDefault — so "fresh -> auto-save -> reload level1"
            // comes up with the identical gold/mana it had on the fresh
            // run. A hand-edited level1 keeps whatever the designer set.
            if (data.Players.Count > 0)
            {
                data.Players[0].StartingGold = StartingGold;
                data.Players[0].StartingMana = 100;
            }

            // OwnerId 0 — CarveRoom now explicitly stamps its footprint
            // OwnerId 0 (the local keeper) alongside Ownership.Claimed, so
            // that's what the tile-scanning loop above captured into
            // data.Tiles for every one of these tiles.
            // LevelDesignerSession.PlaceStructure re-paints its whole
            // footprint as Claimed floor using THIS OwnerId on every load
            // (see its own comment) — recording -1 here made it re-paint the
            // footprint Unclaimed on every load, silently undoing the
            // correct Claimed/OwnerId=0 state the tile loop had just
            // restored a moment earlier.
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
        ///
        /// focusGroundPoint is the ground position the view opens centered
        /// on — the local player's Throne Room in gameplay (see BuildWorld),
        /// so each player starts looking at their own dungeon rather than
        /// the geometric middle of the map, which on a multi-player level
        /// is nobody's. Null centers on the map middle, the original
        /// behavior (still used by the Level Designer preview). Pan bounds
        /// stay anchored to the map middle either way, so opening off-center
        /// never shrinks how far the camera can roam.
        private static Camera CreateIsoCamera(DungeonGrid grid, float panMargin, Vector3? focusGroundPoint = null)
        {
            var cameraGO = new GameObject("Main Camera");
            cameraGO.tag = "MainCamera";
            var camera = cameraGO.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 10f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.05f, 0.07f);
            cameraGO.AddComponent<AudioListener>();

            var mapCenter = new Vector3(grid.Width * grid.CellSize * 0.5f, 0f, grid.Height * grid.CellSize * 0.5f);
            var target = focusGroundPoint ?? mapCenter;
            var rotation = Quaternion.Euler(45f, 45f, 0f);
            const float distance = 20f;
            cameraGO.transform.rotation = rotation;
            cameraGO.transform.position = target - rotation * Vector3.forward * distance;

            var isoCam = cameraGO.AddComponent<IsoCameraController>();
            // Bounds are anchored to the map-center camera position, never
            // the (possibly off-center) opening position — panMargin is
            // sized by the caller to be at least half the map footprint
            // plus slack (see both call sites), so center ± panMargin
            // already reaches every edge tile no matter where the view
            // opens. Deriving bounds from the opening position instead
            // would let a Throne-Room-focused start cut off the far side of
            // the map.
            var mapCenterCamPos = mapCenter - rotation * Vector3.forward * distance;
            isoCam.SetPanBounds(
                new Vector2(mapCenterCamPos.x - panMargin, mapCenterCamPos.z - panMargin),
                new Vector2(mapCenterCamPos.x + panMargin, mapCenterCamPos.z + panMargin));

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
                        // A bridged Water/Lava tile carries a "Bridge_"
                        // RoomId — defer it into the footprint map so the
                        // RoomReconstruction dispatch below rebuilds it
                        // through the owning keeper's BridgeManager, same as
                        // any other room. Only Water/Lava ever get bridged.
                        if ((tileData.Type == TileType.Water || tileData.Type == TileType.Lava) && !string.IsNullOrEmpty(tileData.RoomId))
                        {
                            if (!roomFootprints.TryGetValue(tileData.RoomId, out var bridgeFootprint))
                            {
                                bridgeFootprint = new List<Vector2Int>();
                                roomFootprints[tileData.RoomId] = bridgeFootprint;
                                roomOwners[tileData.RoomId] = tileData.OwnerId;
                            }
                            bridgeFootprint.Add(coord);
                        }
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

        /// The saved coord of the Structure of kind owned by
        /// preferredOwnerId (the local player, 0, in gameplay — so a
        /// multi-player level's single ThroneRoom/Portal component and the
        /// opening camera focus both track the local Keeper's, not
        /// whichever the designer happened to place first). Falls back to
        /// the first Structure of that kind regardless of owner, then to
        /// fallback if none is saved at all (shouldn't happen for a level1
        /// born from SaveStartingLevelAsLevel1, which always appends both —
        /// but don't hard-crash BuildWorld over a hand-edited/stale save
        /// that's missing one).
        private static Vector2Int FindStructureCoordOrDefault(LevelData data, StructureKind kind, Vector2Int fallback, int preferredOwnerId = 0)
        {
            Vector2Int? firstOfKind = null;
            foreach (var structure in data.Structures)
            {
                if (structure.Kind != kind)
                {
                    continue;
                }

                var coord = new Vector2Int(structure.X, structure.Y);
                if (structure.OwnerId == preferredOwnerId)
                {
                    return coord;
                }

                firstOfKind ??= coord;
            }

            return firstOfKind ?? fallback;
        }

        /// BuildWorld's "data != null" creature-restoration step — unlike
        /// the Level Designer's PlaceCreature (an inert visual marker),
        /// this spawns each saved creature as a real live agent via the
        /// matching spawner's existing "spawn one at this coord, no cost/
        /// join-requirement checks" primitive, since this is actual
        /// gameplay. EditorCreatureKind maps 1:1 onto the 6 species (see
        /// LevelDesignerSession.CaptureLiveCreatures' own header). Each
        /// creature is spawned through the spawner belonging to its own
        /// keeper's context (clamped in case of a stray/out-of-range
        /// OwnerId), so it comes up wired to that player's job board /
        /// managers.
        private static void RestoreWorldCreatures(LevelData data, KeeperContext[] contexts)
        {
            foreach (var creatureData in data.Creatures)
            {
                var coord = new Vector2Int(creatureData.X, creatureData.Y);
                var ownerId = Mathf.Clamp(creatureData.OwnerId, 0, contexts.Length - 1);
                var ctx = contexts[ownerId];
                switch (creatureData.Kind)
                {
                    case EditorCreatureKind.Imp:
                        ctx.ImplingSpawner.SpawnImplingAt(coord);
                        break;
                    case EditorCreatureKind.Gremlin:
                        ctx.GremlinSpawner.SpawnGremlin(coord, ownerId);
                        break;
                    case EditorCreatureKind.Warlock:
                        ctx.WarlockSpawner.SpawnWarlock(coord, ownerId);
                        break;
                    case EditorCreatureKind.MazeRattler:
                        ctx.MazeRattlerSpawner.SpawnMazeRattler(coord, ownerId);
                        break;
                    case EditorCreatureKind.BeanCounter:
                        ctx.BeanCounterSpawner.SpawnBeanCounter(coord, ownerId);
                        break;
                    case EditorCreatureKind.Elf:
                        ctx.ElfSpawner.SpawnElf(coord, ownerId);
                        break;
                }
            }
        }

        /// One keeper's initial roster/economy config, synthesized from the
        /// loaded level's player list (or a single default for a freshly
        /// generated map). See BuildKeeperContext.
        private struct PlayerSpec
        {
            public int OwnerId;
            public bool IsAI;
            public Color Color;
            public int StartingGold;
            public int StartingMana;
        }

        /// One PlayerSpec per player in the loaded roster — or a single
        /// default (owner 0, human, green, the StartingGold constant, 100
        /// mana) for a from-scratch map or an old/hand-edited save with an
        /// empty player list. Matches the pre-multiplayer behavior exactly
        /// when there's only one player.
        private static PlayerSpec[] SynthesizePlayerSpecs(LevelData data)
        {
            if (data == null || data.Players.Count == 0)
            {
                return new[]
                {
                    new PlayerSpec { OwnerId = 0, IsAI = false, Color = Color.green, StartingGold = StartingGold, StartingMana = 100 },
                };
            }

            var specs = new PlayerSpec[data.Players.Count];
            for (int i = 0; i < specs.Length; i++)
            {
                var p = data.Players[i];
                specs[i] = new PlayerSpec
                {
                    OwnerId = i,
                    IsAI = p.IsAI,
                    Color = LevelDesignerColors.Palette[p.ColorIndex % LevelDesignerColors.Palette.Length],
                    StartingGold = p.StartingGold,
                    StartingMana = p.StartingMana > 0 ? p.StartingMana : 100,
                };
            }
            return specs;
        }

        /// Builds one keeper's entire gameplay stack — the exact same
        /// CreateComponent + Initialize wiring BuildWorld used to run once
        /// inline, now scoped to a single player and with spec.OwnerId
        /// threaded into every Initialize so the job board only reacts to
        /// this player's grid actions, rooms/creatures spawn as this
        /// player's, and roomIds land in this owner's disjoint band (see
        /// DungeonGrid.RoomIdOwnerStride). The mutual LairManager /
        /// TreasuryManager reference and every room manager's RoomSold
        /// subscription stay within this one context. Portal recruit pools
        /// are seeded per keeper.
        private static KeeperContext BuildKeeperContext(DungeonGrid grid, PlayerSpec spec, Vector2Int throneCoord, Vector2Int portalCoord, Transform parent)
        {
            var ctx = new KeeperContext
            {
                OwnerId = spec.OwnerId,
                IsAI = spec.IsAI,
                Color = spec.Color,
                ThroneCoord = throneCoord,
                PortalCoord = portalCoord,
            };

            var owner = spec.OwnerId;

            ctx.Throne = CreateComponent<ThroneRoom>($"ThroneRoom P{owner + 1}", parent);
            ctx.Throne.PlayerColor = spec.Color;
            ctx.Throne.Initialize(throneCoord, grid, owner, spec.StartingMana);

            ctx.Portal = CreateComponent<Portal>($"Portal P{owner + 1}", parent);
            ctx.Portal.Initialize(portalCoord, grid);

            ctx.JobBoard = CreateComponent<BuilderJobBoard>($"BuilderJobBoard P{owner + 1}", parent);
            ctx.JobBoard.Initialize(grid, owner);

            // LairManager <-> TreasuryManager mutual reference — created
            // first, then wired in either order (C# events / field
            // assignment don't need the other's Initialize to have run).
            ctx.Lair = CreateComponent<LairManager>($"LairManager P{owner + 1}", parent);
            ctx.Treasury = CreateComponent<TreasuryManager>($"TreasuryManager P{owner + 1}", parent);
            ctx.Treasury.Initialize(grid, ctx.Lair, owner);
            ctx.Lair.Initialize(grid, ctx.Treasury, owner);

            ctx.SlimeHatchery = CreateComponent<SlimeHatcheryManager>($"SlimeHatcheryManager P{owner + 1}", parent);
            ctx.SlimeHatchery.Initialize(grid, ctx.Lair, ctx.Treasury, simulateBreeding: true, ownerId: owner);

            ctx.Tavern = CreateComponent<TavernManager>($"TavernManager P{owner + 1}", parent);
            ctx.Tavern.Initialize(grid, ctx.Lair, ctx.Treasury, owner);

            ctx.TrainingRoom = CreateComponent<TrainingRoomManager>($"TrainingRoomManager P{owner + 1}", parent);
            ctx.TrainingRoom.Initialize(grid, ctx.Lair, ctx.Treasury, owner);

            ctx.Library = CreateComponent<LibraryManager>($"LibraryManager P{owner + 1}", parent);
            ctx.Library.Initialize(grid, ctx.Lair, ctx.Treasury, owner);

            ctx.Jail = CreateComponent<JailManager>($"JailManager P{owner + 1}", parent);
            ctx.Jail.Initialize(grid, ctx.JobBoard, ctx.Lair, ctx.Treasury, owner);

            ctx.Bridge = CreateComponent<BridgeManager>($"BridgeManager P{owner + 1}", parent);
            ctx.Bridge.Initialize(grid, ctx.Lair, ctx.Treasury, owner);

            ctx.ImplingSpawner = CreateComponent<ImplingSpawner>($"ImplingSpawner P{owner + 1}", parent);
            ctx.ImplingSpawner.Initialize(ctx.JobBoard, grid, ctx.Treasury, ctx.Throne, ctx.SlimeHatchery, ctx.Tavern, owner);

            ctx.GremlinSpawner = CreateComponent<GremlinSpawner>($"GremlinSpawner P{owner + 1}", parent);
            ctx.GremlinSpawner.Initialize(grid, ctx.Portal, ctx.Lair, ctx.SlimeHatchery, ctx.TrainingRoom, ctx.Tavern, ctx.Treasury, owner);
            ctx.Portal.SeedPool(GremlinAgent.CreatureKind, StartingGremlinPoolCount);

            ctx.WarlockSpawner = CreateComponent<WarlockSpawner>($"WarlockSpawner P{owner + 1}", parent);
            ctx.WarlockSpawner.Initialize(grid, ctx.Portal, ctx.Lair, ctx.Library, ctx.SlimeHatchery, ctx.Tavern, ctx.TrainingRoom, ctx.Treasury, owner);
            ctx.Portal.SeedPool(WarlockAgent.CreatureKind, StartingWarlockPoolCount);

            ctx.MazeRattlerSpawner = CreateComponent<MazeRattlerSpawner>($"MazeRattlerSpawner P{owner + 1}", parent);
            ctx.MazeRattlerSpawner.Initialize(grid, ctx.Portal, ctx.Lair, ctx.Jail, ctx.Tavern, ctx.TrainingRoom, ctx.Treasury, owner);
            ctx.Portal.SeedPool(MazeRattlerAgent.CreatureKind, StartingMazeRattlerPoolCount);

            ctx.ElfSpawner = CreateComponent<ElfSpawner>($"ElfSpawner P{owner + 1}", parent);
            ctx.ElfSpawner.Initialize(grid, ctx.Portal, ctx.Lair, ctx.Tavern, ctx.Treasury);

            ctx.ConversionClass = CreateComponent<ConversionClassManager>($"ConversionClassManager P{owner + 1}", parent);
            ctx.ConversionClass.Initialize(grid, ctx.Lair, ctx.Treasury, ctx.Jail, ctx.GremlinSpawner, ctx.WarlockSpawner, ctx.MazeRattlerSpawner, ctx.ElfSpawner, owner);

            ctx.BeanCounterSpawner = CreateComponent<BeanCounterSpawner>($"BeanCounterSpawner P{owner + 1}", parent);
            ctx.BeanCounterSpawner.Initialize(grid, ctx.Portal, ctx.Lair, ctx.ConversionClass, ctx.Jail, ctx.Tavern, ctx.Treasury, owner);
            ctx.Portal.SeedPool(BeanCounterAgent.CreatureKind, StartingBeanCounterPoolCount);

            return ctx;
        }

        /// Loaded-level room reconstruction, one keeper at a time — feeds
        /// RoomReconstruction.RestoreRooms only the footprints owned by
        /// contexts[i] (a stray/out-of-range room owner falls to context
        /// 0), dispatched to that context's own managers. Same
        /// IRestorableRoomManager path the single-manager version used.
        private static void RestoreWorldRoomsPerOwner(DungeonGrid grid, Dictionary<string, List<Vector2Int>> roomFootprints, Dictionary<string, int> roomOwners, KeeperContext[] contexts)
        {
            for (int i = 0; i < contexts.Length; i++)
            {
                var ctx = contexts[i];
                var ownFootprints = new Dictionary<string, List<Vector2Int>>();
                // The clamped owner, not the raw saved one — a stray/out-of-
                // range owner is reassigned to keeper 0 here, and
                // RoomReconstruction hands this value straight to
                // RestoreRoom, so a manager that acts on it (BridgeManager
                // stamps the tile's OwnerId with it) never sees an index
                // with no KeeperContext.
                var ownOwners = new Dictionary<string, int>();
                foreach (var entry in roomFootprints)
                {
                    var roomOwner = roomOwners.TryGetValue(entry.Key, out var o) ? o : 0;
                    if (roomOwner < 0 || roomOwner >= contexts.Length)
                    {
                        roomOwner = 0;
                    }
                    if (roomOwner == i)
                    {
                        ownFootprints[entry.Key] = entry.Value;
                        ownOwners[entry.Key] = roomOwner;
                    }
                }

                if (ownFootprints.Count == 0)
                {
                    continue;
                }

                var roomManagers = new Dictionary<RoomDesignTool, IRestorableRoomManager>
                {
                    { RoomDesignTool.Lair, ctx.Lair },
                    { RoomDesignTool.Treasury, ctx.Treasury },
                    { RoomDesignTool.SlimeHatchery, ctx.SlimeHatchery },
                    { RoomDesignTool.Tavern, ctx.Tavern },
                    { RoomDesignTool.TrainingRoom, ctx.TrainingRoom },
                    { RoomDesignTool.Library, ctx.Library },
                    { RoomDesignTool.Jail, ctx.Jail },
                    { RoomDesignTool.ConversionClass, ctx.ConversionClass },
                    { RoomDesignTool.Bridge, ctx.Bridge },
                };
                RoomReconstruction.RestoreRooms(grid, ownFootprints, ownOwners, roomManagers);
            }
        }

        /// Sweeps the grid once and queues a claim job for every Unclaimed
        /// Floor tile on every keeper's BuilderJobBoard — see
        /// BuilderJobBoard.QueueClaimJob for why every board and not just
        /// one. Dug-out floor already fired FloorNeedsClaim during
        /// CompleteDig; this only matters for floor that was authored
        /// Unclaimed and loaded straight in without ever being dug. Room
        /// tiles are Claimed Floor so the Ownership check skips them.
        private static void QueuePreplacedClaimJobs(DungeonGrid grid, KeeperContext[] contexts)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                for (int y = 0; y < grid.Height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    var tile = grid.GetTile(coord);
                    if (tile.Type != TileType.Floor || tile.Ownership != TileOwnership.Unclaimed)
                    {
                        continue;
                    }

                    foreach (var ctx in contexts)
                    {
                        ctx.JobBoard.QueueClaimJob(coord);
                    }
                }
            }
        }

        /// A spread-out fallback Throne/Portal coord for a loaded level's
        /// player i whose ThroneRoom/PortalRoom structure is missing from
        /// the save — so a degenerate/hand-edited save doesn't stack every
        /// keeper's landmarks on the exact same tile.
        private static Vector2Int SpreadFallbackCoord(DungeonGrid grid, int playerIndex)
        {
            return new Vector2Int(
                Mathf.Clamp(grid.Width / 2 + playerIndex * 6, 1, grid.Width - 2),
                grid.Height / 2);
        }

        private static T CreateComponent<T>(string name) where T : Component
        {
            return CreateComponent<T>(name, parent: null);
        }

        private static T CreateComponent<T>(string name, Transform parent) where T : Component
        {
            var go = new GameObject(name);
            if (parent != null)
            {
                go.transform.SetParent(parent, worldPositionStays: false);
            }
            return go.AddComponent<T>();
        }
    }
}

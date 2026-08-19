using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.CameraControl;
using KeepersDomain.Rooms;
using KeepersDomain.Implings;
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

        // 5x5 room, so the 3x3 Chaos Core structure sits centered with a
        // 1-tile walkable margin around it.
        private const int ChaosCoreRoomHalfSize = 2;

        // 3x3 room around the portal — bigger than a single tile so the
        // staircase reads as sitting in an actual room, not just a corridor cell.
        private const int PortalRoomHalfSize = 1;

        // 3x3 Treasury room, mirroring the Portal's "own room off a
        // one-tile corridor" shape but placed on Chaos Core's north side so
        // it doesn't collide with the Portal's east-side layout.
        private const int TreasuryRoomHalfSize = 1;

        private const int StartingGold = 200;

        // TEMPORARY — for testing only. See CarveTemporaryTestRooms; delete
        // this const, its carve call, and that method once done testing.
        private const int TestRoomSize = 4;

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
            RemoveStrayCameras();
            CreateSun();

            var grid = CreateComponent<DungeonGrid>("DungeonGrid");
            grid.Initialize(GridWidth, GridHeight, CellSize);

            // Chaos Core sits at the grid center; the portal gets its own
            // room to the east, joined by a single one-tile corridor.
            var chaosCoreCenter = new Vector2Int(GridWidth / 2, GridHeight / 2);
            var corridorCoord = chaosCoreCenter + new Vector2Int(ChaosCoreRoomHalfSize + 1, 0);
            var portalCoord = corridorCoord + new Vector2Int(PortalRoomHalfSize + 1, 0);

            // Both rooms carve buildable by default, then re-carve just the
            // footprint that actually has a fixed structure on it (Chaos
            // Core's 3x3 platform, the Portal's single staircase tile) back
            // to unbuildable — the walkable margin around each stays open
            // for the player's very first Lair. Without at least one
            // buildable tile from the start, there'd be no way to ever place
            // a first Lair (and so no first impling) since nothing exists
            // yet to dig new floor either.
            grid.CarveRoom(chaosCoreCenter, ChaosCoreRoomHalfSize);
            grid.CarveRoom(chaosCoreCenter, 1, isBuildable: false);
            grid.CarveRoom(corridorCoord, 0, isBuildable: false);
            grid.CarveRoom(portalCoord, PortalRoomHalfSize);
            grid.CarveRoom(portalCoord, 0, isBuildable: false);

            // Treasury sits north of Chaos Core, its own room off a
            // single-tile corridor. Carved buildable (unlike Chaos
            // Core/Portal's fixed structure tiles) since the starting
            // Treasury is placed the same way a player-built one is — see
            // TreasuryManager.TryPlaceTreasury below — rather than being a
            // permanent landmark; only the corridor stays unbuildable, so
            // a room can never block the one path between the two rooms.
            var treasuryCorridorCoord = chaosCoreCenter + new Vector2Int(0, ChaosCoreRoomHalfSize + 1);
            var treasuryCoord = treasuryCorridorCoord + new Vector2Int(0, TreasuryRoomHalfSize + 1);
            grid.CarveRoom(treasuryCoord, TreasuryRoomHalfSize);
            grid.CarveRoom(treasuryCorridorCoord, 0, isBuildable: false);

            // TEMPORARY — for testing only, see CarveTemporaryTestRooms.
            CarveTemporaryTestRooms(grid, chaosCoreCenter);

            // Scatter resource-wall veins into whatever's still Rock now
            // that every starting room/corridor is carved to Floor — those
            // tiles are automatically skipped (ScatterResourceWalls only
            // ever touches Rock).
            ScatterResourceWalls(grid);

            var chaosCore = CreateComponent<ChaosCore>("ChaosCore");
            chaosCore.Initialize(chaosCoreCenter, grid);

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

            // The starting Treasury is placed exactly like a player-built
            // one — PlaceStartingTreasury, not a direct tile-registration
            // loop — so it's a real, sellable room from the moment the game
            // starts, not a permanent landmark. Unlike TryPlaceTreasury,
            // this skips the gold cost — it's terrain generation, not a
            // purchase, and there'd be no gold to pay it with yet anyway.
            treasuryManager.PlaceStartingTreasury(
                treasuryCoord - new Vector2Int(TreasuryRoomHalfSize, TreasuryRoomHalfSize),
                treasuryCoord + new Vector2Int(TreasuryRoomHalfSize, TreasuryRoomHalfSize));

            // Starting gold, seeded onto the room's middle tile so the
            // player isn't broke at the very start of the game.
            treasuryManager.Deposit(treasuryCoord, StartingGold);

            // Slime Hatchery/Bacon Beacon are placed by the player exactly
            // like Lair/Treasury — no starting instance carved here — but
            // both subscribe to LairManager.RoomSold the same way
            // TreasuryManager does, and charge their own per-tile cost out
            // of TreasuryManager same as LairManager, so they need both to
            // exist first.
            var slimeHatcheryManager = CreateComponent<SlimeHatcheryManager>("SlimeHatcheryManager");
            slimeHatcheryManager.Initialize(grid, lairManager, treasuryManager);

            var baconBeaconManager = CreateComponent<BaconBeaconManager>("BaconBeaconManager");
            baconBeaconManager.Initialize(grid, lairManager, treasuryManager);

            var implingSpawner = CreateComponent<ImplingSpawner>("ImplingSpawner");
            implingSpawner.Initialize(jobBoard, grid, treasuryManager, chaosCore, slimeHatcheryManager, baconBeaconManager);

            SpawnStartingImplings(implingSpawner, chaosCoreCenter);

            var camera = CreateIsoCamera(grid);

            var interactionController = CreateComponent<TileInteractionController>("TileInteractionController");
            interactionController.Initialize(camera, grid, jobBoard, lairManager, treasuryManager, slimeHatcheryManager, baconBeaconManager, implingSpawner);

            var bottomMenuBar = CreateComponent<BottomMenuBar>("BottomMenuBar");
            bottomMenuBar.Initialize(grid, jobBoard, interactionController, treasuryManager, chaosCore, baconBeaconManager);
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
            var lightGO = new GameObject("Sun");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static Camera CreateIsoCamera(DungeonGrid grid)
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
            // Pan margin scaled by the same +50% as GridWidth/GridHeight
            // (15f base -> 22.5f), so the camera can still reach the whole
            // enlarged map instead of the old grid's bounds.
            const float panMargin = 22.5f;
            isoCam.SetPanBounds(new Vector2(camPos.x - panMargin, camPos.z - panMargin), new Vector2(camPos.x + panMargin, camPos.z + panMargin));

            return camera;
        }

        /// TEMPORARY — for testing only, no gameplay purpose. 3 extra
        /// empty, Claimed 4x4 rooms off Chaos Core, each behind its own
        /// single-tile corridor the same way Portal/Treasury are: one west,
        /// one south, and a third further west beyond the first (there
        /// were only two cardinal sides left free, so the third chains off
        /// the first rather than bordering Chaos Core directly). Delete
        /// this method, its call in Init(), and TestRoomSize once done.
        private static void CarveTemporaryTestRooms(DungeonGrid grid, Vector2Int chaosCoreCenter)
        {
            var westCorridor = chaosCoreCenter + new Vector2Int(-(ChaosCoreRoomHalfSize + 1), 0);
            var westRoomOrigin = westCorridor + new Vector2Int(-TestRoomSize, -TestRoomSize / 2);
            grid.CarveRect(westRoomOrigin, TestRoomSize, TestRoomSize);
            grid.CarveRoom(westCorridor, 0, isBuildable: false);

            var southCorridor = chaosCoreCenter + new Vector2Int(0, -(ChaosCoreRoomHalfSize + 1));
            var southRoomOrigin = southCorridor + new Vector2Int(-TestRoomSize / 2, -TestRoomSize);
            grid.CarveRect(southRoomOrigin, TestRoomSize, TestRoomSize);
            grid.CarveRoom(southCorridor, 0, isBuildable: false);

            var farWestCorridor = westRoomOrigin + new Vector2Int(-1, TestRoomSize / 2);
            var farWestRoomOrigin = farWestCorridor + new Vector2Int(-TestRoomSize, -TestRoomSize / 2);
            grid.CarveRect(farWestRoomOrigin, TestRoomSize, TestRoomSize);
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

        /// 4 starting implings, one on each corner of the Chaos Core's 3x3
        /// platform — those tiles are still plain walkable Floor underneath
        /// (the platform's just a visual overlay, see ChaosCore.Initialize),
        /// so SpawnImplingAt — the same mana-summon the Impling menu's
        /// button uses — works directly here without needing a Lair first
        /// (implings are mana-conjured, not Lair-dependent). Goes through
        /// ImplingSpawner rather than a direct instantiate so these
        /// implings reserve their upkeep mana exactly like any other spawn
        /// (see ImplingSpawner.SpawnImpling).
        private static void SpawnStartingImplings(ImplingSpawner implingSpawner, Vector2Int chaosCoreCenter)
        {
            var offset = ChaosCore.PlatformHalfSize;
            implingSpawner.SpawnImplingAt(chaosCoreCenter + new Vector2Int(-offset, -offset));
            implingSpawner.SpawnImplingAt(chaosCoreCenter + new Vector2Int(offset, -offset));
            implingSpawner.SpawnImplingAt(chaosCoreCenter + new Vector2Int(-offset, offset));
            implingSpawner.SpawnImplingAt(chaosCoreCenter + new Vector2Int(offset, offset));
        }

        private static T CreateComponent<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            return go.AddComponent<T>();
        }
    }
}

using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.CameraControl;
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

        // Starting Lair/Slime Hatchery/Bacon Beacon, each their own 4x4
        // room chained off Chaos Core the same one-tile-corridor way as
        // Treasury/Library/Training Room — see CarveStartingUtilityRooms.
        // 4x4 satisfies Bacon Beacon's own MinFootprintSize exactly and
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

            // Library chains off Treasury's east side via its own one-tile
            // corridor — carved buildable (unlike Chaos Core/Portal's fixed
            // structure tiles), same as Treasury itself, since it's about
            // to become a real, sellable Library room below rather than a
            // permanent landmark; only the corridor stays unbuildable.
            var libraryCorridorCoord = treasuryCoord + new Vector2Int(TreasuryRoomHalfSize + 1, 0);
            var libraryRoomOrigin = libraryCorridorCoord + new Vector2Int(1, -2);
            var libraryRoomEndCoord = libraryRoomOrigin + new Vector2Int(LibraryRoomWidth - 1, LibraryRoomHeight - 1);
            grid.CarveRect(libraryRoomOrigin, LibraryRoomWidth, LibraryRoomHeight);
            grid.CarveRoom(libraryCorridorCoord, 0, isBuildable: false);

            // Training Room chains further east off the Library's own east
            // side, same one-tile-corridor pattern.
            var trainingRoomStartCorridorCoord = libraryRoomOrigin + new Vector2Int(LibraryRoomWidth, 1);
            var trainingRoomStartOrigin = trainingRoomStartCorridorCoord + new Vector2Int(1, -1);
            var trainingRoomStartEndCoord = trainingRoomStartOrigin + new Vector2Int(TrainingRoomStartWidth - 1, TrainingRoomStartHeight - 1);
            grid.CarveRect(trainingRoomStartOrigin, TrainingRoomStartWidth, TrainingRoomStartHeight);
            grid.CarveRoom(trainingRoomStartCorridorCoord, 0, isBuildable: false);

            CarveStartingUtilityRooms(grid, chaosCoreCenter,
                out var lairRoomOrigin, out var lairRoomEndCoord,
                out var hatcheryRoomOrigin, out var hatcheryRoomEndCoord,
                out var beaconRoomOrigin, out var beaconRoomEndCoord);

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

            // Starting gold, spread across every tile the starting Treasury
            // just registered (AddGold, not Deposit — Deposit targets one
            // specific tile and caps at GoldCapacityPerTile, which silently
            // dropped everything past 500 when StartingGold grew past a
            // single tile's capacity).
            treasuryManager.AddGold(StartingGold);

            // Slime Hatchery/Bacon Beacon get a starting instance too (see
            // PlaceStartingHatchery/PlaceStartingBeacon below, once the
            // utility-room footprints are carved) on top of being
            // player-placeable like Lair/Treasury — both subscribe to
            // LairManager.RoomSold the same way TreasuryManager does, and
            // charge their own per-tile cost out of TreasuryManager same as
            // LairManager, so they need both to exist first.
            var slimeHatcheryManager = CreateComponent<SlimeHatcheryManager>("SlimeHatcheryManager");
            slimeHatcheryManager.Initialize(grid, lairManager, treasuryManager);

            var baconBeaconManager = CreateComponent<BaconBeaconManager>("BaconBeaconManager");
            baconBeaconManager.Initialize(grid, lairManager, treasuryManager);

            // Training Room follows the same "player-placed, subscribes to
            // RoomSold" wiring as Hatchery/Beacon — placement and visuals
            // only for now, see TrainingRoomManager's own header comment.
            var trainingRoomManager = CreateComponent<TrainingRoomManager>("TrainingRoomManager");
            trainingRoomManager.Initialize(grid, lairManager, treasuryManager);

            // Library follows the same "player-placed, subscribes to
            // RoomSold" wiring as Hatchery/Beacon/Training Room — placement
            // and visuals only for now, see LibraryManager's own header
            // comment.
            var libraryManager = CreateComponent<LibraryManager>("LibraryManager");
            libraryManager.Initialize(grid, lairManager, treasuryManager);

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

            // Jail follows the same "player-placed, subscribes to
            // RoomSold" wiring as Hatchery/Beacon/Training Room/Library —
            // placement and visuals only for now (no Maze Rattler/capture
            // mechanic exists yet, see JailManager's own header comment).
            // No starting instance — unlike Library/Training Room, there's
            // no reason yet to force one into the starting domain.
            var jailManager = CreateComponent<JailManager>("JailManager");
            jailManager.Initialize(grid, jobBoard, lairManager, treasuryManager);

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
            baconBeaconManager.PlaceStartingBeacon(beaconRoomOrigin, beaconRoomEndCoord);

            var implingSpawner = CreateComponent<ImplingSpawner>("ImplingSpawner");
            implingSpawner.Initialize(jobBoard, grid, treasuryManager, chaosCore, slimeHatcheryManager, baconBeaconManager);

            SpawnStartingImplings(implingSpawner, chaosCoreCenter);

            // First non-Imp creature — recruited out of the Portal's pool
            // (see Portal.SeedPool/TryTakeFromPool), not placed freely; see
            // GremlinAgent/GremlinSpawner's own header comments for its
            // join requirements and priority-list AI.
            var gremlinSpawner = CreateComponent<GremlinSpawner>("GremlinSpawner");
            gremlinSpawner.Initialize(grid, portal, lairManager, slimeHatcheryManager, trainingRoomManager, baconBeaconManager, treasuryManager);
            portal.SeedPool(GremlinAgent.CreatureKind, StartingGremlinPoolCount);

            // Second non-Imp creature, and the first "intelligent" one — see
            // WarlockAgent/WarlockSpawner's own header comments for its
            // extra join requirements (a Lair tile, a 3x3+ Library, and
            // Hatchery/Beacon capacity) on top of pool availability.
            var warlockSpawner = CreateComponent<WarlockSpawner>("WarlockSpawner");
            warlockSpawner.Initialize(grid, portal, lairManager, libraryManager, slimeHatcheryManager, baconBeaconManager, trainingRoomManager, treasuryManager);
            portal.SeedPool(WarlockAgent.CreatureKind, StartingWarlockPoolCount);

            var camera = CreateIsoCamera(grid);

            var interactionController = CreateComponent<TileInteractionController>("TileInteractionController");
            interactionController.Initialize(camera, grid, jobBoard, lairManager, treasuryManager, slimeHatcheryManager, baconBeaconManager, trainingRoomManager, libraryManager, jailManager, implingSpawner);

            var bottomMenuBar = CreateComponent<BottomMenuBar>("BottomMenuBar");
            bottomMenuBar.Initialize(grid, jobBoard, interactionController, treasuryManager, chaosCore, baconBeaconManager, trainingRoomManager, libraryManager, jailManager, gremlinSpawner, warlockSpawner);
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

        /// 3 utility rooms off Chaos Core, each behind its own single-tile
        /// corridor the same way Portal/Treasury are: Lair to the west,
        /// Slime Hatchery to the south, and Bacon Beacon further west
        /// beyond the Lair (there were only two cardinal sides left free,
        /// so the third chains off the first rather than bordering Chaos
        /// Core directly). Only carves Floor here — filling each with its
        /// actual room happens later in Init(), once the relevant manager
        /// exists (see LairManager.PlaceStartingLair and friends), same
        /// staging Library/Training Room already use.
        private static void CarveStartingUtilityRooms(DungeonGrid grid, Vector2Int chaosCoreCenter,
            out Vector2Int lairRoomOrigin, out Vector2Int lairRoomEndCoord,
            out Vector2Int hatcheryRoomOrigin, out Vector2Int hatcheryRoomEndCoord,
            out Vector2Int beaconRoomOrigin, out Vector2Int beaconRoomEndCoord)
        {
            var westCorridor = chaosCoreCenter + new Vector2Int(-(ChaosCoreRoomHalfSize + 1), 0);
            lairRoomOrigin = westCorridor + new Vector2Int(-StartingUtilityRoomSize, -StartingUtilityRoomSize / 2);
            lairRoomEndCoord = lairRoomOrigin + new Vector2Int(StartingUtilityRoomSize - 1, StartingUtilityRoomSize - 1);
            grid.CarveRect(lairRoomOrigin, StartingUtilityRoomSize, StartingUtilityRoomSize);
            grid.CarveRoom(westCorridor, 0, isBuildable: false);

            var southCorridor = chaosCoreCenter + new Vector2Int(0, -(ChaosCoreRoomHalfSize + 1));
            hatcheryRoomOrigin = southCorridor + new Vector2Int(-StartingUtilityRoomSize / 2, -StartingUtilityRoomSize);
            hatcheryRoomEndCoord = hatcheryRoomOrigin + new Vector2Int(StartingUtilityRoomSize - 1, StartingUtilityRoomSize - 1);
            grid.CarveRect(hatcheryRoomOrigin, StartingUtilityRoomSize, StartingUtilityRoomSize);
            grid.CarveRoom(southCorridor, 0, isBuildable: false);

            var farWestCorridor = lairRoomOrigin + new Vector2Int(-1, StartingUtilityRoomSize / 2);
            beaconRoomOrigin = farWestCorridor + new Vector2Int(-StartingUtilityRoomSize, -StartingUtilityRoomSize / 2);
            beaconRoomEndCoord = beaconRoomOrigin + new Vector2Int(StartingUtilityRoomSize - 1, StartingUtilityRoomSize - 1);
            grid.CarveRect(beaconRoomOrigin, StartingUtilityRoomSize, StartingUtilityRoomSize);
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

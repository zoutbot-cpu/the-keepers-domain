using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using KeepersDomain.AI;
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
    /// Builds the playable world for offline play, Skirmish, Continue and the
    /// multiplayer host — every keeper's KeeperContext, rooms, creatures and
    /// HUD — either from a LevelData or as the legacy from-scratch
    /// single-keeper map.
    internal static class WorldBuilder
    {
        // +50% over the original 24x24 prototype size, to give ongoing
        // development more room to work with.
        internal const int GridWidth = 36;
        internal const int GridHeight = 36;
        internal const float CellSize = 1f;

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

        /// NetLobby.OnHostBuildGame — every player readied up and the host
        /// hit Start. Build the authoritative world from the map picked in
        /// the lobby (empty id = fresh procedural), then spawn the
        /// session-lifetime networked objects and bind them so tile changes
        /// replicate. The client needs no copy of the map file — it arrives
        /// as NetGame's tile snapshot.
        internal static void BuildHostGame()
        {
            var mapId = NetLobby.Instance != null ? NetLobby.Instance.MapId : "level1";
            if (mapId == NetLobby.ProceduralMapId)
            {
                var playerCount = Mathf.Clamp(
                    NetLobby.Instance != null ? NetLobby.Instance.Players.Count : 2, 2, 4);
                var seed = new System.Random().Next();
                Debug.Log($"GameBootstrap: procedural multiplayer map — seed {seed}, {playerCount} players.");
                BuildWorld(MapGenerator.Generate(new MapGenSettings
                {
                    Seed = seed,
                    PlayerCount = playerCount,
                    MapWidth = GameBootstrap.GeneratedMapSize(playerCount),
                    MapHeight = GameBootstrap.GeneratedMapSize(playerCount),
                    Multiplayer = true
                }));
            }
            else
            {
                BuildWorld(LevelFileIO.Load(mapId));
            }

            var grid = Object.FindAnyObjectByType<DungeonGrid>();

            var netGameGo = Object.Instantiate(Resources.Load<GameObject>("Net/NetGame"));
            var netGame = netGameGo.GetComponent<NetGame>();
            // Bind BEFORE Spawn — NetGame.OnNetworkSpawn writes the map-size
            // netvars, which then ride the spawn message to the (already
            // connected, since we're coming from the lobby) client. Bind
            // after Spawn and the client sizes its grid from 0.
            netGame.HostBind(grid);
            netGameGo.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
            // Every KeeperContext (and its room managers) exists by now —
            // relay their lair-claim / treasury-gold visual state to
            // whichever client joins (see NetGame.HostBindKeeperRooms).
            netGame.HostBindKeeperRooms();

            // One economy mirror per keeper, for the client HUD.
            var keeperPrefab = Resources.Load<GameObject>("Net/KeeperNetState");
            if (KeeperContext.All != null)
            {
                foreach (var ctx in KeeperContext.All)
                {
                    var go = Object.Instantiate(keeperPrefab);
                    go.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
                    go.GetComponent<KeeperNetState>().HostBind(ctx);
                }
            }

            SceneSetup.CreateComponent<NetHud>("NetHud").Initialize(isHost: true);
            SceneSetup.CreateComponent<NetPauseScreen>("NetPauseScreen");
            SceneSetup.CreateComponent<NetChat>("NetChat");
        }

        internal static void BuildWorld(LevelData data = null)
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
            SceneSetup.RemoveStrayCameras();
            SceneSetup.CreateSun();

            var grid = SceneSetup.CreateComponent<DungeonGrid>("DungeonGrid");
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
            var liquidAnimator = SceneSetup.CreateComponent<LiquidAnimator>("LiquidAnimator");
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
                    contexts[i].Tavern.AddBacon(specs[i].StartingBacon);
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

            // Local-keeper fog of war. Every path through BuildWorld gets one
            // — offline "Start Game", "Skirmish (generated)", "Continue", and
            // the multiplayer host (BuildHostGame calls BuildWorld). The
            // networked client builds its own in BuildClientWorld; the Level
            // Designer never creates one, so its DungeonGrid.Fog stays null
            // and the whole map renders live. Seeds the local Throne Room's footprint
            // as explored so it shows on the map from the start.
            var fogOfWar = SceneSetup.CreateComponent<FogOfWar>("FogOfWar");
            fogOfWar.Initialize(grid, localPlayerIndex, contexts[localPlayerIndex].ThroneCoord, ThroneRoomHalfSize);

            // Only the local keeper's own queued-job icons are drawn (the
            // debug player switcher moves this — see LocalPlayerController).
            grid.LocalViewerOwnerId = contexts[localPlayerIndex].OwnerId;

            // Pan margin: 22.5f for a freshly generated map (the +50%-scaled
            // gameplay grid — 15f base -> 22.5f — kept exactly as tuned), but
            // a loaded level can be any size up to the Level Designer's 256,
            // so scale to the actual footprint the same way
            // SetUpLevelDesignerWorld does. Opens centered on the local
            // player's Throne Room (see CreateIsoCamera's focusGroundPoint).
            var panMargin = data != null
                ? Mathf.Max(grid.Width, grid.Height) * CellSize * 0.5f + 10f
                : 22.5f;
            var camera = SceneSetup.CreateIsoCamera(grid, panMargin, grid.GridToWorld(throneCoords[localPlayerIndex]));

            // Input / grab hand / HUD are built once and bound to the local
            // keeper's context; the debug player switcher (BottomMenuBar,
            // only shown when contexts.Length > 1) repoints all three plus
            // the camera through LocalPlayerController.SetActivePlayer.
            var minionGrabController = SceneSetup.CreateComponent<MinionGrabController>("MinionGrabController");
            minionGrabController.Initialize(camera, grid, contexts, localPlayerIndex);

            var interactionController = SceneSetup.CreateComponent<TileInteractionController>("TileInteractionController");
            interactionController.Initialize(camera, grid, contexts, minionGrabController, localPlayerIndex);

            var localPlayerController = SceneSetup.CreateComponent<LocalPlayerController>("LocalPlayerController");
            var bottomMenuBar = SceneSetup.CreateComponent<BottomMenuBar>("BottomMenuBar");
            bottomMenuBar.Initialize(grid, contexts, interactionController, localPlayerController, localPlayerIndex, fog: fogOfWar);
            localPlayerController.Initialize(camera, grid, contexts, interactionController, minionGrabController, bottomMenuBar, localPlayerIndex);

            // Lose-condition: a Throne beaten to 0 HP ends the match (see
            // ThroneRoom.Defeated / HandleThroneDefeated).
            foreach (var ctx in contexts)
            {
                if (ctx?.Throne != null)
                {
                    ctx.Throne.Defeated += HandleThroneDefeated;
                }
            }

            // Rival keepers flagged AI (Skirmish's P2, a Level Designer "AI"
            // slot) get a KeeperAI to play them — see its header.
            KeeperAI.CreateFor(contexts, grid, contexts[localPlayerIndex].OwnerId);

            SaveStartingLevelAsLevel1(grid, contexts[0].ThroneCoord, contexts[0].PortalCoord);
        }

        /// A keeper's Throne Room just hit 0 HP. Shows the match-over screen
        /// — DEFEAT if it's the local player's (index 0), VICTORY once every
        /// rival's Throne is also down. On a networked host it also tells the
        /// client (combat isn't host-authoritative-replicated yet, so this
        /// only ever fires from the host's own simulation, but the result
        /// still has to reach the client's screen).
        private static void HandleThroneDefeated(int defeatedOwnerId)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                NetGame.Instance?.NotifyMatchOverRpc(defeatedOwnerId);
            }

            const int localOwner = 0;
            if (defeatedOwnerId == localOwner)
            {
                EndScreen.Show(victory: false, "Your Throne Room has fallen.");
                return;
            }

            var all = KeeperContext.All;
            if (all != null)
            {
                foreach (var ctx in all)
                {
                    if (ctx?.Throne != null && ctx.Throne.OwnerId != localOwner && !ctx.Throne.IsDefeated)
                    {
                        Debug.Log($"GameBootstrap: rival keeper P{defeatedOwnerId + 1} defeated; others still stand.");
                        return;
                    }
                }
            }

            EndScreen.Show(victory: true, "Every rival Throne Room has fallen.");
        }

        /// Snapshots the freshly-built starting world into a "level1" save
        /// file (via CaptureWorldToLevelData, the same path SaveGame uses),
        /// so it shows up in the Level Designer's Load list ready to tweak.
        /// Only writes it once — overwriting "level1" on every launch would
        /// clobber any edits saved back onto it since; skip entirely once
        /// the file exists (it's also bundled in the build, so in practice
        /// this is now a no-op — kept for a truly first-ever install with
        /// no bundled level and no save).
        private static void SaveStartingLevelAsLevel1(DungeonGrid grid, Vector2Int throneRoomCenter, Vector2Int portalCoord)
        {
            if (LevelFileIO.Load("level1") != null || KeeperContext.All == null || KeeperContext.All.Length == 0)
            {
                return;
            }

            LevelFileIO.Save("level1", GameSave.CaptureWorldToLevelData(grid, KeeperContext.All));
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
                    case TileType.UnholyGround:
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

                // A mid-game save records each creature's level/exp (0 for a
                // hand-authored level). The spawners are void, so grab the
                // just-spawned agent off its species roster (spawn order ==
                // roster order) and restore its progress.
                switch (creatureData.Kind)
                {
                    case EditorCreatureKind.Imp:
                        var impsBefore = ImplingAgent.All.Count;
                        ctx.ImplingSpawner.SpawnImplingAt(coord);
                        if (ImplingAgent.All.Count > impsBefore)
                        {
                            RestoreProgress(ImplingAgent.All[ImplingAgent.All.Count - 1].Creature, creatureData);
                        }
                        break;
                    case EditorCreatureKind.Gremlin:
                        ctx.GremlinSpawner.SpawnGremlin(coord, ownerId);
                        RestoreProgress(GremlinAgent.All[GremlinAgent.All.Count - 1].Creature, creatureData);
                        break;
                    case EditorCreatureKind.Warlock:
                        ctx.WarlockSpawner.SpawnWarlock(coord, ownerId);
                        RestoreProgress(WarlockAgent.All[WarlockAgent.All.Count - 1].Creature, creatureData);
                        break;
                    case EditorCreatureKind.MazeRattler:
                        ctx.MazeRattlerSpawner.SpawnMazeRattler(coord, ownerId);
                        RestoreProgress(MazeRattlerAgent.All[MazeRattlerAgent.All.Count - 1].Creature, creatureData);
                        break;
                    case EditorCreatureKind.BeanCounter:
                        ctx.BeanCounterSpawner.SpawnBeanCounter(coord, ownerId);
                        RestoreProgress(BeanCounterAgent.All[BeanCounterAgent.All.Count - 1].Creature, creatureData);
                        break;
                    case EditorCreatureKind.Elf:
                        ctx.ElfSpawner.SpawnElf(coord, ownerId);
                        RestoreProgress(ElfAgent.All[ElfAgent.All.Count - 1].Creature, creatureData);
                        break;
                }
            }
        }

        private static void RestoreProgress(KeepersDomain.Creatures.Creature creature, LevelCreatureData data)
        {
            if (creature != null && data.Level > 0)
            {
                creature.SetProgress(data.Level, data.Exp);
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
            public int StartingBacon;
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
                    StartingBacon = p.StartingBacon,
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

            ctx.Throne = SceneSetup.CreateComponent<ThroneRoom>($"ThroneRoom P{owner + 1}", parent);
            ctx.Throne.PlayerColor = spec.Color;
            ctx.Throne.Initialize(throneCoord, grid, owner, spec.StartingMana);

            ctx.Portal = SceneSetup.CreateComponent<Portal>($"Portal P{owner + 1}", parent);
            ctx.Portal.Initialize(portalCoord, grid);

            ctx.JobBoard = SceneSetup.CreateComponent<BuilderJobBoard>($"BuilderJobBoard P{owner + 1}", parent);
            ctx.JobBoard.Initialize(grid, owner);

            // LairManager <-> TreasuryManager mutual reference — created
            // first, then wired in either order (C# events / field
            // assignment don't need the other's Initialize to have run).
            ctx.Lair = SceneSetup.CreateComponent<LairManager>($"LairManager P{owner + 1}", parent);
            ctx.Treasury = SceneSetup.CreateComponent<TreasuryManager>($"TreasuryManager P{owner + 1}", parent);
            ctx.Treasury.Initialize(grid, ctx.Lair, owner);
            ctx.Lair.Initialize(grid, ctx.Treasury, owner);

            ctx.SlimeHatchery = SceneSetup.CreateComponent<SlimeHatcheryManager>($"SlimeHatcheryManager P{owner + 1}", parent);
            ctx.SlimeHatchery.Initialize(grid, ctx.Lair, ctx.Treasury, simulateBreeding: true, ownerId: owner);

            ctx.Tavern = SceneSetup.CreateComponent<TavernManager>($"TavernManager P{owner + 1}", parent);
            ctx.Tavern.Initialize(grid, ctx.Lair, ctx.Treasury, owner);

            ctx.TrainingRoom = SceneSetup.CreateComponent<TrainingRoomManager>($"TrainingRoomManager P{owner + 1}", parent);
            ctx.TrainingRoom.Initialize(grid, ctx.Lair, ctx.Treasury, owner);

            ctx.Library = SceneSetup.CreateComponent<LibraryManager>($"LibraryManager P{owner + 1}", parent);
            ctx.Library.Initialize(grid, ctx.Lair, ctx.Treasury, owner);

            ctx.Jail = SceneSetup.CreateComponent<JailManager>($"JailManager P{owner + 1}", parent);
            ctx.Jail.Initialize(grid, ctx.Lair, ctx.Treasury, owner);

            ctx.Bridge = SceneSetup.CreateComponent<BridgeManager>($"BridgeManager P{owner + 1}", parent);
            ctx.Bridge.Initialize(grid, ctx.Lair, ctx.Treasury, owner);

            ctx.ImplingSpawner = SceneSetup.CreateComponent<ImplingSpawner>($"ImplingSpawner P{owner + 1}", parent);
            ctx.ImplingSpawner.Initialize(ctx.JobBoard, grid, ctx.Treasury, ctx.Throne, ctx.SlimeHatchery, ctx.Tavern, owner);

            ctx.GremlinSpawner = SceneSetup.CreateComponent<GremlinSpawner>($"GremlinSpawner P{owner + 1}", parent);
            ctx.GremlinSpawner.Initialize(grid, ctx.Portal, ctx.Lair, ctx.SlimeHatchery, ctx.TrainingRoom, ctx.Tavern, ctx.Treasury, owner);
            ctx.Portal.SeedPool(GremlinAgent.CreatureKind, StartingGremlinPoolCount);

            ctx.WarlockSpawner = SceneSetup.CreateComponent<WarlockSpawner>($"WarlockSpawner P{owner + 1}", parent);
            ctx.WarlockSpawner.Initialize(grid, ctx.Portal, ctx.Lair, ctx.Library, ctx.SlimeHatchery, ctx.Tavern, ctx.TrainingRoom, ctx.Treasury, owner);
            ctx.Portal.SeedPool(WarlockAgent.CreatureKind, StartingWarlockPoolCount);

            ctx.MazeRattlerSpawner = SceneSetup.CreateComponent<MazeRattlerSpawner>($"MazeRattlerSpawner P{owner + 1}", parent);
            ctx.MazeRattlerSpawner.Initialize(grid, ctx.Portal, ctx.Lair, ctx.Jail, ctx.Tavern, ctx.TrainingRoom, ctx.Treasury, owner);
            ctx.Portal.SeedPool(MazeRattlerAgent.CreatureKind, StartingMazeRattlerPoolCount);

            ctx.ElfSpawner = SceneSetup.CreateComponent<ElfSpawner>($"ElfSpawner P{owner + 1}", parent);
            ctx.ElfSpawner.Initialize(grid, ctx.Portal, ctx.Lair, ctx.Tavern, ctx.Treasury);

            ctx.ConversionClass = SceneSetup.CreateComponent<ConversionClassManager>($"ConversionClassManager P{owner + 1}", parent);
            ctx.ConversionClass.Initialize(grid, ctx.Lair, ctx.Treasury, ctx.Jail, ctx.GremlinSpawner, ctx.WarlockSpawner, ctx.MazeRattlerSpawner, ctx.ElfSpawner, owner);

            ctx.BeanCounterSpawner = SceneSetup.CreateComponent<BeanCounterSpawner>($"BeanCounterSpawner P{owner + 1}", parent);
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
    }
}

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
    /// Builds the networked client's render-only world: a replicated grid,
    /// camera, HUD, fog of war and a stand-in KeeperContext whose actions all
    /// route to the host as RPCs. The client simulates nothing itself.
    internal static class ClientWorldBuilder
    {
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
        /// The joined client's world (Milestone 1b) — deliberately thin: it
        /// RENDERS, it never simulates. No KeeperContext, no job boards, no
        /// spawners, no StanceRegistry. A grid the host's NetGame fills via
        /// a snapshot + live tile deltas (DungeonGrid.ApplyReplicatedTile),
        /// a local pan/zoom camera, the cosmetic liquid animator, a slim
        /// HUD, and a gold-free / sim-off room-manager set purely so
        /// RoomReconstruction can rebuild room decoration from the snapshot
        /// (NetGame drives that once the snapshot lands). Creature ghosts
        /// self-render (CreatureNetView). Called synchronously from NetGame's
        /// client-side OnNetworkSpawn, so the grid exists before NetGame
        /// requests the snapshot on the next line there.
        internal static void BuildClientWorld()
        {
            KeeperContext.All = null;
            StanceRegistry.Current = null;

            SceneSetup.RemoveStrayCameras();
            SceneSetup.CreateSun();

            var netGame = NetGame.Instance;
            var width = netGame != null ? Mathf.Max(1, netGame.MapWidth.Value) : WorldBuilder.GridWidth;
            var height = netGame != null ? Mathf.Max(1, netGame.MapHeight.Value) : WorldBuilder.GridHeight;

            var grid = SceneSetup.CreateComponent<DungeonGrid>("DungeonGrid");
            grid.Initialize(width, height, WorldBuilder.CellSize);

            // The client is keeper 1 (see clientCtx below) — only draw this
            // keeper's own queued Mine/Reinforce/Construct icons, not the
            // host keeper's, even though the replicated tiles carry both.
            grid.LocalViewerOwnerId = 1;

            // Placeholder 2-colour palette so owner-tinted floor / orbs /
            // rings read on the client until the real roster syncs (M2).
            // PlayerColor first (fallback for -1/out-of-range owners), THEN
            // OwnerColors -- the PlayerColor setter collapses OwnerColors to
            // a single entry as a side effect (see its own header), so
            // setting it first and overriding OwnerColors after is the only
            // order that leaves the real 2-entry array in place (same
            // ordering BuildWorld's own multi-player branch uses above).
            // Doing it the other way around silently collapsed every
            // owner's color to whichever one PlayerColor happened to be --
            // every creature ring / claimed floor / wall orb read as one
            // color regardless of actual OwnerId.
            var clientOwnerColors = new[] { LevelDesignerColors.Palette[0], LevelDesignerColors.Palette[1] };
            grid.PlayerColor = clientOwnerColors[0];
            grid.OwnerColors = clientOwnerColors;
            grid.TintFloorByOwner = true;

            var liquidAnimator = SceneSetup.CreateComponent<LiquidAnimator>("LiquidAnimator");
            liquidAnimator.Initialize();

            // Scaled to the actual (replicated) map size -- a fixed margin
            // couldn't reach the corners of a big map (level1 is 96x96).
            var panMargin = Mathf.Max(grid.Width, grid.Height) * WorldBuilder.CellSize * 0.5f + 10f;
            var mapCenter = grid.GridToWorld(new Vector2Int(width / 2, height / 2));
            var camera = SceneSetup.CreateIsoCamera(grid, panMargin, mapCenter);

            SceneSetup.CreateComponent<NetHud>("NetHud").Initialize(isHost: false);
            SceneSetup.CreateComponent<NetPauseScreen>("NetPauseScreen");
            SceneSetup.CreateComponent<NetChat>("NetChat");

            // Room decoration from the tile snapshot — same gold-free,
            // simulation-off managers the Level Designer's load path uses,
            // but owner -1 (not the default 0): this ONE shared manager set
            // decorates rooms for EVERY keeper, not just keeper 0, and
            // RestoreRoom's placement check (CanBuildRoomOn(coord, ownerId))
            // requires the tile's real OwnerId to match the manager's own
            // -- offline gameplay avoids this by building one manager set
            // PER keeper (see RestoreWorldRoomsPerOwner); the client can't,
            // since it never builds per-keeper KeeperContexts. -1 is
            // CanBuildRoomOn's documented "owner-agnostic" bypass, which is
            // exactly right here: these tiles already carry the correct
            // OwnerId from replication, so there's nothing left to enforce.
            var roomManagers = LevelDesignerBootstrap.CreateLevelDesignerRoomManagers(grid, ownerId: -1);
            netGame?.ClientBindRooms(grid, roomManagers);

            // The client runs the host's real gameplay UI now (the user's
            // own GUI design comes later). It's driven by a stand-in
            // KeeperContext for keeper 1: the gold-free room managers above
            // handle placement PREVIEWS; every mutation routes through
            // NetworkedKeeperActions -> a server RPC on NetGame instead of
            // these managers. The simulation-side fields (JobBoard,
            // spawners, Throne) stay null -- BottomMenuBar's networked mode
            // reads HUD numbers off KeeperNetState and hides the panels
            // that need them. Deliberately NOT registered in
            // KeeperContext.All (that's the host's authoritative registry).
            var clientCtx = new KeeperContext
            {
                OwnerId = 1,
                Lair = roomManagers[RoomDesignTool.Lair] as LairManager,
                Treasury = roomManagers[RoomDesignTool.Treasury] as TreasuryManager,
                SlimeHatchery = roomManagers[RoomDesignTool.SlimeHatchery] as SlimeHatcheryManager,
                Tavern = roomManagers[RoomDesignTool.Tavern] as TavernManager,
                TrainingRoom = roomManagers[RoomDesignTool.TrainingRoom] as TrainingRoomManager,
                Library = roomManagers[RoomDesignTool.Library] as LibraryManager,
                Jail = roomManagers[RoomDesignTool.Jail] as JailManager,
                ConversionClass = roomManagers[RoomDesignTool.ConversionClass] as ConversionClassManager,
                Bridge = roomManagers[RoomDesignTool.Bridge] as BridgeManager,
            };
            var clientContexts = new[] { clientCtx };
            var netActions = new NetworkedKeeperActions();

            // Fog of war for the client's keeper. The client runs no
            // simulation, so vision comes from its replicated creature
            // ghosts rather than the (host-only) agent rosters, and it never
            // clears queued-job flags itself — those are the host's state.
            // Throne coord isn't known yet (KeeperNetState replicates it
            // later); claimed-territory vision covers the Throne Room anyway.
            var fogOfWar = SceneSetup.CreateComponent<FogOfWar>("FogOfWar");
            fogOfWar.Initialize(grid, clientCtx.OwnerId, throneCoord: null, 0,
                gatherVision: (ownerId, positions) =>
                {
                    foreach (var view in CreatureNetView.All)
                    {
                        if (view != null && view.OwnerId == ownerId)
                        {
                            positions.Add(view.Position);
                        }
                    }
                },
                clearStaleQueuedJobs: false);

            // Grab isn't wired for netcode yet -- pass null (every
            // _minionGrabController call in the controller is null-safe).
            var interactionController = SceneSetup.CreateComponent<TileInteractionController>("TileInteractionController");
            interactionController.Initialize(camera, grid, clientContexts, null, 0, netActions);

            var bottomMenuBar = SceneSetup.CreateComponent<BottomMenuBar>("BottomMenuBar");
            bottomMenuBar.Initialize(grid, clientContexts, interactionController, null, 0, netActions, networked: true, fog: fogOfWar);
        }
    }
}

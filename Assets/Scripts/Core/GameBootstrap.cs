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
    /// Entry point: everything is built procedurally on Play — nothing is
    /// hand-wired in the scene. This class owns the menu flow (main menu,
    /// Start / Continue / Skirmish, host / join / lobby) and hands off to the
    /// builders, one per kind of world:
    ///   WorldBuilder           — offline play, Skirmish, Continue, MP host
    ///   ClientWorldBuilder     — the networked client's render-only world
    ///   LevelDesignerBootstrap — Level Designer worlds
    ///   GameSave               — the mid-game save slot
    ///   SceneSetup             — cameras, sun, component helpers they share
    public static class GameBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            // Player options (display mode / resolution / name / orbit keys),
            // read from PlayerPrefs and applied to the screen right away.
            GameSettings.Load();

            // The long-lived NetworkManager + Unity Gaming Services wrapper.
            // Idle unless Host/Join is pressed — offline "Start Game" never
            // touches it. Wired here so the callbacks survive a
            // Main Menu <-> game bounce.
            NetSession.Create();
            NetSession.Instance.OnHostReady = OnHostReady;
            NetSession.Instance.OnClientLobby = ShowClientLobby;
            NetSession.Instance.OnClientReady = ClientWorldBuilder.BuildClientWorld;
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
            Time.timeScale = 1f;
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
        internal static void ShowMainMenu()
        {
            // Clears out whatever camera the previous screen was using
            // (BuildWorld's iso camera on a "Main Menu" bounce-back, or this
            // same menu's own camera on a "Back" from Level Designer
            // properties) before creating a fresh one below.
            SceneSetup.RemoveStrayCameras();

            var menuCameraGO = new GameObject("Menu Camera");
            menuCameraGO.tag = "MainCamera";
            var menuCamera = menuCameraGO.AddComponent<Camera>();
            menuCamera.orthographic = true;
            menuCamera.clearFlags = CameraClearFlags.SolidColor;
            menuCamera.backgroundColor = new Color(0.05f, 0.05f, 0.07f);

            var menu = SceneSetup.CreateComponent<MainMenu>("MainMenu");
            // Continue is offered only when a mid-game save is on disk (see
            // SaveGame / SaveGameSlot); null hides the button entirely.
            menu.Initialize(StartGame,
                LevelFileIO.SaveExists(GameSave.SaveGameSlot) ? (System.Action)ContinueGame : null,
                LevelDesignerBootstrap.ShowLevelDesignerProperties, HostGame, JoinGame, StartSkirmish);
        }

        /// Default square size for a freshly generated map, scaled to how
        /// many keepers have to fit fairly around it (see MapGenerator).
        internal static int GeneratedMapSize(int playerCount)
        {
            return playerCount <= 2 ? 64 : playerCount == 3 ? 80 : 96;
        }

        /// Main Menu "Skirmish (generated)" — a fresh seed-based map (see
        /// MapGenerator) built straight into offline gameplay through the
        /// ordinary loaded-level path. Two keepers by default; keeper AI
        /// doesn't exist yet, so the rival's creatures just run autonomously
        /// off their own roster. Abandons any mid-game save, same as
        /// Start Game.
        private static void StartSkirmish()
        {
            LevelFileIO.Delete(GameSave.SaveGameSlot);

            const int playerCount = 2;
            var seed = new System.Random().Next();
            Debug.Log($"GameBootstrap: skirmish — seed {seed}, {playerCount} players.");

            WorldBuilder.BuildWorld(MapGenerator.Generate(new MapGenSettings
            {
                Seed = seed,
                PlayerCount = playerCount,
                MapWidth = GeneratedMapSize(playerCount),
                MapHeight = GeneratedMapSize(playerCount),
                Multiplayer = false
            }));
        }

        /// Main Menu "Host Game" — spins up a Relay session (join code) and,
        /// once it's live (NetSession.OnHostReady -> OnHostReady below),
        /// builds the authoritative world exactly as offline Start Game does.
        private static void HostGame()
        {
            // Re-armed each session -- NetSession.Leave() clears it so an
            // intentional "Main Menu" leave doesn't bounce back through here.
            NetSession.Instance.OnDisconnected = ReturnToMainMenu;
            NetSession.Instance.StartHost();
        }

        /// Main Menu "Join Game" — connects to the host's session by code.
        /// The render-only client world is built from NetGame's client-side
        /// OnNetworkSpawn (-> NetSession.OnClientReady -> BuildClientWorld).
        private static void JoinGame(string joinCode)
        {
            NetSession.Instance.OnDisconnected = ReturnToMainMenu;
            NetSession.Instance.JoinByCode(joinCode);
        }

        /// NetSession.OnHostReady — the Relay transport is up and we're the
        /// host. Spawn the lobby object and show the lobby screen; the world
        /// itself isn't built until every player has readied up and the host
        /// hits Start (NetLobby.OnHostBuildGame -> BuildHostGame below).
        private static void OnHostReady()
        {
            var lobbyGo = Object.Instantiate(Resources.Load<GameObject>("Net/NetLobby"));
            lobbyGo.GetComponent<NetLobby>().OnHostBuildGame = WorldBuilder.BuildHostGame;
            lobbyGo.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);

            SceneSetup.CreateComponent<LobbyScreen>("LobbyScreen").Initialize(isHost: true, ReturnToMainMenu);
        }

        /// NetSession.OnClientLobby — the lobby object has replicated in, so
        /// we're connected and waiting for the host to start. The render-only
        /// world is still built later, off NetGame's client-side spawn (see
        /// BuildClientWorld).
        private static void ShowClientLobby()
        {
            SceneSetup.CreateComponent<LobbyScreen>("LobbyScreen").Initialize(isHost: false, ReturnToMainMenu);
        }

        /// The Start Game button's actual callback — a fresh run off the
        /// "level1" template (a level saved/edited via the Level Designer,
        /// or the bundled procedural default). Deletes any mid-game save
        /// first, since starting fresh abandons it.
        private static void StartGame()
        {
            LevelFileIO.Delete(GameSave.SaveGameSlot);
            WorldBuilder.BuildWorld(LevelFileIO.Load("level1"));
        }

        /// Main Menu "Continue" — resumes the mid-game save (see SaveGame).
        /// Only wired up / shown when LevelFileIO.SaveExists(SaveGameSlot).
        private static void ContinueGame()
        {
            var data = LevelFileIO.Load(GameSave.SaveGameSlot);
            if (data == null)
            {
                StartGame();
                return;
            }

            WorldBuilder.BuildWorld(data);
        }
}
}

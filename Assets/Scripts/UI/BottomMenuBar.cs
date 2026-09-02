using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using KeepersDomain.Core;
using KeepersDomain.Creatures;
using KeepersDomain.Grid;
using KeepersDomain.Implings;
using KeepersDomain.Input;
using KeepersDomain.Monsters;
using KeepersDomain.Rooms;

namespace KeepersDomain.UI
{
    /// The player-facing control surface — a bottom bar with four expandable
    /// menus. This class only ever reads state and forwards button presses,
    /// leaving all actual game logic to the systems it queries.
    public class BottomMenuBar : MonoBehaviour
    {
        private enum MenuTab
        {
            None,
            Build,
            Impling,
            Creatures,
            Tasks,
            Settings
        }

        private const float BarHeight = 44f;
        private const float PanelWidth = 340f;
        private const float PanelHeight = 260f;
        private const float TabButtonWidth = 110f;
        private const float BannerHeight = 28f;
        // Wide/tall enough for a creature's full stat dump (see
        // TileInteractionController.Inspect/DescribeMonster) — a plain
        // tile's inspection text is much shorter and just leaves the rest
        // of the panel blank.
        private const float InspectionPanelWidth = 300f;
        private const float InspectionPanelHeight = 320f;
        private const float TopBarWidth = 280f;
        private const float TopBarHeight = 28f;

        public static bool PointerOverPanel { get; private set; }

        private DungeonGrid _grid;
        private TileInteractionController _interactionController;
        private LocalPlayerController _localPlayer;

        // Every context this session — only used to draw the debug player
        // switcher (and only when Length > 1).
        private KeeperContext[] _contexts;

        // The keeper whose economy/tasks/recruiting this HUD currently
        // shows. Cached pointers into _active, refreshed by SetActiveContext
        // when the debug switcher flips players — the draw code below keeps
        // referring to _jobBoard / _throneRoom / ... unchanged.
        private KeeperContext _active;
        private BuilderJobBoard _jobBoard;
        private TreasuryManager _treasuryManager;
        private ThroneRoom _throneRoom;
        private TavernManager _tavernManager;
        private TrainingRoomManager _trainingRoomManager;
        private LibraryManager _libraryManager;
        private JailManager _jailManager;
        private ConversionClassManager _conversionClassManager;
        private GremlinSpawner _gremlinSpawner;
        private WarlockSpawner _warlockSpawner;
        private MazeRattlerSpawner _mazeRattlerSpawner;
        private BeanCounterSpawner _beanCounterSpawner;

        private MenuTab _openTab = MenuTab.None;
        private bool _squareModeOn;
        private bool _halfWallsOn;
        private bool _digQueuePaused;
        private bool _autoReinforceOn;
        private List<JobKind> _priorityOrder;
        private Vector2 _buildScrollPos;
        private Vector2 _tasksScrollPos;
        private Vector2 _creaturesScrollPos;

        public void Initialize(DungeonGrid grid, KeeperContext[] contexts, TileInteractionController interactionController, LocalPlayerController localPlayer, int activeIndex)
        {
            _grid = grid;
            _contexts = contexts;
            _interactionController = interactionController;
            _localPlayer = localPlayer;
            SetActiveContext(contexts[activeIndex]);
        }

        /// Repoints every cached manager/spawner field at ctx and re-seeds
        /// the job-priority list from ctx's board — called on init and by
        /// LocalPlayerController when the debug player switcher flips
        /// players.
        public void SetActiveContext(KeeperContext ctx)
        {
            _active = ctx;
            _jobBoard = ctx.JobBoard;
            _treasuryManager = ctx.Treasury;
            _throneRoom = ctx.Throne;
            _tavernManager = ctx.Tavern;
            _trainingRoomManager = ctx.TrainingRoom;
            _libraryManager = ctx.Library;
            _jailManager = ctx.Jail;
            _conversionClassManager = ctx.ConversionClass;
            _gremlinSpawner = ctx.GremlinSpawner;
            _warlockSpawner = ctx.WarlockSpawner;
            _mazeRattlerSpawner = ctx.MazeRattlerSpawner;
            _beanCounterSpawner = ctx.BeanCounterSpawner;
            // Seeded from the board's actual current order, not a second
            // hardcoded default — see BuilderJobBoard.GetJobPriorityOrder.
            _priorityOrder = new List<JobKind>(_jobBoard.GetJobPriorityOrder());
            // These toggles are per-board state; resync the UI to the
            // newly-active board so a checkbox doesn't lie.
            _digQueuePaused = false;
            _autoReinforceOn = false;
        }

        private void OnGUI()
        {
            var barRect = new Rect(0f, Screen.height - BarHeight, Screen.width, BarHeight);
            var panelRect = new Rect(10f, barRect.y - PanelHeight - 6f, PanelWidth, PanelHeight);
            var inspectionRect = new Rect(Screen.width - InspectionPanelWidth - 10f, 10f, InspectionPanelWidth, InspectionPanelHeight);
            var topBarRect = new Rect(10f, 10f, TopBarWidth, TopBarHeight);

            var hasSwitcher = _contexts != null && _contexts.Length > 1 && _localPlayer != null;
            var switcherRect = new Rect(10f, topBarRect.yMax + 4f, TopBarWidth, hasSwitcher ? TopBarHeight : 0f);

            var rawMousePos = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            var mouseScreenPos = new Vector2(rawMousePos.x, Screen.height - rawMousePos.y);
            var isOverInspectionPanel = _interactionController.BuildMode == BuildMode.View && inspectionRect.Contains(mouseScreenPos);
            PointerOverPanel = barRect.Contains(mouseScreenPos) || (_openTab != MenuTab.None && panelRect.Contains(mouseScreenPos)) || isOverInspectionPanel || topBarRect.Contains(mouseScreenPos) || (hasSwitcher && switcherRect.Contains(mouseScreenPos));

            DrawTopBar(topBarRect);
            if (hasSwitcher)
            {
                DrawPlayerSwitcher(switcherRect);
            }
            DrawPendingPlacementBanner(panelRect, barRect);
            DrawBar(barRect);

            if (_openTab != MenuTab.None)
            {
                DrawPanel(panelRect);
            }

            DrawInspectionPanel(inspectionRect);
            DrawHoveredCoordLabel(mouseScreenPos);
        }

        /// Small (x, y) readout following the cursor — troubleshooting aid
        /// for confirming exactly which tile is under the pointer. Blank
        /// whenever the pointer isn't over the grid at all (over a panel,
        /// or off the map edge).
        private void DrawHoveredCoordLabel(Vector2 mouseScreenPos)
        {
            var coord = _interactionController.HoveredCoord;
            if (!coord.HasValue)
            {
                return;
            }

            var rect = new Rect(mouseScreenPos.x + 16f, mouseScreenPos.y + 16f, 90f, 22f);
            GUI.Label(rect, $"({coord.Value.x}, {coord.Value.y})", GUI.skin.box);
        }

        /// View mode's inspection readout — kept independent of which (if
        /// any) bottom-bar menu is open, since inspecting something should
        /// stay visible while the player looks around rather than being
        /// tucked away inside the Build menu's own panel.
        private void DrawInspectionPanel(Rect rect)
        {
            if (_interactionController.BuildMode != BuildMode.View)
            {
                return;
            }

            GUILayout.BeginArea(rect, "Inspect", GUI.skin.window);
            var info = _interactionController.InspectedDescription;
            GUILayout.Label(string.IsNullOrEmpty(info) ? "Click a tile or creature to inspect it." : info);
            GUILayout.EndArea();
        }

        /// Always-visible resource readout, top-left of the screen — gold
        /// in Treasury reserves, plus mana as current/reserved/max (see
        /// ThroneRoom: reserved is upkeep held by currently-alive implings).
        private void DrawTopBar(Rect rect)
        {
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Gold: {_treasuryManager.TotalGold}");
            GUILayout.Space(12f);
            GUILayout.Label($"Mana: {_throneRoom.CurrentMana}/{_throneRoom.ReservedMana}/{_throneRoom.MaxMana}");
            GUILayout.Space(12f);
            GUILayout.Label($"Bacon: {_tavernManager.TotalBacon}");
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// Debug-only player switcher — one toggle per keeper (P1..PN, AI
        /// ones tagged), only shown on a multi-player level. Clicking one
        /// repoints input / HUD / camera at that keeper via
        /// LocalPlayerController. Gameplay is still single-player; this is
        /// purely for inspecting/driving each roster during testing.
        private void DrawPlayerSwitcher(Rect rect)
        {
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("View:", GUILayout.Width(36f));
            for (int i = 0; i < _contexts.Length; i++)
            {
                var label = _contexts[i].IsAI ? $"P{i + 1} (AI)" : $"P{i + 1}";
                var isActive = i == _localPlayer.ActiveIndex;
                if (GUILayout.Toggle(isActive, label, GUI.skin.button) && !isActive)
                {
                    _localPlayer.SetActivePlayer(i);
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawBar(Rect rect)
        {
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.BeginHorizontal();
            DrawTabButton(MenuTab.Build, "Build menu");
            DrawTabButton(MenuTab.Impling, "Impling menu");
            DrawTabButton(MenuTab.Creatures, "Creatures");
            DrawTabButton(MenuTab.Tasks, "Tasks");
            DrawTabButton(MenuTab.Settings, "Settings");
            GUILayout.FlexibleSpace();
            // Tears the whole running game down and shows the main menu
            // again — see GameBootstrap.ReturnToMainMenu. No confirmation
            // prompt, matching every other button on this bar (Sell, cancel
            // job, etc. all fire immediately too).
            if (GUILayout.Button("Main Menu", GUILayout.Width(TabButtonWidth), GUILayout.Height(BarHeight - 8f)))
            {
                GameBootstrap.ReturnToMainMenu();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawTabButton(MenuTab tab, string label)
        {
            var isActive = _openTab == tab;
            if (GUILayout.Button(isActive ? $"[{label}]" : label, GUILayout.Width(TabButtonWidth), GUILayout.Height(BarHeight - 8f)))
            {
                _openTab = isActive ? MenuTab.None : tab;
            }
        }

        private void DrawPanel(Rect rect)
        {
            GUILayout.BeginArea(rect, $"{_openTab} menu", GUI.skin.window);
            switch (_openTab)
            {
                case MenuTab.Build:
                    DrawBuildMenu();
                    break;
                case MenuTab.Impling:
                    DrawImplingMenu();
                    break;
                case MenuTab.Creatures:
                    DrawCreaturesMenu();
                    break;
                case MenuTab.Tasks:
                    DrawTasksMenu();
                    break;
                case MenuTab.Settings:
                    DrawSettingsMenu();
                    break;
            }
            GUILayout.EndArea();
        }

        /// Feedback for a one-shot placement tool (Lair, Treasury, Sell,
        /// Spawn Impling, Toggle Claim) currently armed via
        /// TileInteractionController — the control itself has no visible
        /// "on" state once its menu closes, so without this the player
        /// would have no way to tell a tap is about to do something
        /// special. While a Lair or Treasury placement is actively being
        /// dragged, shows the footprint size instead so the player can see
        /// what they're about to place before releasing.
        private void DrawPendingPlacementBanner(Rect panelRect, Rect barRect)
        {
            var pending = _interactionController.PendingPlacementAction;
            var isDraggingRoom = _interactionController.IsPlacingLair || _interactionController.IsPlacingTreasury
                || _interactionController.IsPlacingHatchery || _interactionController.IsPlacingTavern || _interactionController.IsPlacingTrainingRoom
                || _interactionController.IsPlacingLibrary || _interactionController.IsPlacingJail || _interactionController.IsPlacingConversionClass;
            if (pending == PlacementAction.None && !isDraggingRoom)
            {
                return;
            }

            var y = (_openTab != MenuTab.None ? panelRect.y : barRect.y) - BannerHeight - 4f;
            var rect = new Rect(10f, y, PanelWidth, BannerHeight);

            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.BeginHorizontal();

            if (_interactionController.IsPlacingLair)
            {
                DrawRoomDragSize("Lair", _interactionController.LairDragStartCoord, _interactionController.LairDragCurrentCoord, tileCount => tileCount * LairManager.CostPerTile);
            }
            else if (_interactionController.IsPlacingTreasury)
            {
                DrawRoomDragSize("Treasury", _interactionController.TreasuryDragStartCoord, _interactionController.TreasuryDragCurrentCoord, _treasuryManager.PreviewCost);
            }
            else if (_interactionController.IsPlacingHatchery)
            {
                DrawRoomDragSize("Slime Hatchery", _interactionController.HatcheryDragStartCoord, _interactionController.HatcheryDragCurrentCoord, tileCount => tileCount * SlimeHatcheryManager.CostPerTile);
            }
            else if (_interactionController.IsPlacingTavern)
            {
                DrawRoomDragSize("Tavern", _interactionController.TavernDragStartCoord, _interactionController.TavernDragCurrentCoord, tileCount => tileCount * TavernManager.CostPerTile);
            }
            else if (_interactionController.IsPlacingTrainingRoom)
            {
                DrawRoomDragSize("Training Room", _interactionController.TrainingRoomDragStartCoord, _interactionController.TrainingRoomDragCurrentCoord, tileCount => tileCount * TrainingRoomManager.CostPerTile);
            }
            else if (_interactionController.IsPlacingLibrary)
            {
                DrawRoomDragSize("Library", _interactionController.LibraryDragStartCoord, _interactionController.LibraryDragCurrentCoord, tileCount => tileCount * LibraryManager.CostPerTile);
            }
            else if (_interactionController.IsPlacingJail)
            {
                DrawRoomDragSize("Jail", _interactionController.JailDragStartCoord, _interactionController.JailDragCurrentCoord, tileCount => tileCount * JailManager.CostPerTile);
            }
            else if (_interactionController.IsPlacingConversionClass)
            {
                DrawRoomDragSize("Conversion Class", _interactionController.ConversionClassDragStartCoord, _interactionController.ConversionClassDragCurrentCoord, tileCount => tileCount * ConversionClassManager.CostPerTile);
            }
            else
            {
                var instructionVerb = pending is PlacementAction.PlaceLair or PlacementAction.PlaceTreasury or PlacementAction.PlaceSlimeHatchery or PlacementAction.PlaceTavern or PlacementAction.PlaceTrainingRoom or PlacementAction.PlaceLibrary or PlacementAction.PlaceJail or PlacementAction.PlaceConversionClass ? "Drag to size, release to place" : "Tap a tile to place";
                GUILayout.Label($"{instructionVerb}: {PlacementActionLabel(pending)}");
                if (GUILayout.Button("Cancel", GUILayout.Width(60f)))
                {
                    _interactionController.RequestPlacement(PlacementAction.None);
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// costForTileCount computes the gold cost for a given tile count —
        /// a delegate rather than a flat per-tile constant so Treasury's
        /// one-free-tile-when-empty discount (see TreasuryManager.
        /// PreviewCost) shows up here exactly as TryPlaceTreasury will
        /// actually charge it.
        private void DrawRoomDragSize(string roomLabel, Vector2Int start, Vector2Int current, Func<int, int> costForTileCount)
        {
            var width = Mathf.Abs(current.x - start.x) + 1;
            var height = Mathf.Abs(current.y - start.y) + 1;
            var cost = costForTileCount(width * height);
            var affordable = _treasuryManager.TotalGold >= cost;
            var costText = affordable ? $"{cost}g" : $"{cost}g — can't afford";
            GUILayout.Label($"Placing {roomLabel} — {width}x{height} ({costText}, release to place)");
        }

        private void DrawBuildMenu()
        {
            _buildScrollPos = GUILayout.BeginScrollView(_buildScrollPos, GUILayout.Height(210f));

            var squareOn = GUILayout.Toggle(_squareModeOn, "Square mode (paint rectangles)");
            if (squareOn != _squareModeOn)
            {
                _squareModeOn = squareOn;
                _interactionController.SetSquareModeToggle(_squareModeOn);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Build mode");
            DrawBuildModeOption(BuildMode.View, "View mode");
            DrawBuildModeOption(BuildMode.Mine, "Mine mode");
            DrawBuildModeOption(BuildMode.Reinforce, "Reinforce mode");
            DrawBuildModeOption(BuildMode.Construct, "Construct wall");
            DrawBuildModeOption(BuildMode.Grab, "Grab mode");
            if (_interactionController.BuildMode == BuildMode.Grab)
            {
                GUILayout.Label(_interactionController.IsCarryingMinion
                    ? "Carrying — tap a walkable tile to drop"
                    : "Tap a minion to grab it");
            }

            GUILayout.Space(8f);
            // Placeholder terrain painters standing in for the map
            // generator that doesn't exist yet (see DungeonGrid.
            // SetTerrainFeature) — not a real player-facing tool.
            GUILayout.Label("[Dev] Terrain");
            DrawBuildModeOption(BuildMode.PlaceWater, "[Dev] Place Water");
            DrawBuildModeOption(BuildMode.PlaceLava, "[Dev] Place Lava");
            DrawBuildModeOption(BuildMode.PlaceChasm, "[Dev] Place Chasm");
            DrawBuildModeOption(BuildMode.PlaceHolyGround, "[Dev] Place Holy Ground");
            DrawBuildModeOption(BuildMode.PlaceBedrock, "[Dev] Place Bedrock");

            GUILayout.Space(8f);
            var pauseOn = GUILayout.Toggle(_digQueuePaused, "Pause dig queue");
            if (pauseOn != _digQueuePaused)
            {
                _digQueuePaused = pauseOn;
                _jobBoard.SetDigJobsPaused(_digQueuePaused);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Buildings");

            // 3 per row, wrapping downward, rather than one long horizontal
            // row — that used to overflow the panel's fixed width and force
            // a horizontal scrollbar. Sell stays armed across taps (see
            // TileInteractionController.RequestPlacement) rather than being
            // consumed after one use, so its own button toggles it off on a
            // second press instead of just re-arming an already-armed tool.
            var sellActive = _interactionController.PendingPlacementAction == PlacementAction.SellLair;

            BeginButtonRow();
            DrawPlacementButton(PlacementAction.PlaceLair, $"Lair ({LairManager.CostPerTile}g/tile)");
            DrawPlacementButton(PlacementAction.PlaceTreasury, $"Treasury ({TreasuryManager.CostPerTile}g/tile)");
            DrawPlacementButton(PlacementAction.PlaceSlimeHatchery, $"Slime Hatchery ({SlimeHatcheryManager.CostPerTile}g/tile)");
            EndButtonRow();

            BeginButtonRow();
            DrawPlacementButton(PlacementAction.PlaceTavern, $"Tavern ({TavernManager.CostPerTile}g/tile)");
            DrawPlacementButton(PlacementAction.PlaceTrainingRoom, $"Training Room ({TrainingRoomManager.CostPerTile}g/tile)");
            DrawPlacementButton(PlacementAction.PlaceLibrary, $"Library ({LibraryManager.CostPerTile}g/tile)");
            EndButtonRow();

            BeginButtonRow();
            DrawPlacementButton(PlacementAction.PlaceJail, $"Jail ({JailManager.CostPerTile}g/tile)");
            DrawPlacementButton(PlacementAction.PlaceConversionClass, $"Conversion Class ({ConversionClassManager.CostPerTile}g/tile)");
            if (GUILayout.Button(sellActive ? "Sell (active)" : "Sell", GUILayout.Width(ButtonGridColumnWidth)))
            {
                _interactionController.RequestPlacement(sellActive ? PlacementAction.None : PlacementAction.SellLair);
            }
            EndButtonRow();

            BeginButtonRow();
            // Manual stand-in for a monster claiming/vacating its Lair — no
            // monster system exists yet to do this for real (see
            // LairManager.ToggleLairClaim), so this is how the claimed
            // "nest" visual gets tested in the meantime.
            DrawPlacementButton(PlacementAction.ToggleLairClaim, "Toggle Claim");
            // Bridge is a persistent paint tool (BuildMode.Bridge), not a
            // one-shot PlacementAction, but sits in the Buildings grid
            // alongside every other room since it's placed the same way a
            // player thinks about "building" it.
            DrawBuildModeGridButton(BuildMode.Bridge, $"Bridge ({BridgeManager.CostPerTile}g/tile)");
            EndButtonRow();

            GUILayout.EndScrollView();
        }

        // 3 of these per row fit comfortably inside PanelWidth (340)
        // alongside the scroll view's own vertical scrollbar — see
        // DrawBuildMenu's Buildings grid.
        private const float ButtonGridColumnWidth = 100f;

        private static void BeginButtonRow()
        {
            GUILayout.BeginHorizontal();
        }

        private static void EndButtonRow()
        {
            GUILayout.EndHorizontal();
        }

        private void DrawPlacementButton(PlacementAction action, string label)
        {
            if (GUILayout.Button(PlacementButtonLabel(action, label), GUILayout.Width(ButtonGridColumnWidth)))
            {
                _interactionController.RequestPlacement(action);
            }
        }

        /// Same persistent-toggle idea as DrawBuildModeOption, but rendered
        /// with the button style/fixed width so it sits flush in the
        /// Buildings grid alongside the one-shot PlacementAction buttons
        /// (see DrawPlacementButton) instead of the checkbox+label look
        /// DrawBuildModeOption's own toggles use.
        private void DrawBuildModeGridButton(BuildMode mode, string label)
        {
            var isSelected = _interactionController.BuildMode == mode;
            var pressed = GUILayout.Toggle(isSelected, label, GUI.skin.button, GUILayout.Width(ButtonGridColumnWidth));
            if (pressed && !isSelected)
            {
                _interactionController.SetBuildMode(mode);
            }
        }

        private void DrawBuildModeOption(BuildMode mode, string label)
        {
            var isSelected = _interactionController.BuildMode == mode;
            var pressed = GUILayout.Toggle(isSelected, label);
            if (pressed && !isSelected)
            {
                _interactionController.SetBuildMode(mode);
            }
        }

        private void DrawImplingMenu()
        {
            var spawnLabel = PlacementButtonLabel(PlacementAction.SpawnImpling, $"Spawn impling ({ImplingSpawner.ImplingManaUpkeep} mana)");
            GUI.enabled = _throneRoom.CurrentMana >= ImplingSpawner.ImplingManaUpkeep;
            if (GUILayout.Button(spawnLabel))
            {
                _interactionController.RequestPlacement(PlacementAction.SpawnImpling);
            }
            GUI.enabled = true;

            GUILayout.Space(8f);
            var autoReinforceOn = GUILayout.Toggle(_autoReinforceOn, "Auto-reinforce dungeon walls");
            if (autoReinforceOn != _autoReinforceOn)
            {
                _autoReinforceOn = autoReinforceOn;
                _jobBoard.SetAutoReinforceEnabled(_autoReinforceOn);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Job priority (top = done first)");
            DrawPriorityList();
        }

        private void DrawPriorityList()
        {
            for (int i = 0; i < _priorityOrder.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{i + 1}. {_priorityOrder[i]}", GUILayout.Width(150f));

                GUI.enabled = i > 0;
                if (GUILayout.Button("Up", GUILayout.Width(40f)))
                {
                    (_priorityOrder[i - 1], _priorityOrder[i]) = (_priorityOrder[i], _priorityOrder[i - 1]);
                    _jobBoard.SetJobPriorityOrder(_priorityOrder.ToArray());
                }

                GUI.enabled = i < _priorityOrder.Count - 1;
                if (GUILayout.Button("Down", GUILayout.Width(50f)))
                {
                    (_priorityOrder[i + 1], _priorityOrder[i]) = (_priorityOrder[i], _priorityOrder[i + 1]);
                    _jobBoard.SetJobPriorityOrder(_priorityOrder.ToArray());
                }

                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        private void DrawCreaturesMenu()
        {
            // Whole-panel scroll, same shape DrawBuildMenu/DrawTasksMenu use
            // — recruit buttons/labels now run to 4 creatures deep and no
            // longer fit the fixed panel height on their own, so the scroll
            // has to wrap everything (buttons included), not just the
            // roster list below them.
            _creaturesScrollPos = GUILayout.BeginScrollView(_creaturesScrollPos, GUILayout.Height(210f));

            // Recruiting takes one Gremlin straight out of the Portal's
            // pool and spawns it there — no tile picking, since every
            // non-Imp creature "joins" by coming down the portal stairway
            // rather than being placed anywhere (see Portal.TryTakeFromPool).
            // Also gated on Gremlin's own join requirements (see
            // GremlinSpawner.MeetsJoinRequirements) — always shown below the
            // button so it's clear why it's greyed out, not just that it is.
            var available = _gremlinSpawner.AvailableToRecruit;
            GUI.enabled = _gremlinSpawner.CanRecruit;
            if (GUILayout.Button($"Recruit Gremlin ({available} available)"))
            {
                _gremlinSpawner.TryRecruitGremlin();
            }
            GUI.enabled = true;
            GUILayout.Label("Requires: a free Lair, fewer non-Imp creatures than Hatchery tiles, 9+ Training Room tiles");

            GUILayout.Space(4f);

            var warlocksAvailable = _warlockSpawner.AvailableToRecruit;
            GUI.enabled = _warlockSpawner.CanRecruit;
            if (GUILayout.Button($"Recruit Warlock ({warlocksAvailable} available)"))
            {
                _warlockSpawner.TryRecruitWarlock();
            }
            GUI.enabled = true;
            GUILayout.Label("Requires: a Lair tile, a 3x3+ Library, fewer non-Imp creatures than Hatchery tiles, fewer intelligent creatures than Tavern tiles");

            GUILayout.Space(4f);

            var mazeRattlersAvailable = _mazeRattlerSpawner.AvailableToRecruit;
            GUI.enabled = _mazeRattlerSpawner.CanRecruit;
            if (GUILayout.Button($"Recruit Maze Rattler ({mazeRattlersAvailable} available)"))
            {
                _mazeRattlerSpawner.TryRecruitMazeRattler();
            }
            GUI.enabled = true;
            GUILayout.Label("Requires: a free Lair, fewer Maze Rattlers than 5x placed Jail rooms");

            GUILayout.Space(4f);

            var beanCountersAvailable = _beanCounterSpawner.AvailableToRecruit;
            GUI.enabled = _beanCounterSpawner.CanRecruit;
            if (GUILayout.Button($"Recruit Bean Counter ({beanCountersAvailable} available)"))
            {
                _beanCounterSpawner.TryRecruitBeanCounter();
            }
            GUI.enabled = true;
            GUILayout.Label("Requires: a free Lair, fewer Bean Counters than 3x placed Conversion Class rooms");

            GUILayout.Space(8f);

            var implings = ImplingAgent.All;
            var gremlins = GremlinAgent.All;
            var warlocks = WarlockAgent.All;
            var mazeRattlers = MazeRattlerAgent.All;
            var beanCounters = BeanCounterAgent.All;
            var elves = ElfAgent.All;
            GUILayout.Label($"{implings.Count} impling(s), {gremlins.Count} gremlin(s), {warlocks.Count} warlock(s), {mazeRattlers.Count} maze rattler(s), {beanCounters.Count} bean counter(s), {elves.Count} elf(ves)");

            foreach (var impling in implings)
            {
                var coord = _grid.WorldToGrid(impling.Position);
                GUILayout.Label($"#{impling.Id}  Lv{impling.Creature.Level}  P{impling.Creature.OwnerId + 1}  {impling.State}  ({coord.x},{coord.y})  G:{impling.Inventory.Gold} M:{impling.Inventory.ManaCrystals} S:{impling.Inventory.Slimes}");
            }

            foreach (var gremlin in gremlins)
            {
                var coord = _grid.WorldToGrid(gremlin.Position);
                var hungryTag = gremlin.Hunger.IsHungry ? " (hungry)" : "";
                var unhappyTag = gremlin.Pay.IsUnhappy ? " (unpaid!)" : "";
                GUILayout.Label($"{gremlin.Name}  Lv{gremlin.Creature.Level}  P{gremlin.Creature.OwnerId + 1}  {gremlin.Task}  ({coord.x},{coord.y})  Hunger:{gremlin.Hunger.Value:0}{hungryTag}  Wage:{Pay.WageFor(gremlin.Creature.Level)}g{unhappyTag}  Happy:{gremlin.Happiness.Value:0} ({gremlin.Happiness.Tier})");
            }

            foreach (var warlock in warlocks)
            {
                var coord = _grid.WorldToGrid(warlock.Position);
                var hungryTag = warlock.Hunger.IsHungry ? " (hungry)" : "";
                var unhappyTag = warlock.Pay.IsUnhappy ? " (unpaid!)" : "";
                GUILayout.Label($"{warlock.Name}  Lv{warlock.Creature.Level}  P{warlock.Creature.OwnerId + 1}  {warlock.Task}  ({coord.x},{coord.y})  Hunger:{warlock.Hunger.Value:0}{hungryTag}  Wage:{Pay.WageFor(warlock.Creature.Level)}g{unhappyTag}  Happy:{warlock.Happiness.Value:0} ({warlock.Happiness.Tier})");
            }

            foreach (var mazeRattler in mazeRattlers)
            {
                var coord = _grid.WorldToGrid(mazeRattler.Position);
                var hungryTag = mazeRattler.Hunger.IsHungry ? " (hungry)" : "";
                var unhappyTag = mazeRattler.Pay.IsUnhappy ? " (unpaid!)" : "";
                GUILayout.Label($"{mazeRattler.Name}  Lv{mazeRattler.Creature.Level}  P{mazeRattler.Creature.OwnerId + 1}  {mazeRattler.Task}  ({coord.x},{coord.y})  Hunger:{mazeRattler.Hunger.Value:0}{hungryTag}  Wage:{Pay.WageFor(mazeRattler.Creature.Level)}g{unhappyTag}  Happy:{mazeRattler.Happiness.Value:0} ({mazeRattler.Happiness.Tier})");
            }

            foreach (var beanCounter in beanCounters)
            {
                var coord = _grid.WorldToGrid(beanCounter.Position);
                var hungryTag = beanCounter.Hunger.IsHungry ? " (hungry)" : "";
                var unhappyTag = beanCounter.Pay.IsUnhappy ? " (unpaid!)" : "";
                GUILayout.Label($"{beanCounter.Name}  Lv{beanCounter.Creature.Level}  P{beanCounter.Creature.OwnerId + 1}  {beanCounter.Task}  ({coord.x},{coord.y})  Hunger:{beanCounter.Hunger.Value:0}{hungryTag}  Wage:{Pay.WageFor(beanCounter.Creature.Level)}g{unhappyTag}  Happy:{beanCounter.Happiness.Value:0} ({beanCounter.Happiness.Tier})");
            }

            foreach (var elf in elves)
            {
                var coord = _grid.WorldToGrid(elf.Position);
                var hungryTag = elf.Hunger.IsHungry ? " (hungry)" : "";
                var unhappyTag = elf.Pay.IsUnhappy ? " (unpaid!)" : "";
                GUILayout.Label($"{elf.Name}  Lv{elf.Creature.Level}  P{elf.Creature.OwnerId + 1}  {elf.Task}  ({coord.x},{coord.y})  Hunger:{elf.Hunger.Value:0}{hungryTag}  Wage:{Pay.WageFor(elf.Creature.Level)}g{unhappyTag}  Happy:{elf.Happiness.Value:0} ({elf.Happiness.Tier})");
            }

            GUILayout.EndScrollView();
        }

        /// Display-only options that don't touch game state — currently just
        /// the half-wall view toggle (see DungeonGrid.SetHalfWalls).
        private void DrawSettingsMenu()
        {
            var halfWallsOn = GUILayout.Toggle(_halfWallsOn, "Half wall");
            if (halfWallsOn != _halfWallsOn)
            {
                _halfWallsOn = halfWallsOn;
                _grid.SetHalfWalls(_halfWallsOn);
                _jailManager.SetHalfWalls(_halfWallsOn);
            }
            GUILayout.Label("Squashes every wall to half height — bottom half kept, top pressed down. Also lowers Jail pit rims.");
        }

        private void DrawTasksMenu()
        {
            _tasksScrollPos = GUILayout.BeginScrollView(_tasksScrollPos, GUILayout.Height(210f));

            DrawCancelableJobList("Dig", _jobBoard.GetJobs(), _jobBoard.CanCancel, coord =>
            {
                if (_jobBoard.CancelJob(coord))
                {
                    _grid.CancelDig(coord);
                }
            }, showHp: true);

            DrawCancelableJobList("Reinforce", _jobBoard.GetReinforceJobs(), _jobBoard.CanCancelReinforce, coord =>
            {
                if (_jobBoard.CancelReinforceJob(coord))
                {
                    _grid.CancelReinforce(coord);
                }
            }, showHp: true);

            DrawCancelableJobList("Build", _jobBoard.GetBuildJobs(), _jobBoard.CanCancelBuild, coord =>
            {
                if (_jobBoard.CancelBuildJob(coord))
                {
                    _grid.CancelBuild(coord);
                }
            }, showHp: false);

            DrawClaimJobList();
            DrawRepairJobList();

            GUILayout.EndScrollView();
        }

        private void DrawCancelableJobList(string label, List<JobInfo> jobs, Func<Vector2Int, bool> canCancel, Action<Vector2Int> cancel, bool showHp)
        {
            GUILayout.Label($"{label} jobs — {jobs.Count}");

            foreach (var job in jobs)
            {
                GUILayout.BeginHorizontal();

                var tile = _grid.GetTile(job.Coord);
                string status;
                if (job.IsPending)
                {
                    status = $"pending {job.PendingSecondsRemaining:0.0}s";
                }
                else if (tile.IsUnreachable)
                {
                    status = "unreachable";
                }
                else if (job.ClaimCount > 0)
                {
                    status = job.MaxWorkers > 1 ? $"active {job.ClaimCount}/{job.MaxWorkers}" : "active";
                }
                else
                {
                    status = "open";
                }

                var hpPart = showHp ? $"hp {tile.Hp} " : string.Empty;
                GUILayout.Label($"({job.Coord.x},{job.Coord.y}) {hpPart}— {status}", GUILayout.Width(210f));

                GUI.enabled = canCancel(job.Coord);
                if (GUILayout.Button("X", GUILayout.Width(24f)))
                {
                    cancel(job.Coord);
                }
                GUI.enabled = true;

                GUILayout.EndHorizontal();
            }
        }

        private void DrawClaimJobList()
        {
            var claimJobs = _jobBoard.GetClaimJobs();
            GUILayout.Label($"Claim jobs — {claimJobs.Count}");

            foreach (var coord in claimJobs)
            {
                string status;
                if (_jobBoard.IsClaimJobAssigned(coord))
                {
                    status = "claiming";
                }
                else if (_grid.BordersClaimedTile(coord))
                {
                    status = "open";
                }
                else
                {
                    status = "waiting";
                }

                GUILayout.Label($"({coord.x},{coord.y}) — {status}");
            }
        }

        /// Same shape as DrawClaimJobList — repair jobs are automatic
        /// (queued whenever a room tile takes damage, see
        /// DungeonGrid.RoomDamaged/BuilderJobBoard.OnRoomDamaged), so
        /// there's nothing here for the player to cancel, just a status
        /// readout.
        private void DrawRepairJobList()
        {
            var repairJobs = _jobBoard.GetRepairJobs();
            GUILayout.Label($"Repair jobs — {repairJobs.Count}");

            foreach (var coord in repairJobs)
            {
                var status = _jobBoard.IsRepairJobAssigned(coord) ? "repairing" : "open";
                var tile = _grid.GetTile(coord);
                GUILayout.Label($"({coord.x},{coord.y}) hp {tile.Hp} — {status}");
            }
        }

        private string PlacementButtonLabel(PlacementAction action, string label)
        {
            return _interactionController.PendingPlacementAction == action ? $"{label} (active)" : label;
        }

        /// Friendly display name for the pending-placement banner (see
        /// DrawPendingPlacementBanner) — SellLair's own enum name reads as
        /// raw code ("Tap a tile to place: SellLair"); every other action's
        /// name already doubles as a readable label.
        private static string PlacementActionLabel(PlacementAction action)
        {
            return action == PlacementAction.SellLair ? "Sell room tile" : action.ToString();
        }
    }
}

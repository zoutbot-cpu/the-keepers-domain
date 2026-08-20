using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
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
            Tasks
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
        private BuilderJobBoard _jobBoard;
        private TileInteractionController _interactionController;
        private TreasuryManager _treasuryManager;
        private ChaosCore _chaosCore;
        private BaconBeaconManager _baconBeaconManager;
        private TrainingRoomManager _trainingRoomManager;
        private LibraryManager _libraryManager;
        private JailManager _jailManager;
        private GremlinSpawner _gremlinSpawner;
        private WarlockSpawner _warlockSpawner;

        private MenuTab _openTab = MenuTab.None;
        private bool _squareModeOn;
        private bool _digQueuePaused;
        private bool _autoReinforceOn;
        private List<JobKind> _priorityOrder;
        private Vector2 _buildScrollPos;
        private Vector2 _tasksScrollPos;
        private Vector2 _creaturesScrollPos;

        public void Initialize(DungeonGrid grid, BuilderJobBoard jobBoard, TileInteractionController interactionController, TreasuryManager treasuryManager, ChaosCore chaosCore, BaconBeaconManager baconBeaconManager, TrainingRoomManager trainingRoomManager, LibraryManager libraryManager, JailManager jailManager, GremlinSpawner gremlinSpawner, WarlockSpawner warlockSpawner)
        {
            _grid = grid;
            _jobBoard = jobBoard;
            _interactionController = interactionController;
            _treasuryManager = treasuryManager;
            _chaosCore = chaosCore;
            _baconBeaconManager = baconBeaconManager;
            _trainingRoomManager = trainingRoomManager;
            _libraryManager = libraryManager;
            _jailManager = jailManager;
            _gremlinSpawner = gremlinSpawner;
            _warlockSpawner = warlockSpawner;
            // Seeded from the board's actual current order, not a second
            // hardcoded default — see BuilderJobBoard.GetJobPriorityOrder.
            _priorityOrder = new List<JobKind>(_jobBoard.GetJobPriorityOrder());
        }

        private void OnGUI()
        {
            var barRect = new Rect(0f, Screen.height - BarHeight, Screen.width, BarHeight);
            var panelRect = new Rect(10f, barRect.y - PanelHeight - 6f, PanelWidth, PanelHeight);
            var inspectionRect = new Rect(Screen.width - InspectionPanelWidth - 10f, 10f, InspectionPanelWidth, InspectionPanelHeight);
            var topBarRect = new Rect(10f, 10f, TopBarWidth, TopBarHeight);

            var rawMousePos = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            var mouseScreenPos = new Vector2(rawMousePos.x, Screen.height - rawMousePos.y);
            var isOverInspectionPanel = _interactionController.BuildMode == BuildMode.View && inspectionRect.Contains(mouseScreenPos);
            PointerOverPanel = barRect.Contains(mouseScreenPos) || (_openTab != MenuTab.None && panelRect.Contains(mouseScreenPos)) || isOverInspectionPanel || topBarRect.Contains(mouseScreenPos);

            DrawTopBar(topBarRect);
            DrawPendingPlacementBanner(panelRect, barRect);
            DrawBar(barRect);

            if (_openTab != MenuTab.None)
            {
                DrawPanel(panelRect);
            }

            DrawInspectionPanel(inspectionRect);
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
        /// ChaosCore: reserved is upkeep held by currently-alive implings).
        private void DrawTopBar(Rect rect)
        {
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Gold: {_treasuryManager.TotalGold}");
            GUILayout.Space(12f);
            GUILayout.Label($"Mana: {_chaosCore.CurrentMana}/{_chaosCore.ReservedMana}/{_chaosCore.MaxMana}");
            GUILayout.Space(12f);
            GUILayout.Label($"Bacon: {_baconBeaconManager.TotalBacon}");
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
                || _interactionController.IsPlacingHatchery || _interactionController.IsPlacingBeacon || _interactionController.IsPlacingTrainingRoom
                || _interactionController.IsPlacingLibrary || _interactionController.IsPlacingJail;
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
            else if (_interactionController.IsPlacingBeacon)
            {
                DrawRoomDragSize("Bacon Beacon", _interactionController.BeaconDragStartCoord, _interactionController.BeaconDragCurrentCoord, tileCount => tileCount * BaconBeaconManager.CostPerTile);
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
            else
            {
                var instructionVerb = pending is PlacementAction.PlaceLair or PlacementAction.PlaceTreasury or PlacementAction.PlaceSlimeHatchery or PlacementAction.PlaceBaconBeacon or PlacementAction.PlaceTrainingRoom or PlacementAction.PlaceLibrary or PlacementAction.PlaceJail ? "Drag to size, release to place" : "Tap a tile to place";
                GUILayout.Label($"{instructionVerb}: {pending}");
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

            GUILayout.Space(8f);
            var pauseOn = GUILayout.Toggle(_digQueuePaused, "Pause dig queue");
            if (pauseOn != _digQueuePaused)
            {
                _digQueuePaused = pauseOn;
                _jobBoard.SetDigJobsPaused(_digQueuePaused);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Buildings");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(PlacementButtonLabel(PlacementAction.PlaceLair, $"Lair ({LairManager.CostPerTile}g/tile)")))
            {
                _interactionController.RequestPlacement(PlacementAction.PlaceLair);
            }

            if (GUILayout.Button(PlacementButtonLabel(PlacementAction.PlaceTreasury, $"Treasury ({TreasuryManager.CostPerTile}g/tile)")))
            {
                _interactionController.RequestPlacement(PlacementAction.PlaceTreasury);
            }

            if (GUILayout.Button(PlacementButtonLabel(PlacementAction.PlaceSlimeHatchery, $"Slime Hatchery ({SlimeHatcheryManager.CostPerTile}g/tile)")))
            {
                _interactionController.RequestPlacement(PlacementAction.PlaceSlimeHatchery);
            }

            if (GUILayout.Button(PlacementButtonLabel(PlacementAction.PlaceBaconBeacon, $"Bacon Beacon ({BaconBeaconManager.CostPerTile}g/tile)")))
            {
                _interactionController.RequestPlacement(PlacementAction.PlaceBaconBeacon);
            }

            if (GUILayout.Button(PlacementButtonLabel(PlacementAction.PlaceTrainingRoom, $"Training Room ({TrainingRoomManager.CostPerTile}g/tile)")))
            {
                _interactionController.RequestPlacement(PlacementAction.PlaceTrainingRoom);
            }

            if (GUILayout.Button(PlacementButtonLabel(PlacementAction.PlaceLibrary, $"Library ({LibraryManager.CostPerTile}g/tile)")))
            {
                _interactionController.RequestPlacement(PlacementAction.PlaceLibrary);
            }

            if (GUILayout.Button(PlacementButtonLabel(PlacementAction.PlaceJail, $"Jail ({JailManager.CostPerTile}g/tile)")))
            {
                _interactionController.RequestPlacement(PlacementAction.PlaceJail);
            }

            // Sell stays armed across taps (see TileInteractionController.
            // RequestPlacement) rather than being consumed after one use, so
            // this button toggles it off on a second press instead of just
            // re-arming an already-armed tool.
            var sellActive = _interactionController.PendingPlacementAction == PlacementAction.SellLair;
            if (GUILayout.Button(sellActive ? "Sell (active)" : "Sell"))
            {
                _interactionController.RequestPlacement(sellActive ? PlacementAction.None : PlacementAction.SellLair);
            }

            // Manual stand-in for a monster claiming/vacating its Lair — no
            // monster system exists yet to do this for real (see
            // LairManager.ToggleLairClaim), so this is how the claimed
            // "nest" visual gets tested in the meantime.
            if (GUILayout.Button(PlacementButtonLabel(PlacementAction.ToggleLairClaim, "Toggle Claim")))
            {
                _interactionController.RequestPlacement(PlacementAction.ToggleLairClaim);
            }
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();
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
            GUI.enabled = _chaosCore.CurrentMana >= ImplingSpawner.ImplingManaUpkeep;
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
            GUILayout.Label("Requires: a Lair tile, a 3x3+ Library, fewer non-Imp creatures than Hatchery tiles, fewer intelligent creatures than Bacon Beacon tiles");

            GUILayout.Space(8f);

            var implings = ImplingAgent.All;
            var gremlins = GremlinAgent.All;
            var warlocks = WarlockAgent.All;
            GUILayout.Label($"{implings.Count} impling(s), {gremlins.Count} gremlin(s), {warlocks.Count} warlock(s)");
            _creaturesScrollPos = GUILayout.BeginScrollView(_creaturesScrollPos, GUILayout.Height(210f));

            foreach (var impling in implings)
            {
                var coord = _grid.WorldToGrid(impling.Position);
                GUILayout.Label($"#{impling.Id}  Lv{impling.Creature.Level}  {impling.State}  ({coord.x},{coord.y})  G:{impling.Inventory.Gold} M:{impling.Inventory.ManaCrystals} S:{impling.Inventory.Slimes}");
            }

            foreach (var gremlin in gremlins)
            {
                var coord = _grid.WorldToGrid(gremlin.Position);
                var hungryTag = gremlin.Hunger.IsHungry ? " (hungry)" : "";
                var unhappyTag = gremlin.Pay.IsUnhappy ? " (unpaid!)" : "";
                GUILayout.Label($"{gremlin.Name}  Lv{gremlin.Creature.Level}  {gremlin.Task}  ({coord.x},{coord.y})  Hunger:{gremlin.Hunger.Value:0}{hungryTag}  Wage:{Pay.WageFor(gremlin.Creature.Level)}g{unhappyTag}  Happy:{gremlin.Happiness.Value:0} ({gremlin.Happiness.Tier})");
            }

            foreach (var warlock in warlocks)
            {
                var coord = _grid.WorldToGrid(warlock.Position);
                var hungryTag = warlock.Hunger.IsHungry ? " (hungry)" : "";
                var unhappyTag = warlock.Pay.IsUnhappy ? " (unpaid!)" : "";
                GUILayout.Label($"{warlock.Name}  Lv{warlock.Creature.Level}  {warlock.Task}  ({coord.x},{coord.y})  Hunger:{warlock.Hunger.Value:0}{hungryTag}  Wage:{Pay.WageFor(warlock.Creature.Level)}g{unhappyTag}  Happy:{warlock.Happiness.Value:0} ({warlock.Happiness.Tier})");
            }

            GUILayout.EndScrollView();
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

        private string PlacementButtonLabel(PlacementAction action, string label)
        {
            return _interactionController.PendingPlacementAction == action ? $"{label} (active)" : label;
        }
    }
}

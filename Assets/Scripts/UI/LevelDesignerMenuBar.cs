using System;
using UnityEngine;
using UnityEngine.InputSystem;
using KeepersDomain.Core;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Rooms;

namespace KeepersDomain.UI
{
    /// The Level Designer's own control surface — a bottom bar with 6
    /// expandable menus (Map Information, Player Settings, Map Design,
    /// Rooms, Creatures, Save/Load), same "read state, forward button
    /// presses" shape BottomMenuBar uses for the ordinary gameplay bar.
    /// Stands entirely apart from BottomMenuBar rather than extending it —
    /// the level designer has none of gameplay's systems (BuilderJobBoard,
    /// room managers, ...) behind it, so its menus/tools are a different
    /// shape throughout, not just a reskin.
    public class LevelDesignerMenuBar : MonoBehaviour
    {
        private enum MenuTab
        {
            None,
            MapInfo,
            Players,
            MapDesign,
            Rooms,
            Creatures,
            Edit,
            Remove,
            File,
            Settings
        }

        private const float BarHeight = 44f;
        private const float PanelWidth = 380f;
        private const float PanelHeight = 300f;
        private const float TabButtonWidth = 110f;
        private const float ToolButtonWidth = 108f;
        private const float OwnerButtonWidth = 40f;

        private const float MirrorButtonDiameter = 56f;
        private const float MirrorButtonSpacing = 12f;
        private const float MirrorButtonLeftMargin = 20f;
        private const float MirrorButtonTopMargin = 120f;
        private static readonly Color MirrorXOnColor = new Color(0.2f, 0.9f, 0.3f);
        private static readonly Color MirrorYOnColor = new Color(0.9f, 0.2f, 0.2f);
        private static readonly Color MirrorOffColor = new Color(0.35f, 0.35f, 0.38f);

        private static Texture2D _circleTexture;

        public static bool PointerOverPanel { get; private set; }

        private LevelDesignerSession _session;
        private LevelDesignerInteractionController _interactionController;
        private DungeonGrid _grid;
        private JailManager _jailManager;
        private Action<string, LevelData> _onLoadRequested;

        private MenuTab _openTab = MenuTab.None;
        // Half-wall view defaults ON in the editor — authors spend most of
        // their time looking down into rooms, so full-height walls just get
        // in the way (see DungeonGrid.SetHalfWalls / JailManager.SetHalfWalls).
        private bool _halfWallsOn = true;
        private int _selectedOwnerId;
        private Vector2 _playersScrollPos;
        private Vector2 _fileScrollPos;
        private Vector2 _mapDesignScrollPos;
        private string _levelNameInput = "MyLevel";
        private string _statusMessage = "";

        private Rect _mirrorXButtonRect;
        private Rect _mirrorYButtonRect;

        /// initialLevelName pre-fills the Save field with whatever level
        /// was just loaded (null on a brand-new map — see
        /// GameBootstrap.BuildLevelDesignerWorld/LoadLevelDesignerWorld),
        /// so re-saving defaults to overwriting the same file rather than
        /// making the player retype the name.
        public void Initialize(LevelDesignerSession session, LevelDesignerInteractionController interactionController, DungeonGrid grid, JailManager jailManager, Action<string, LevelData> onLoadRequested, string initialLevelName)
        {
            _session = session;
            _interactionController = interactionController;
            _grid = grid;
            _jailManager = jailManager;
            _onLoadRequested = onLoadRequested;
            if (!string.IsNullOrEmpty(initialLevelName))
            {
                _levelNameInput = initialLevelName;
            }

            // Push the default-on half-wall state straight through — the
            // grid and every placed Jail already exist by the time the menu
            // bar is wired (see GameBootstrap.SetUpLevelDesignerWorld).
            ApplyHalfWalls();
        }

        private void OnGUI()
        {
            var barRect = new Rect(0f, Screen.height - BarHeight, Screen.width, BarHeight);
            var panelRect = new Rect(10f, barRect.y - PanelHeight - 6f, PanelWidth, PanelHeight);
            _mirrorXButtonRect = new Rect(MirrorButtonLeftMargin, MirrorButtonTopMargin, MirrorButtonDiameter, MirrorButtonDiameter);
            _mirrorYButtonRect = new Rect(MirrorButtonLeftMargin, MirrorButtonTopMargin + MirrorButtonDiameter + MirrorButtonSpacing, MirrorButtonDiameter, MirrorButtonDiameter);

            var rawMousePos = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            var mouseScreenPos = new Vector2(rawMousePos.x, Screen.height - rawMousePos.y);
            PointerOverPanel = barRect.Contains(mouseScreenPos) || (_openTab != MenuTab.None && panelRect.Contains(mouseScreenPos))
                || _mirrorXButtonRect.Contains(mouseScreenPos) || _mirrorYButtonRect.Contains(mouseScreenPos);

            // Keeps the interaction controller's owner selection in sync
            // regardless of the order the player picks a tool vs. an
            // owner in — cheap enough to just push it every frame rather
            // than tracking whether it actually changed.
            _interactionController.SetSelectedOwner(_selectedOwnerId);

            DrawMirrorButtons();
            DrawBar(barRect);

            if (_openTab != MenuTab.None)
            {
                DrawPanel(panelRect);
            }

            DrawHoveredCoordLabel(mouseScreenPos);
        }

        /// Small (x, y) readout following the cursor — troubleshooting aid
        /// for confirming exactly which tile an edit is about to land on.
        /// Blank whenever the pointer isn't over the grid at all (over a
        /// panel, or off the map edge).
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

        /// The two round Mirror X/Y toggles on the left edge of the
        /// screen — separate from the bottom bar's tabs since they're a
        /// persistent editing mode, not a menu to open/close. Toggling one
        /// on splits the map with a divider line (green for X, red for Y
        /// — see LevelDesignerInteractionController.SetMirrorX/SetMirrorY)
        /// and mirrors every further map-design/room/structure/creature
        /// edit across it; both can be on at once for 4-way symmetry.
        private void DrawMirrorButtons()
        {
            DrawMirrorButton(_mirrorXButtonRect, "X", _interactionController.MirrorX, MirrorXOnColor, _interactionController.SetMirrorX);
            DrawMirrorButton(_mirrorYButtonRect, "Y", _interactionController.MirrorY, MirrorYOnColor, _interactionController.SetMirrorY);
        }

        /// A round toggle button — IMGUI has no native circular button
        /// style, so this draws a procedurally-generated circle texture
        /// (see GetCircleTexture) as the background, tinted per state,
        /// with an invisible GUI.Button over the same rect to catch the
        /// click and the letter drawn on top.
        private static void DrawMirrorButton(Rect rect, string label, bool isOn, Color onColor, Action<bool> setEnabled)
        {
            var previousColor = GUI.color;
            GUI.color = isOn ? onColor : MirrorOffColor;
            GUI.DrawTexture(rect, GetCircleTexture());
            GUI.color = previousColor;

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
            {
                setEnabled(!isOn);
            }

            var labelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 22,
                fontStyle = FontStyle.Bold
            };
            labelStyle.normal.textColor = Color.white;
            GUI.Label(rect, label, labelStyle);
        }

        /// Built once and cached — a plain white circle with a ~1px soft
        /// edge, tinted per call via GUI.color rather than baking color in,
        /// so the same texture serves both buttons in both on/off states.
        private static Texture2D GetCircleTexture()
        {
            if (_circleTexture != null)
            {
                return _circleTexture;
            }

            const int size = 64;
            _circleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = (size - 1) * 0.5f;
            var radius = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var dx = x - center;
                    var dy = y - center;
                    var dist = Mathf.Sqrt(dx * dx + dy * dy);
                    var alpha = Mathf.Clamp01(radius - dist);
                    _circleTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            _circleTexture.Apply();
            return _circleTexture;
        }

        private void DrawBar(Rect rect)
        {
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.BeginHorizontal();
            DrawTabButton(MenuTab.MapInfo, "Map Info");
            DrawTabButton(MenuTab.Players, "Players");
            DrawTabButton(MenuTab.MapDesign, "Map Design");
            DrawTabButton(MenuTab.Rooms, "Rooms");
            DrawTabButton(MenuTab.Creatures, "Creatures");
            DrawTabButton(MenuTab.Edit, "Edit");
            DrawTabButton(MenuTab.Remove, "Remove");
            DrawTabButton(MenuTab.File, "Save/Load");
            DrawTabButton(MenuTab.Settings, "Settings");
            GUILayout.FlexibleSpace();
            // Tears the level-designer world down and shows the main menu
            // again — same GameBootstrap.ReturnToMainMenu gameplay's own
            // BottomMenuBar uses, no confirmation prompt either.
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
                case MenuTab.MapInfo:
                    DrawMapInfoMenu();
                    break;
                case MenuTab.Players:
                    DrawPlayersMenu();
                    break;
                case MenuTab.MapDesign:
                    DrawMapDesignMenu();
                    break;
                case MenuTab.Rooms:
                    DrawRoomsMenu();
                    break;
                case MenuTab.Creatures:
                    DrawCreaturesMenu();
                    break;
                case MenuTab.Edit:
                    DrawEditMenu();
                    break;
                case MenuTab.Remove:
                    DrawRemoveMenu();
                    break;
                case MenuTab.File:
                    DrawFileMenu();
                    break;
                case MenuTab.Settings:
                    DrawSettingsMenu();
                    break;
            }
            GUILayout.EndArea();
        }

        private void DrawMapInfoMenu()
        {
            GUILayout.Label($"Map Size: {_session.MapWidth} x {_session.MapHeight}");
            GUILayout.Label($"Mode: {(_session.Multiplayer ? "Multiplayer" : "Singleplayer")}");
            GUILayout.Space(8f);

            _session.GetPlayerCountRange(out var min, out var max);
            var newCount = DrawIntStepper("Amount of Players", _session.Players.Count, min, max, 1);
            if (newCount != _session.Players.Count)
            {
                _session.SetPlayerCount(newCount);
            }

            if (!_session.Multiplayer && _session.Players.Count > 1)
            {
                var aiCount = _session.Players.Count - 1;
                GUILayout.Label($"({aiCount} AI player{(aiCount == 1 ? "" : "s")})");
            }
        }

        private void DrawPlayersMenu()
        {
            _playersScrollPos = GUILayout.BeginScrollView(_playersScrollPos, GUILayout.Height(250f));

            for (int i = 0; i < _session.Players.Count; i++)
            {
                var player = _session.Players[i];

                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label($"Player {i + 1} — {LevelDesignerColors.Names[player.ColorIndex]}");

                GUILayout.BeginHorizontal();
                if (GUILayout.Toggle(!player.IsAI, "Player", GUI.skin.button))
                {
                    player.IsAI = false;
                }
                if (GUILayout.Toggle(player.IsAI, "AI", GUI.skin.button))
                {
                    player.IsAI = true;
                }
                GUILayout.EndHorizontal();

                GUILayout.Label("Color");
                GUILayout.BeginHorizontal();
                for (int c = 0; c < LevelDesignerColors.Palette.Length; c++)
                {
                    var previousColor = GUI.color;
                    GUI.color = LevelDesignerColors.Palette[c];
                    if (GUILayout.Toggle(player.ColorIndex == c, player.ColorIndex == c ? "■" : "□", GUI.skin.button, GUILayout.Width(28f)))
                    {
                        player.ColorIndex = c;
                        // Without this, DungeonGrid's cached owner-color
                        // array (and every already-placed Claimed tile's
                        // baked-in tint) stays stale until the level is
                        // reloaded — see LevelDesignerSession.
                        // RefreshGridOwnerColors's own comment.
                        _session.RefreshGridOwnerColors();
                    }
                    GUI.color = previousColor;
                }
                GUILayout.EndHorizontal();

                player.StartingGold = DrawIntStepper("Starting Gold", player.StartingGold, 0, 10000, 100);
                player.StartingMana = DrawIntStepper("Starting Mana", player.StartingMana, 0, 5000, 50);

                GUILayout.EndVertical();
                GUILayout.Space(6f);
            }

            GUILayout.EndScrollView();
        }

        private void DrawMapDesignMenu()
        {
            var interactionController = _interactionController;
            var activeTool = interactionController.MapDesignTool;

            // Scrolls — the wall/terrain/floor/bridge tool groups plus the
            // conditional owner selector overflow the fixed-height panel
            // (same reason Players / Save-Load menus scroll).
            _mapDesignScrollPos = GUILayout.BeginScrollView(_mapDesignScrollPos, GUILayout.Height(250f));

            GUILayout.Label("Walls");
            BeginButtonRow();
            DrawMapToolButton(MapDesignTool.PlainWall, "Plain");
            DrawMapToolButton(MapDesignTool.ReinforcedWall, "Reinforced");
            DrawMapToolButton(MapDesignTool.Bedrock, "Bedrock");
            EndButtonRow();
            BeginButtonRow();
            DrawMapToolButton(MapDesignTool.GoldWall, "Gold Wall");
            DrawMapToolButton(MapDesignTool.RegeneratingGoldWall, "Regen. Gold");
            DrawMapToolButton(MapDesignTool.ManaCrystalWall, "Mana Crystal");
            EndButtonRow();

            GUILayout.Space(8f);
            GUILayout.Label("Terrain");
            BeginButtonRow();
            DrawMapToolButton(MapDesignTool.Water, "Water");
            DrawMapToolButton(MapDesignTool.Lava, "Lava");
            DrawMapToolButton(MapDesignTool.Chasm, "Chasm");
            EndButtonRow();
            BeginButtonRow();
            DrawMapToolButton(MapDesignTool.HolyGround, "Holy Ground");
            // Bridge sits with terrain — it paints onto Water/Lava.
            DrawMapToolButton(MapDesignTool.Bridge, "Bridge");
            EndButtonRow();

            GUILayout.Space(8f);
            GUILayout.Label("Floor");
            BeginButtonRow();
            DrawMapToolButton(MapDesignTool.UnclaimedFloor, "Unclaimed");
            DrawMapToolButton(MapDesignTool.ClaimedFloor, "Claimed");
            EndButtonRow();

            if (activeTool == MapDesignTool.ClaimedFloor || activeTool == MapDesignTool.ReinforcedWall
                || activeTool == MapDesignTool.Bridge)
            {
                GUILayout.Space(8f);
                DrawOwnerSelector("Belongs to:");
                if (activeTool == MapDesignTool.Bridge)
                {
                    GUILayout.Label("Paint over Water/Lava. Drag to lay a run.");
                }
            }

            GUILayout.EndScrollView();
        }

        private void DrawRoomsMenu()
        {
            GUILayout.Label("Rooms (free — no gold cost, no minimum size)");
            BeginButtonRow();
            DrawRoomToolButton(RoomDesignTool.Lair, "Lair");
            DrawRoomToolButton(RoomDesignTool.Treasury, "Treasury");
            DrawRoomToolButton(RoomDesignTool.SlimeHatchery, "Slime Hatchery");
            EndButtonRow();
            BeginButtonRow();
            DrawRoomToolButton(RoomDesignTool.Tavern, "Tavern");
            DrawRoomToolButton(RoomDesignTool.TrainingRoom, "Training Room");
            DrawRoomToolButton(RoomDesignTool.Library, "Library");
            EndButtonRow();
            BeginButtonRow();
            DrawRoomToolButton(RoomDesignTool.Jail, "Jail");
            DrawRoomToolButton(RoomDesignTool.ConversionClass, "Conversion Class");
            EndButtonRow();

            GUILayout.Space(8f);
            if (_interactionController.RoomTool != RoomDesignTool.None)
            {
                // A room must belong to a player (its tiles are Claimed
                // Floor — see LevelDesignerInteractionController.
                // PlaceRoomFootprint, which no-ops with no owner picked),
                // so the same selector Structures/Creatures use is shown
                // here too rather than silently defaulting to Player 1.
                DrawOwnerSelector("Belongs to:");
                GUILayout.Space(4f);
                GUILayout.Label(_selectedOwnerId < 0
                    ? "Rooms must belong to a player — pick one above."
                    : "Drag to size, release to place.");
            }

            if (_interactionController.IsPlacingRoom)
            {
                var start = _interactionController.RoomDragStartCoord;
                var current = _interactionController.RoomDragCurrentCoord;
                var w = Mathf.Abs(current.x - start.x) + 1;
                var h = Mathf.Abs(current.y - start.y) + 1;
                GUILayout.Label($"Placing {_interactionController.RoomTool} — {w}x{h} (release to place)");
            }

            // Structures — fixed 5x5/3x3 footprints reusing the real
            // ThroneRoom/Portal components (see LevelDesignerSession.
            // PlaceStructure), placed with a single tap rather than a
            // drag, unlike every ordinary room above.
            GUILayout.Space(10f);
            GUILayout.Label("Structures (fixed size, tap to place)");
            BeginButtonRow();
            DrawStructureToolButton(StructureKind.ThroneRoom, "Throne Room");
            DrawStructureToolButton(StructureKind.PortalRoom, "Portal Room");
            EndButtonRow();

            if (_interactionController.StructureTool.HasValue)
            {
                GUILayout.Space(8f);
                DrawOwnerSelector("Belongs to:");
            }
        }

        private void DrawCreaturesMenu()
        {
            BeginButtonRow();
            DrawCreatureToolButton(EditorCreatureKind.Imp, "Imp");
            DrawCreatureToolButton(EditorCreatureKind.Gremlin, "Gremlin");
            DrawCreatureToolButton(EditorCreatureKind.Warlock, "Warlock");
            EndButtonRow();
            BeginButtonRow();
            DrawCreatureToolButton(EditorCreatureKind.MazeRattler, "Maze Rattler");
            DrawCreatureToolButton(EditorCreatureKind.BeanCounter, "Bean Counter");
            DrawCreatureToolButton(EditorCreatureKind.Elf, "Elf");
            EndButtonRow();

            if (_interactionController.CreatureTool.HasValue)
            {
                GUILayout.Space(8f);
                DrawOwnerSelector("Belongs to:");
                GUILayout.Label("Tap a tile to place.");
            }

            GUILayout.Space(8f);
            GUILayout.Label($"{_session.Creatures.Count} creature(s) placed");
        }

        /// A 5th tool category (see LevelDesignerInteractionController.
        /// SetEditMode) — an on/off toggle, same GUILayout.Toggle-as-
        /// button pattern every other tool button here already uses, plus
        /// a readout of whatever Edit mode's last tap selected (see
        /// SelectAt) and the same DrawOwnerSelector every placement tool
        /// already uses, repurposed here as "reassign to" instead of
        /// "belongs to."
        private void DrawEditMenu()
        {
            GUILayout.Label("Tap an already-placed tile, wall, room, structure, or creature to select it, then reassign who it belongs to.");
            GUILayout.Space(8f);

            var isEditModeOn = _interactionController.EditMode;
            var pressed = GUILayout.Toggle(isEditModeOn, isEditModeOn ? "[Edit Mode: ON]" : "Edit Mode: OFF", GUI.skin.button);
            if (pressed != isEditModeOn)
            {
                _interactionController.SetEditMode(pressed);
            }

            if (!_interactionController.EditMode)
            {
                return;
            }

            GUILayout.Space(8f);

            var selection = _interactionController.SelectionKind;
            if (selection == EditSelectionKind.None || !_interactionController.SelectedCoord.HasValue)
            {
                GUILayout.Label("Nothing selected.");
                return;
            }

            var coord = _interactionController.SelectedCoord.Value;
            var currentOwnerId = _interactionController.SelectedCurrentOwnerId;
            var currentOwnerLabel = currentOwnerId >= 0 && currentOwnerId < _session.Players.Count
                ? $"Player {currentOwnerId + 1}"
                : "Unclaimed";
            GUILayout.Label($"Selected: {selection} at ({coord.x}, {coord.y}) — currently {currentOwnerLabel}");

            GUILayout.Space(8f);
            DrawOwnerSelector("Reassign to:");

            if (GUILayout.Button("Reassign", GUILayout.Height(28f)))
            {
                _interactionController.ReassignSelectedOwner(_selectedOwnerId);
            }
        }

        /// A 6th tool category — an on/off mode (same GUILayout.Toggle-as-
        /// button shape Edit mode uses) that turns every tap into a delete:
        /// walls, terrain, floor, rooms, structures, creatures. See
        /// LevelDesignerInteractionController.RemoveAt.
        private void DrawRemoveMenu()
        {
            var isRemoveModeOn = _interactionController.RemoveMode;
            var pressed = GUILayout.Toggle(isRemoveModeOn, isRemoveModeOn ? "[Remove Mode: ON]" : "Remove Mode: OFF", GUI.skin.button);
            if (pressed != isRemoveModeOn)
            {
                _interactionController.SetRemoveMode(pressed);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Tap a wall, terrain tile, floor, room, structure, or creature to delete it. Rooms and structures take their whole footprint back to plain rock. Not mirrored, and there's no undo.");
        }

        /// Display-only options that don't touch the level data — currently
        /// just the half-wall view toggle (default ON in the editor, see
        /// the _halfWallsOn field). Mirrors BottomMenuBar's own Settings
        /// menu in ordinary gameplay.
        private void DrawSettingsMenu()
        {
            var halfWallsOn = GUILayout.Toggle(_halfWallsOn, "Half wall");
            if (halfWallsOn != _halfWallsOn)
            {
                _halfWallsOn = halfWallsOn;
                ApplyHalfWalls();
            }
            GUILayout.Label("Squashes every wall to half height — bottom half kept, top pressed down. Also lowers Jail pit rims. Not saved with the level.");
        }

        private void ApplyHalfWalls()
        {
            _grid.SetHalfWalls(_halfWallsOn);
            _jailManager.SetHalfWalls(_halfWallsOn);
        }

        private void DrawFileMenu()
        {
            GUILayout.Label("Level name");
            _levelNameInput = GUILayout.TextField(_levelNameInput, GUILayout.Width(240f));

            if (GUILayout.Button("Save", GUILayout.Width(100f)))
            {
                var sanitized = LevelFileIO.SanitizeName(_levelNameInput);
                if (string.IsNullOrEmpty(sanitized))
                {
                    _statusMessage = "Enter a level name first.";
                }
                else
                {
                    _levelNameInput = sanitized;
                    LevelFileIO.Save(sanitized, _session.BuildLevelData());
                    _statusMessage = $"Saved as \"{sanitized}\".";
                }
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Label(_statusMessage);
            }

            GUILayout.Space(10f);
            GUILayout.Label("Load a saved level");
            _fileScrollPos = GUILayout.BeginScrollView(_fileScrollPos, GUILayout.Height(150f));

            var levelNames = LevelFileIO.ListLevelNames();
            if (levelNames.Length == 0)
            {
                GUILayout.Label("No saved levels yet.");
            }
            else
            {
                foreach (var levelName in levelNames)
                {
                    if (GUILayout.Button(levelName))
                    {
                        var data = LevelFileIO.Load(levelName);
                        if (data != null)
                        {
                            _onLoadRequested?.Invoke(levelName, data);
                        }
                    }
                }
            }

            GUILayout.EndScrollView();
        }

        /// Shared by Map Design's Claimed Tile tool and every Creatures
        /// tool — "make the claimed tile one have a selection box for whom
        /// the tile belongs to before placing it" from the brief, reused
        /// as-is for creature ownership too since it's the same choice.
        /// The gray "Unclaimed" entry is a pseudo-player standing in for
        /// ownerId -1 ("no owner") — every consumer already treats a
        /// negative id that way (unclaimed floor, no creature owner-ring,
        /// a Reinforced wall's default-blue orb). Rooms are the one thing
        /// that can't be unowned (see DrawRoomsMenu).
        private void DrawOwnerSelector(string label)
        {
            GUILayout.Label(label);
            GUILayout.BeginHorizontal();

            var previousColor = GUI.color;
            GUI.color = LevelDesignerColors.Unowned;
            if (GUILayout.Toggle(_selectedOwnerId < 0, "Unclaimed", GUI.skin.button, GUILayout.Width(OwnerButtonWidth * 2f)))
            {
                _selectedOwnerId = -1;
            }
            GUI.color = previousColor;

            for (int i = 0; i < _session.Players.Count; i++)
            {
                previousColor = GUI.color;
                GUI.color = _session.Players[i].Color;
                if (GUILayout.Toggle(_selectedOwnerId == i, $"P{i + 1}", GUI.skin.button, GUILayout.Width(OwnerButtonWidth)))
                {
                    _selectedOwnerId = i;
                }
                GUI.color = previousColor;
            }
            GUILayout.EndHorizontal();
        }

        private void DrawMapToolButton(MapDesignTool tool, string label)
        {
            var isSelected = _interactionController.MapDesignTool == tool;
            var pressed = GUILayout.Toggle(isSelected, label, GUI.skin.button, GUILayout.Width(ToolButtonWidth));
            if (pressed != isSelected)
            {
                _interactionController.SetMapDesignTool(pressed ? tool : MapDesignTool.None);
            }
        }

        private void DrawRoomToolButton(RoomDesignTool tool, string label)
        {
            var isSelected = _interactionController.RoomTool == tool;
            var pressed = GUILayout.Toggle(isSelected, label, GUI.skin.button, GUILayout.Width(ToolButtonWidth));
            if (pressed != isSelected)
            {
                _interactionController.SetRoomTool(pressed ? tool : RoomDesignTool.None);
            }
        }

        private void DrawStructureToolButton(StructureKind kind, string label)
        {
            var isSelected = _interactionController.StructureTool == kind;
            var pressed = GUILayout.Toggle(isSelected, label, GUI.skin.button, GUILayout.Width(ToolButtonWidth));
            if (pressed != isSelected)
            {
                _interactionController.SetStructureTool(pressed ? kind : (StructureKind?)null);
            }
        }

        private void DrawCreatureToolButton(EditorCreatureKind kind, string label)
        {
            var isSelected = _interactionController.CreatureTool == kind;
            var pressed = GUILayout.Toggle(isSelected, label, GUI.skin.button, GUILayout.Width(ToolButtonWidth));
            if (pressed != isSelected)
            {
                _interactionController.SetCreatureTool(pressed ? kind : (EditorCreatureKind?)null);
            }
        }

        private static void BeginButtonRow()
        {
            GUILayout.BeginHorizontal();
        }

        private static void EndButtonRow()
        {
            GUILayout.EndHorizontal();
        }

        /// "-"/"+" stepper, same shape LevelDesignerPropertiesMenu's own
        /// DrawStepper uses (kept as its own small copy here rather than a
        /// shared helper — this one flows with GUILayout for a
        /// variable-length player list, the properties screen's own uses
        /// fixed Rects instead, so the two don't actually share a signature).
        private static int DrawIntStepper(string label, int value, int min, int max, int step)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            GUI.enabled = value > min;
            if (GUILayout.Button("-", GUILayout.Width(24f)))
            {
                value = Mathf.Max(min, value - step);
            }
            GUI.enabled = true;
            GUILayout.Label(value.ToString(), GUILayout.Width(50f));
            GUI.enabled = value < max;
            if (GUILayout.Button("+", GUILayout.Width(24f)))
            {
                value = Mathf.Min(max, value + step);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            return value;
        }
    }
}

using System;
using UnityEngine;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.UI
{
    /// Data collected by LevelDesignerPropertiesMenu before a new level is
    /// created — handed off via GameBootstrap once the level-designer
    /// canvas itself exists to actually consume it.
    public struct LevelDesignerProperties
    {
        public bool Multiplayer;
        public int PlayerCount;
        public int MapWidth;
        public int MapHeight;
    }

    /// Reached from the main menu's "Level Designer" button — collects the
    /// level's up-front properties before the level-designer canvas itself
    /// (which doesn't exist yet) would open. See
    /// GameBootstrap.ShowLevelDesignerProperties.
    public class LevelDesignerPropertiesMenu : MonoBehaviour
    {
        private const int MapDimensionStandard = 64;
        private const int MapDimensionMin = 12;
        private const int MapDimensionMax = 256;
        private const int MapDimensionStep = 4;

        // Singleplayer's "Amount of Players" doubles as a total headcount —
        // 1 is just the human Keeper, anything above that fills the rest
        // with AI Keepers (see the hint drawn under the stepper).
        private const int SingleplayerStandardPlayers = 1;
        private const int SingleplayerMinPlayers = 1;
        private const int SingleplayerMaxPlayers = 4;

        private const int MultiplayerStandardPlayers = 2;
        private const int MultiplayerMinPlayers = 2;
        private const int MultiplayerMaxPlayers = 4;

        private const float PanelWidth = 380f;
        private const float RowHeight = 32f;
        private const float RowSpacing = 12f;
        private const float ButtonWidth = 160f;
        private const float ButtonHeight = 44f;

        private const float LoadPanelWidth = 260f;
        private const float LoadPanelSpacing = 20f;
        private const float LoadPanelHeight = 300f;

        private Action _onBack;
        private Action<LevelDesignerProperties> _onCreate;
        private Action<string, LevelData> _onLoad;

        private bool _multiplayer;
        private int _playerCount;
        private int _mapWidth;
        private int _mapHeight;
        private Vector2 _loadScrollPos;

        /// onLoad lets this screen skip "configure a new map" entirely and
        /// jump straight into editing a previously saved one instead — see
        /// the "Load Existing Level" list drawn alongside the properties
        /// form. Same (name, LevelData) shape LevelDesignerMenuBar's own
        /// Save/Load tab already uses (GameBootstrap.
        /// LoadLevelDesignerWorld), so both entry points share one loader.
        public void Initialize(Action onBack, Action<LevelDesignerProperties> onCreate, Action<string, LevelData> onLoad)
        {
            _onBack = onBack;
            _onCreate = onCreate;
            _onLoad = onLoad;

            _multiplayer = false;
            _playerCount = SingleplayerStandardPlayers;
            _mapWidth = MapDimensionStandard;
            _mapHeight = MapDimensionStandard;
        }

        private void OnGUI()
        {
            var centerX = Screen.width * 0.5f;
            var panelX = centerX - PanelWidth * 0.5f;
            var y = Screen.height * 0.2f;

            var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(panelX, y, PanelWidth, 40f), "Level Properties", titleStyle);
            y += 40f + RowSpacing * 2f;

            var multiplayerRect = new Rect(panelX, y, PanelWidth, RowHeight);
            var multiplayerOn = GUI.Toggle(multiplayerRect, _multiplayer, " Multiplayer");
            if (multiplayerOn != _multiplayer)
            {
                _multiplayer = multiplayerOn;
                GetPlayerCountRange(out _, out _, out var standard);
                _playerCount = standard;
            }
            y += RowHeight + RowSpacing;

            GetPlayerCountRange(out var playerMin, out var playerMax, out _);
            DrawStepper(new Rect(panelX, y, PanelWidth, RowHeight), "Amount of Players", ref _playerCount, playerMin, playerMax, 1);
            y += RowHeight;

            // Spells out what the count actually means in singleplayer,
            // per the brief — "Standard 1, max 4 (3 AI players in that
            // case)" — since the stepper alone doesn't make that obvious.
            if (!_multiplayer && _playerCount > 1)
            {
                var aiCount = _playerCount - 1;
                var hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Italic };
                GUI.Label(new Rect(panelX, y, PanelWidth, 20f), $"({aiCount} AI player{(aiCount == 1 ? "" : "s")})", hintStyle);
            }
            y += 20f + RowSpacing;

            DrawStepper(new Rect(panelX, y, PanelWidth, RowHeight), "Map Width", ref _mapWidth, MapDimensionMin, MapDimensionMax, MapDimensionStep);
            y += RowHeight + RowSpacing;

            DrawStepper(new Rect(panelX, y, PanelWidth, RowHeight), "Map Height", ref _mapHeight, MapDimensionMin, MapDimensionMax, MapDimensionStep);
            y += RowHeight + RowSpacing * 2f;

            var backRect = new Rect(panelX, y, ButtonWidth, ButtonHeight);
            var createRect = new Rect(panelX + PanelWidth - ButtonWidth, y, ButtonWidth, ButtonHeight);

            if (GUI.Button(backRect, "Back"))
            {
                _onBack?.Invoke();
                Destroy(gameObject);
            }

            if (GUI.Button(createRect, "Create Level"))
            {
                _onCreate?.Invoke(new LevelDesignerProperties
                {
                    Multiplayer = _multiplayer,
                    PlayerCount = _playerCount,
                    MapWidth = _mapWidth,
                    MapHeight = _mapHeight
                });
                Destroy(gameObject);
            }

            DrawLoadPanel(panelX + PanelWidth + LoadPanelSpacing, Screen.height * 0.2f);
        }

        /// Lists every saved level (see LevelFileIO.ListLevelNames) next to
        /// the new-map form — clicking one skips property setup entirely
        /// and loads it straight into the editor via _onLoad, same as
        /// LevelDesignerMenuBar's own Save/Load tab.
        private void DrawLoadPanel(float x, float y)
        {
            var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(x, y, LoadPanelWidth, 26f), "Load Existing Level", titleStyle);
            y += 30f;

            var listRect = new Rect(x, y, LoadPanelWidth, LoadPanelHeight);
            var levelNames = LevelFileIO.ListLevelNames();
            var contentHeight = Mathf.Max(LoadPanelHeight, levelNames.Length * 32f);
            var contentRect = new Rect(0f, 0f, LoadPanelWidth - 20f, contentHeight);

            GUI.Box(listRect, GUIContent.none);
            _loadScrollPos = GUI.BeginScrollView(listRect, _loadScrollPos, contentRect);

            if (levelNames.Length == 0)
            {
                GUI.Label(new Rect(4f, 4f, contentRect.width - 8f, 24f), "No saved levels yet.");
            }
            else
            {
                var rowY = 4f;
                foreach (var levelName in levelNames)
                {
                    if (GUI.Button(new Rect(4f, rowY, contentRect.width - 8f, 26f), levelName))
                    {
                        var data = LevelFileIO.Load(levelName);
                        if (data != null)
                        {
                            _onLoad?.Invoke(levelName, data);
                            Destroy(gameObject);
                        }
                    }
                    rowY += 32f;
                }
            }

            GUI.EndScrollView();
        }

        private void GetPlayerCountRange(out int min, out int max, out int standard)
        {
            if (_multiplayer)
            {
                min = MultiplayerMinPlayers;
                max = MultiplayerMaxPlayers;
                standard = MultiplayerStandardPlayers;
            }
            else
            {
                min = SingleplayerMinPlayers;
                max = SingleplayerMaxPlayers;
                standard = SingleplayerStandardPlayers;
            }
        }

        /// "-"/"+" stepper rather than a free-text field — this prototype's
        /// other menus (MainMenu, BottomMenuBar) don't do free-text input at
        /// all yet, and a stepper keeps every value trivially clamped to its
        /// min/max without needing to parse or validate anything.
        private static void DrawStepper(Rect rect, string label, ref int value, int min, int max, int step)
        {
            const float LabelWidth = 170f;
            const float ButtonSize = 28f;
            const float ValueWidth = 50f;

            GUI.Label(new Rect(rect.x, rect.y, LabelWidth, rect.height), label);

            var minusRect = new Rect(rect.x + LabelWidth, rect.y, ButtonSize, rect.height);
            var valueRect = new Rect(minusRect.xMax + 4f, rect.y, ValueWidth, rect.height);
            var plusRect = new Rect(valueRect.xMax + 4f, rect.y, ButtonSize, rect.height);

            GUI.enabled = value > min;
            if (GUI.Button(minusRect, "-"))
            {
                value = Mathf.Max(min, value - step);
            }
            GUI.enabled = true;

            var valueStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(valueRect, value.ToString(), valueStyle);

            GUI.enabled = value < max;
            if (GUI.Button(plusRect, "+"))
            {
                value = Mathf.Min(max, value + step);
            }
            GUI.enabled = true;
        }
    }
}

using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace KeepersDomain.Core
{
    /// Player-facing options, persisted to PlayerPrefs. There's no options
    /// asset or ScriptableObject anywhere in this prototype (everything is
    /// built procedurally by GameBootstrap), so this is a plain static store:
    /// Load() once at startup, mutate through the setters, Save() writes back.
    ///
    /// Covers what the main-menu Settings screen (SettingsMenu) exposes:
    ///   - display mode + resolution (applied via Screen.SetResolution)
    ///   - the player's display name (used by multiplayer chat)
    ///   - the four camera-orbit keys (read by IsoCameraController)
    ///
    /// Everything else the game reads off the mouse (right-drag pan, wheel
    /// zoom, left-click select/dig) or fixed modifier keys, and isn't
    /// rebindable here.
    public static class GameSettings
    {
        private const string KeyPrefix = "kd.settings.";

        // ---- display ----

        public static FullScreenMode DisplayMode { get; private set; } = FullScreenMode.FullScreenWindow;

        /// 0x0 until the player picks one — treated as "current desktop
        /// resolution" by ApplyDisplay.
        public static int ResolutionWidth { get; private set; }
        public static int ResolutionHeight { get; private set; }

        // ---- identity ----

        public const string DefaultPlayerName = "Keeper";
        public const int MaxPlayerNameLength = 20;

        public static string PlayerName { get; private set; } = DefaultPlayerName;

        // ---- camera orbit keys ----

        public const Key DefaultOrbitUp = Key.UpArrow;
        public const Key DefaultOrbitDown = Key.DownArrow;
        public const Key DefaultOrbitLeft = Key.LeftArrow;
        public const Key DefaultOrbitRight = Key.RightArrow;

        public static Key OrbitUpKey { get; private set; } = DefaultOrbitUp;
        public static Key OrbitDownKey { get; private set; } = DefaultOrbitDown;
        public static Key OrbitLeftKey { get; private set; } = DefaultOrbitLeft;
        public static Key OrbitRightKey { get; private set; } = DefaultOrbitRight;

        public enum OrbitAction { Up, Down, Left, Right }

        /// Raised whenever anything here changes and is saved — nothing
        /// listens yet (every reader polls the properties directly), but
        /// it's the hook for anything that needs to react live.
        public static event Action Changed;

        private static bool _loaded;

        // ---- lifecycle ----

        /// Called once from GameBootstrap.Init, before the main menu shows.
        public static void Load()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            // Default to however the build actually launched, so a player
            // who's never opened Settings isn't yanked into a different
            // mode on first run.
            var hasDisplayPref = PlayerPrefs.HasKey(KeyPrefix + "displayMode");
            DisplayMode = (FullScreenMode)PlayerPrefs.GetInt(KeyPrefix + "displayMode", (int)Screen.fullScreenMode);
            ResolutionWidth = PlayerPrefs.GetInt(KeyPrefix + "resWidth", 0);
            ResolutionHeight = PlayerPrefs.GetInt(KeyPrefix + "resHeight", 0);

            PlayerName = SanitizeName(PlayerPrefs.GetString(KeyPrefix + "playerName", DefaultPlayerName));

            OrbitUpKey = (Key)PlayerPrefs.GetInt(KeyPrefix + "orbitUp", (int)DefaultOrbitUp);
            OrbitDownKey = (Key)PlayerPrefs.GetInt(KeyPrefix + "orbitDown", (int)DefaultOrbitDown);
            OrbitLeftKey = (Key)PlayerPrefs.GetInt(KeyPrefix + "orbitLeft", (int)DefaultOrbitLeft);
            OrbitRightKey = (Key)PlayerPrefs.GetInt(KeyPrefix + "orbitRight", (int)DefaultOrbitRight);

            // Only touch the screen if the player has actually chosen
            // something before — otherwise leave the launch state alone.
            if (hasDisplayPref)
            {
                ApplyDisplay();
            }
        }

        public static void Save()
        {
            PlayerPrefs.SetInt(KeyPrefix + "displayMode", (int)DisplayMode);
            PlayerPrefs.SetInt(KeyPrefix + "resWidth", ResolutionWidth);
            PlayerPrefs.SetInt(KeyPrefix + "resHeight", ResolutionHeight);
            PlayerPrefs.SetString(KeyPrefix + "playerName", PlayerName);
            PlayerPrefs.SetInt(KeyPrefix + "orbitUp", (int)OrbitUpKey);
            PlayerPrefs.SetInt(KeyPrefix + "orbitDown", (int)OrbitDownKey);
            PlayerPrefs.SetInt(KeyPrefix + "orbitLeft", (int)OrbitLeftKey);
            PlayerPrefs.SetInt(KeyPrefix + "orbitRight", (int)OrbitRightKey);
            PlayerPrefs.Save();

            Changed?.Invoke();
        }

        // ---- display ----

        /// Pushes DisplayMode + resolution to the screen. A 0x0 resolution
        /// means "keep the current one" — only the mode changes.
        public static void ApplyDisplay()
        {
            var w = ResolutionWidth > 0 ? ResolutionWidth : Screen.width;
            var h = ResolutionHeight > 0 ? ResolutionHeight : Screen.height;
            Screen.SetResolution(w, h, DisplayMode);
        }

        public static void SetDisplayMode(FullScreenMode mode)
        {
            DisplayMode = mode;
            ApplyDisplay();
            Save();
        }

        public static void SetResolution(int width, int height)
        {
            ResolutionWidth = Mathf.Max(0, width);
            ResolutionHeight = Mathf.Max(0, height);
            ApplyDisplay();
            Save();
        }

        // ---- identity ----

        public static void SetPlayerName(string name)
        {
            var clean = SanitizeName(name);
            if (clean == PlayerName)
            {
                return;
            }

            PlayerName = clean;
            Save();
        }

        /// Trims, collapses to a single line, caps the length, and falls
        /// back to the default rather than ever returning empty.
        public static string SanitizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return DefaultPlayerName;
            }

            raw = raw.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
            if (raw.Length > MaxPlayerNameLength)
            {
                raw = raw.Substring(0, MaxPlayerNameLength).Trim();
            }

            return raw.Length == 0 ? DefaultPlayerName : raw;
        }

        // ---- camera orbit keys ----

        public static Key GetOrbitKey(OrbitAction action)
        {
            switch (action)
            {
                case OrbitAction.Up: return OrbitUpKey;
                case OrbitAction.Down: return OrbitDownKey;
                case OrbitAction.Left: return OrbitLeftKey;
                default: return OrbitRightKey;
            }
        }

        public static void SetOrbitKey(OrbitAction action, Key key)
        {
            switch (action)
            {
                case OrbitAction.Up: OrbitUpKey = key; break;
                case OrbitAction.Down: OrbitDownKey = key; break;
                case OrbitAction.Left: OrbitLeftKey = key; break;
                case OrbitAction.Right: OrbitRightKey = key; break;
            }

            Save();
        }

        public static void ResetOrbitKeys()
        {
            OrbitUpKey = DefaultOrbitUp;
            OrbitDownKey = DefaultOrbitDown;
            OrbitLeftKey = DefaultOrbitLeft;
            OrbitRightKey = DefaultOrbitRight;
            Save();
        }
    }
}

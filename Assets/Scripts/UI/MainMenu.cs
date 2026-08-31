using System;
using UnityEngine;

namespace KeepersDomain.UI
{
    /// The very first screen shown on launch — logo plus Start/Quit, gating
    /// GameBootstrap's world build until the player presses Start (see
    /// GameBootstrap.ShowMainMenu/BuildWorld).
    public class MainMenu : MonoBehaviour
    {
        private const float LogoWidth = 480f;
        private const float LogoHeight = 240f;
        private const float ButtonWidth = 220f;
        private const float ButtonHeight = 48f;
        private const float ButtonSpacing = 16f;

        private Action _onStart;
        private Action _onLevelDesigner;
        private Texture2D _logo;

        public void Initialize(Action onStart, Action onLevelDesigner)
        {
            _onStart = onStart;
            _onLevelDesigner = onLevelDesigner;
            // Resources.Load, not a serialized field — every other object in
            // this prototype is created procedurally by GameBootstrap rather
            // than wired up in the Inspector, so there's no scene asset slot
            // to drag the logo into. Assets/Resources/UI/logo.png.
            _logo = Resources.Load<Texture2D>("UI/logo");
        }

        private void OnGUI()
        {
            var centerX = Screen.width * 0.5f;

            if (_logo != null)
            {
                var logoRect = new Rect(centerX - LogoWidth * 0.5f, Screen.height * 0.22f, LogoWidth, LogoHeight);
                GUI.DrawTexture(logoRect, _logo, ScaleMode.ScaleToFit);
            }
            else
            {
                // Falls back to the game's name if the logo file is ever
                // missing, rather than leaving a blank gap.
                var titleRect = new Rect(centerX - LogoWidth * 0.5f, Screen.height * 0.3f, LogoWidth, 60f);
                var style = new GUIStyle(GUI.skin.label) { fontSize = 32, alignment = TextAnchor.MiddleCenter };
                GUI.Label(titleRect, "The Keeper's Domain", style);
            }

            var buttonsTop = Screen.height * 0.55f;
            var startRect = new Rect(centerX - ButtonWidth * 0.5f, buttonsTop, ButtonWidth, ButtonHeight);
            var levelDesignerRect = new Rect(centerX - ButtonWidth * 0.5f, buttonsTop + (ButtonHeight + ButtonSpacing), ButtonWidth, ButtonHeight);
            var quitRect = new Rect(centerX - ButtonWidth * 0.5f, buttonsTop + (ButtonHeight + ButtonSpacing) * 2f, ButtonWidth, ButtonHeight);

            if (GUI.Button(startRect, "Start Game"))
            {
                // Fire the callback before destroying this object — Initialize
                // handed us GameBootstrap.StartGame, which loads "level1" if
                // it exists or falls back to BuildWorld's fresh generation
                // otherwise — either way, this is what actually kicks off
                // GameBootstrap's dungeon construction.
                _onStart?.Invoke();
                Destroy(gameObject);
            }

            if (GUI.Button(levelDesignerRect, "Level Designer"))
            {
                // The level-designer canvas itself doesn't exist yet — this
                // hands off to its properties screen instead (see
                // GameBootstrap.ShowLevelDesignerProperties), same
                // fire-then-destroy pattern as Start Game above.
                _onLevelDesigner?.Invoke();
                Destroy(gameObject);
            }

            if (GUI.Button(quitRect, "Quit"))
            {
                Application.Quit();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
            }
        }
    }
}

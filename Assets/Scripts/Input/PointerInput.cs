using UnityEngine;
using UnityEngine.InputSystem;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using EnhancedTouchSupport = UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport;

namespace KeepersDomain.Input
{
    /// Unifies touch and mouse into one "primary pointer" so gameplay code
    /// doesn't need to branch on platform. Mouse is the editor/desktop
    /// fallback. Built on the Input System package (not the legacy
    /// UnityEngine.Input) — touch goes through EnhancedTouch since that's
    /// the package's supported replacement for polling Input.GetTouch.
    public static class PointerInput
    {
        static PointerInput()
        {
            EnhancedTouchSupport.Enable();
        }

        private static bool HasTouch => Touch.activeTouches.Count > 0;

        public static bool PrimaryDown =>
            HasTouch
                ? Touch.activeTouches[0].phase == TouchPhase.Began
                : Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;

        public static bool PrimaryHeld =>
            HasTouch
                ? Touch.activeTouches[0].phase is TouchPhase.Moved or TouchPhase.Stationary
                : Mouse.current != null && Mouse.current.leftButton.isPressed;

        public static bool PrimaryUp =>
            HasTouch
                ? Touch.activeTouches[0].phase is TouchPhase.Ended or TouchPhase.Canceled
                : Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;

        public static Vector2 PrimaryPosition =>
            HasTouch
                ? Touch.activeTouches[0].screenPosition
                : Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;

        public static int TouchCount => Touch.activeTouches.Count;

        public static Vector2 GetTouchPosition(int index) => Touch.activeTouches[index].screenPosition;
    }
}

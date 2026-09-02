using UnityEngine;
using UnityEngine.InputSystem;
using KeepersDomain.CameraControl;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.UI;

namespace KeepersDomain.Core
{
    /// Owns "which keeper is the local player driving right now." Gameplay
    /// is still single-player — this defaults to owner 0 and stays there —
    /// but the debug player switcher (BottomMenuBar, only shown on a
    /// multi-player level) calls SetActivePlayer to repoint the input
    /// controller, grab hand, HUD, and camera at another keeper's stack so
    /// each roster can be inspected/driven during testing. Number keys
    /// 1..9 do the same from the keyboard.
    public class LocalPlayerController : MonoBehaviour
    {
        private KeeperContext[] _contexts;
        private DungeonGrid _grid;
        private TileInteractionController _interaction;
        private MinionGrabController _grab;
        private BottomMenuBar _bar;
        private IsoCameraController _isoCamera;

        public int ActiveIndex { get; private set; }
        public KeeperContext Active => _contexts[ActiveIndex];

        public void Initialize(Camera camera, DungeonGrid grid, KeeperContext[] contexts,
            TileInteractionController interaction, MinionGrabController grab, BottomMenuBar bar, int activeIndex)
        {
            _grid = grid;
            _contexts = contexts;
            _interaction = interaction;
            _grab = grab;
            _bar = bar;
            _isoCamera = camera != null ? camera.GetComponent<IsoCameraController>() : null;
            ActiveIndex = Mathf.Clamp(activeIndex, 0, contexts.Length - 1);
        }

        /// Switches the local player to contexts[index] — aborts any
        /// in-progress placement/sell gesture first (see
        /// TileInteractionController.AbortInProgressGesture), then repoints
        /// input / grab / HUD and recenters the camera on that keeper's
        /// Throne Room. A no-op for an out-of-range index or the current
        /// one.
        public void SetActivePlayer(int index)
        {
            if (_contexts == null || index == ActiveIndex || index < 0 || index >= _contexts.Length)
            {
                return;
            }

            _interaction?.AbortInProgressGesture();
            ActiveIndex = index;

            var ctx = _contexts[index];
            _interaction?.SetActiveContext(ctx);
            _grab?.SetActiveContext(ctx);
            _bar?.SetActiveContext(ctx);
            _isoCamera?.CenterOn(_grid.GridToWorld(ctx.ThroneCoord));
        }

        private void Update()
        {
            if (_contexts == null || _contexts.Length <= 1 || Keyboard.current == null)
            {
                return;
            }

            for (int i = 0; i < _contexts.Length && i < 9; i++)
            {
                if (Keyboard.current[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame)
                {
                    SetActivePlayer(i);
                    break;
                }
            }
        }
    }
}

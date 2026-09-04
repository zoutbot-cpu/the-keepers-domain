using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Input;

namespace KeepersDomain.Net
{
    /// The joined client's two Milestone-1c commands — queue a dig, summon
    /// an impling — turned from a screen tap into a server RPC on NetGame.
    /// There is no local simulation to update: the tile/creature only
    /// actually changes once the host processes the RPC and replicates the
    /// result back through the normal tile-delta / creature-ghost paths,
    /// same "client renders, host decides" split every other piece of M1
    /// follows. Full tool parity with the offline TileInteractionController
    /// (claim, reinforce, build, every room, ...) is later milestones —
    /// this is deliberately just the two commands the plan calls out.
    /// Created by GameBootstrap.BuildClientWorld.
    public class ClientInputController : MonoBehaviour
    {
        private enum Command
        {
            None,
            Mine,
            SummonImpling
        }

        private const float ToolbarMargin = 10f;
        private const float ToolbarWidth = 170f;
        private const float ToolbarButtonHeight = 36f;
        private const float ToolbarButtonSpacing = 4f;

        private Camera _camera;
        private DungeonGrid _grid;
        private Command _command = Command.None;
        private Rect _toolbarRect;

        public void Initialize(Camera camera, DungeonGrid grid)
        {
            _camera = camera;
            _grid = grid;
        }

        private void OnGUI()
        {
            _toolbarRect = new Rect(ToolbarMargin, ToolbarMargin, ToolbarWidth,
                ToolbarButtonHeight * 2f + ToolbarButtonSpacing * 3f);
            GUI.Box(_toolbarRect, GUIContent.none);

            var mineRect = new Rect(_toolbarRect.x + ToolbarButtonSpacing, _toolbarRect.y + ToolbarButtonSpacing,
                ToolbarWidth - ToolbarButtonSpacing * 2f, ToolbarButtonHeight);
            var summonRect = new Rect(mineRect.x, mineRect.yMax + ToolbarButtonSpacing, mineRect.width, ToolbarButtonHeight);

            // Two mutually exclusive toggles — whichever one the click just
            // turned on wins; if the click turned the active one off, fall
            // back to None. Tapping the map (not either button) doesn't
            // touch these at all.
            var mineOn = GUI.Toggle(mineRect, _command == Command.Mine, "Mine");
            var summonOn = GUI.Toggle(summonRect, _command == Command.SummonImpling, "Summon Impling");

            if (mineOn && _command != Command.Mine)
            {
                _command = Command.Mine;
            }
            else if (summonOn && _command != Command.SummonImpling)
            {
                _command = Command.SummonImpling;
            }
            else if (!mineOn && !summonOn)
            {
                _command = Command.None;
            }
        }

        private void Update()
        {
            if (_command == Command.None || !PointerInput.PrimaryDown
                || _camera == null || _grid == null || NetGame.Instance == null)
            {
                return;
            }

            var screenPos = PointerInput.PrimaryPosition;

            // GUI space is top-left-origin, screenPos (Input System) is
            // bottom-left-origin — flip Y before testing against the
            // toolbar rect so a tap on the buttons doesn't also queue a
            // command on whatever tile happens to sit behind them.
            if (_toolbarRect.Contains(new Vector2(screenPos.x, Screen.height - screenPos.y)))
            {
                return;
            }

            if (!TryGetCoordUnderScreenPos(screenPos, out var coord))
            {
                return;
            }

            switch (_command)
            {
                case Command.Mine:
                    NetGame.Instance.RequestDigRpc(NetCoord.From(coord));
                    break;
                case Command.SummonImpling:
                    NetGame.Instance.RequestSummonImplingRpc(NetCoord.From(coord));
                    break;
            }
        }

        /// Same ground-plane raycast TileInteractionController.
        /// TryGetCoordUnderScreenPos uses for the offline game.
        private bool TryGetCoordUnderScreenPos(Vector2 screenPos, out Vector2Int coord)
        {
            var ray = _camera.ScreenPointToRay(screenPos);
            var groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (groundPlane.Raycast(ray, out var distance))
            {
                var worldPoint = ray.GetPoint(distance);
                coord = _grid.WorldToGrid(worldPoint);
                return _grid.InBounds(coord);
            }

            coord = default;
            return false;
        }
    }
}

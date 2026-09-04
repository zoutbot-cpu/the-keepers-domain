using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Input;

namespace KeepersDomain.Net
{
    /// The joined client's commands — a screen tap turned into a server RPC
    /// on NetGame. There is no local simulation to update: the tile/room/
    /// creature only actually changes once the host processes the RPC and
    /// replicates the result back through the normal tile-delta / creature-
    /// ghost / room-visual-state paths, same "client renders, host decides"
    /// split every other piece of the netcode plan follows. Full tool
    /// parity with the offline TileInteractionController (every room,
    /// Bridge, Recruit, Grab, ...) is a later M2 slice — this covers the
    /// tap-a-tile commands so far: Mine, Reinforce, Cancel, Sell, and
    /// Summon Impling. Created by GameBootstrap.BuildClientWorld.
    public class ClientInputController : MonoBehaviour
    {
        private enum Command
        {
            None,
            Mine,
            Reinforce,
            Cancel,
            Sell,
            SummonImpling
        }

        // (command, button label) — order here is the order they're drawn
        // in the toolbar.
        private static readonly (Command Command, string Label)[] Buttons =
        {
            (Command.Mine, "Mine"),
            (Command.Reinforce, "Reinforce"),
            (Command.Cancel, "Cancel"),
            (Command.Sell, "Sell"),
            (Command.SummonImpling, "Summon Impling"),
        };

        private const float ToolbarMargin = 10f;
        private const float ToolbarWidth = 170f;
        private const float ToolbarButtonHeight = 32f;
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
                (ToolbarButtonHeight + ToolbarButtonSpacing) * Buttons.Length + ToolbarButtonSpacing);
            GUI.Box(_toolbarRect, GUIContent.none);

            // Mutually exclusive toggles — whichever one the click just
            // turned on wins; if the click turned the active one off, fall
            // back to None. Tapping the map (not a button) doesn't touch
            // these at all.
            var newCommand = _command;
            var anyOn = false;
            for (int i = 0; i < Buttons.Length; i++)
            {
                var rect = new Rect(
                    _toolbarRect.x + ToolbarButtonSpacing,
                    _toolbarRect.y + ToolbarButtonSpacing + i * (ToolbarButtonHeight + ToolbarButtonSpacing),
                    ToolbarWidth - ToolbarButtonSpacing * 2f,
                    ToolbarButtonHeight);

                var wasOn = _command == Buttons[i].Command;
                var isOn = GUI.Toggle(rect, wasOn, Buttons[i].Label);
                if (isOn)
                {
                    anyOn = true;
                    if (!wasOn)
                    {
                        newCommand = Buttons[i].Command;
                    }
                }
            }

            _command = anyOn ? newCommand : Command.None;
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

            var netCoord = NetCoord.From(coord);
            switch (_command)
            {
                case Command.Mine:
                    NetGame.Instance.RequestDigRpc(netCoord);
                    break;
                case Command.Reinforce:
                    NetGame.Instance.RequestReinforceRpc(netCoord);
                    break;
                case Command.Cancel:
                    NetGame.Instance.RequestCancelJobRpc(netCoord);
                    break;
                case Command.Sell:
                    NetGame.Instance.RequestSellRoomRpc(netCoord);
                    break;
                case Command.SummonImpling:
                    NetGame.Instance.RequestSummonImplingRpc(netCoord);
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

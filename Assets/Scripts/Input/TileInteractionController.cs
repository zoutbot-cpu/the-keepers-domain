using UnityEngine;
using UnityEngine.InputSystem;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Implings;
using KeepersDomain.UI;

namespace KeepersDomain.Input
{
    /// A one-shot tool armed from the UI (see BottomMenuBar) — the very next
    /// gesture executes it and clears back to None. For most of these that's
    /// a single tap; PlaceLair, PlaceTreasury, PlaceSlimeHatchery, and
    /// PlaceBaconBeacon are drags instead (see TileInteractionController.
    /// BeginGesture/EndGesture), sizing the room's footprint to whatever
    /// rectangle gets dragged out — each room manager owns its own
    /// placement/preview logic for its room kind, but they share the same
    /// underlying validity rule (DungeonGrid.CanBuildRoomOn), plus their own
    /// minimum-size floor for the Hatchery/Beacon. SellLair is also a drag — every room
    /// tile the drag passes over gets sold, whatever kind of room it is,
    /// same line/square-paint mechanic Mine/Reinforce/Construct queuing
    /// uses (see GestureMode.Sell) — and unlike the others it stays armed
    /// (see RequestPlacement) across repeated gestures so the player can
    /// keep selling without re-pressing the button each time.
    public enum PlacementAction
    {
        None,
        PlaceLair,
        PlaceTreasury,
        PlaceSlimeHatchery,
        PlaceBaconBeacon,
        SellLair,
        SpawnImpling,
        ToggleLairClaim
    }

    /// What a plain tap/drag on the grid does — set from the Build menu's
    /// radio group. Persists across taps (unlike PlacementAction, which is
    /// consumed after one use), since this is meant to be "how I'm currently
    /// working," not a single tool pull.
    public enum BuildMode
    {
        /// Never queues anything — taps just inspect whatever's under them
        /// (a creature if one's there, otherwise the tile) so the player can
        /// look around without risking an accidental dig/reinforce/build
        /// queue. See InspectedDescription.
        View,
        Mine,
        Reinforce,
        Construct
    }

    /// Turns raw pointer input into grid actions. If a PlacementAction is
    /// armed (see RequestPlacement, driven by BottomMenuBar), the very next
    /// gesture executes it and nothing else — no fallback to the logic
    /// below. Every placement action is a single tap except PlaceLair and
    /// PlaceTreasury, which drag out a rectangle each (see
    /// BeginGesture/EndGesture) so the player can size the room to whatever
    /// footprint they need; a tap with no movement still places the common
    /// 1x1 case. Otherwise behavior is entirely driven by BuildMode (see
    /// SetBuildMode, also driven by BottomMenuBar): View taps inspect,
    /// Mine/Reinforce/Construct each queue/un-queue their own job kind on
    /// whichever tile type is actually valid for them (Rock for Mine and
    /// Reinforce, Floor for Construct) — a tap on a tile that doesn't match
    /// the current mode's target type is just ignored. Queuing gestures can
    /// paint as either a one-tile-per-step line (default) or a filled
    /// rectangle (while Shift is held, or the Build menu's Square mode
    /// toggle is on).
    ///
    /// The gesture's action is decided once, at the very first frame of the
    /// press, from that tile's state and the current BuildMode at that
    /// instant — not re-decided on release. A quick tap is just a drag that
    /// never moved: it still only applies the action once, since it's the
    /// same code path.
    public class TileInteractionController : MonoBehaviour
    {
        private enum GestureMode
        {
            None,
            Queue,
            Unqueue,
            Sell
        }

        [SerializeField] private float _tapMaxDragPixels = 12f;

        private Camera _camera;
        private DungeonGrid _grid;
        private BuilderJobBoard _jobBoard;
        private LairManager _lairManager;
        private TreasuryManager _treasuryManager;
        private SlimeHatcheryManager _slimeHatcheryManager;
        private BaconBeaconManager _baconBeaconManager;
        private ImplingSpawner _implingSpawner;

        // Sits alongside the Shift-key check so a future UI toggle (touch
        // devices have no Shift key) can flip this without touching any of
        // the gesture logic below.
        private bool _squareModeToggle;

        private BuildMode _buildMode = BuildMode.Mine;
        private string _inspectedDescription = "";

        private bool _isDragging;
        private Vector2 _dragStartScreenPos;
        private Vector2Int _dragStartCoord;
        private Vector2Int _lastPaintedCoord;
        private bool _hasLastPaintedCoord;
        private GestureMode _gestureMode;
        private bool _isViewTap;
        private bool _isPlacingLair;
        private Vector2Int _lairDragCurrentCoord;
        private bool _isPlacingTreasury;
        private Vector2Int _treasuryDragCurrentCoord;
        private bool _isPlacingHatchery;
        private Vector2Int _hatcheryDragCurrentCoord;
        private bool _isPlacingBeacon;
        private Vector2Int _beaconDragCurrentCoord;
        private PlacementAction _pendingPlacementAction;

        /// While a Lair placement is being dragged out, the rectangle so far
        /// — read by BottomMenuBar to show the player what they're about to
        /// place. Meaningless when IsPlacingLair is false.
        public bool IsPlacingLair => _isPlacingLair;
        public Vector2Int LairDragStartCoord => _dragStartCoord;
        public Vector2Int LairDragCurrentCoord => _lairDragCurrentCoord;

        /// Same idea as the Lair drag properties above, for a Treasury
        /// placement in progress instead.
        public bool IsPlacingTreasury => _isPlacingTreasury;
        public Vector2Int TreasuryDragStartCoord => _dragStartCoord;
        public Vector2Int TreasuryDragCurrentCoord => _treasuryDragCurrentCoord;

        /// Same idea again, for a Slime Hatchery placement in progress.
        public bool IsPlacingHatchery => _isPlacingHatchery;
        public Vector2Int HatcheryDragStartCoord => _dragStartCoord;
        public Vector2Int HatcheryDragCurrentCoord => _hatcheryDragCurrentCoord;

        /// Same idea again, for a Bacon Beacon placement in progress.
        public bool IsPlacingBeacon => _isPlacingBeacon;
        public Vector2Int BeaconDragStartCoord => _dragStartCoord;
        public Vector2Int BeaconDragCurrentCoord => _beaconDragCurrentCoord;

        public PlacementAction PendingPlacementAction => _pendingPlacementAction;
        public BuildMode BuildMode => _buildMode;
        public string InspectedDescription => _inspectedDescription;

        public void Initialize(Camera camera, DungeonGrid grid, BuilderJobBoard jobBoard, LairManager lairManager, TreasuryManager treasuryManager, SlimeHatcheryManager slimeHatcheryManager, BaconBeaconManager baconBeaconManager, ImplingSpawner implingSpawner)
        {
            _camera = camera;
            _grid = grid;
            _jobBoard = jobBoard;
            _lairManager = lairManager;
            _treasuryManager = treasuryManager;
            _slimeHatcheryManager = slimeHatcheryManager;
            _baconBeaconManager = baconBeaconManager;
            _implingSpawner = implingSpawner;
        }

        public void SetSquareModeToggle(bool isEnabled)
        {
            _squareModeToggle = isEnabled;
        }

        public void SetBuildMode(BuildMode mode)
        {
            _buildMode = mode;
            _inspectedDescription = "";
        }

        /// Arms a placement tool — the next gesture executes it (see
        /// TryExecutePendingPlacement for the single-tap ones, or
        /// BeginGesture for PlaceLair/PlaceTreasury/SellLair's drags)
        /// regardless of what's normally under that gesture. Every action
        /// except SellLair clears back to None right after it fires;
        /// SellLair stays armed until
        /// this is called again (with SellLair, to re-arm after a
        /// toggle-off, or None to cancel), so the player can keep selling
        /// without re-pressing the button each time — see BottomMenuBar's
        /// Sell button.
        public void RequestPlacement(PlacementAction action)
        {
            _pendingPlacementAction = action;
        }

        private void Update()
        {
            if (_camera == null || _grid == null || BottomMenuBar.PointerOverPanel)
            {
                _lairManager?.ClearSellPreview();
                return;
            }

            UpdateSellPreview();

            if (PointerInput.PrimaryDown)
            {
                BeginGesture(PointerInput.PrimaryPosition);
            }
            else if (PointerInput.PrimaryHeld && _isDragging)
            {
                ContinueGesture(PointerInput.PrimaryPosition);
            }
            else if (PointerInput.PrimaryUp && _isDragging)
            {
                EndGesture(PointerInput.PrimaryPosition);
            }
        }

        /// Live "about to sell" feedback — runs every frame the Sell tool is
        /// armed (not just while dragging), so hovering alone previews it
        /// before the player commits to a gesture. LairManager itself
        /// no-ops the marker for a tile with no room.
        private void UpdateSellPreview()
        {
            if (_pendingPlacementAction != PlacementAction.SellLair || !TryGetCoordUnderScreenPos(PointerInput.PrimaryPosition, out var coord))
            {
                _lairManager?.ClearSellPreview();
                return;
            }

            _lairManager?.ShowSellPreview(coord);
        }

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

        private void BeginGesture(Vector2 screenPos)
        {
            _isDragging = true;
            _dragStartScreenPos = screenPos;
            _hasLastPaintedCoord = false;
            _gestureMode = GestureMode.None;
            _isViewTap = false;

            if (!TryGetCoordUnderScreenPos(screenPos, out var coord))
            {
                return;
            }

            _dragStartCoord = coord;

            if (_pendingPlacementAction == PlacementAction.PlaceLair)
            {
                _isPlacingLair = true;
                _lairDragCurrentCoord = coord;
                _lairManager?.UpdatePlacementPreview(coord, coord);
                return;
            }

            if (_pendingPlacementAction == PlacementAction.PlaceTreasury)
            {
                _isPlacingTreasury = true;
                _treasuryDragCurrentCoord = coord;
                _treasuryManager?.UpdatePlacementPreview(coord, coord);
                return;
            }

            if (_pendingPlacementAction == PlacementAction.PlaceSlimeHatchery)
            {
                _isPlacingHatchery = true;
                _hatcheryDragCurrentCoord = coord;
                _slimeHatcheryManager?.UpdatePlacementPreview(coord, coord);
                return;
            }

            if (_pendingPlacementAction == PlacementAction.PlaceBaconBeacon)
            {
                _isPlacingBeacon = true;
                _beaconDragCurrentCoord = coord;
                _baconBeaconManager?.UpdatePlacementPreview(coord, coord);
                return;
            }

            if (_pendingPlacementAction == PlacementAction.SellLair)
            {
                // Draggable like Mine/Reinforce/Construct queuing — sells
                // the starting tile now, then ContinueGesture's line/square
                // paint sells every further tile the drag passes over via
                // this same ApplyGestureAction(GestureMode.Sell) path.
                _gestureMode = GestureMode.Sell;
                ApplyGestureAction(coord);
                _hasLastPaintedCoord = true;
                _lastPaintedCoord = coord;
                return;
            }

            if (TryExecutePendingPlacement(coord))
            {
                return;
            }

            if (_buildMode == BuildMode.View)
            {
                // Tap-only, resolved on release (see EndGesture) same as
                // room placement used to be — inspecting mid-drag doesn't
                // make sense, and this also guarantees View mode never
                // queues anything even if the player drags.
                _isViewTap = true;
                return;
            }

            if (TryGetModeTarget(coord, out var isAlreadyQueued))
            {
                _gestureMode = isAlreadyQueued ? GestureMode.Unqueue : GestureMode.Queue;
                ApplyGestureAction(coord);
                _hasLastPaintedCoord = true;
                _lastPaintedCoord = coord;
            }
        }

        private void ContinueGesture(Vector2 screenPos)
        {
            if (_isPlacingLair)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var lairCoord) && lairCoord != _lairDragCurrentCoord)
                {
                    _lairDragCurrentCoord = lairCoord;
                    _lairManager?.UpdatePlacementPreview(_dragStartCoord, _lairDragCurrentCoord);
                }
                return;
            }

            if (_isPlacingTreasury)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var treasuryCoord) && treasuryCoord != _treasuryDragCurrentCoord)
                {
                    _treasuryDragCurrentCoord = treasuryCoord;
                    _treasuryManager?.UpdatePlacementPreview(_dragStartCoord, _treasuryDragCurrentCoord);
                }
                return;
            }

            if (_isPlacingHatchery)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var hatcheryCoord) && hatcheryCoord != _hatcheryDragCurrentCoord)
                {
                    _hatcheryDragCurrentCoord = hatcheryCoord;
                    _slimeHatcheryManager?.UpdatePlacementPreview(_dragStartCoord, _hatcheryDragCurrentCoord);
                }
                return;
            }

            if (_isPlacingBeacon)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var beaconCoord) && beaconCoord != _beaconDragCurrentCoord)
                {
                    _beaconDragCurrentCoord = beaconCoord;
                    _baconBeaconManager?.UpdatePlacementPreview(_dragStartCoord, _beaconDragCurrentCoord);
                }
                return;
            }

            if (_gestureMode == GestureMode.None || !TryGetCoordUnderScreenPos(screenPos, out var currentCoord))
            {
                return;
            }

            if (IsSquareModeActive())
            {
                ApplySquareSelection(_dragStartCoord, currentCoord);
            }
            else
            {
                ApplyLineSelectionStep(currentCoord);
            }
        }

        private void EndGesture(Vector2 screenPos)
        {
            _isDragging = false;

            if (_isPlacingLair)
            {
                _isPlacingLair = false;
                _pendingPlacementAction = PlacementAction.None;
                if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
                {
                    _lairManager?.TryPlaceLair(_dragStartCoord, endCoord);
                }
                _lairManager?.ClearPlacementPreview();
                return;
            }

            if (_isPlacingTreasury)
            {
                _isPlacingTreasury = false;
                _pendingPlacementAction = PlacementAction.None;
                if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
                {
                    _treasuryManager?.TryPlaceTreasury(_dragStartCoord, endCoord);
                }
                _treasuryManager?.ClearPlacementPreview();
                return;
            }

            if (_isPlacingHatchery)
            {
                _isPlacingHatchery = false;
                _pendingPlacementAction = PlacementAction.None;
                if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
                {
                    _slimeHatcheryManager?.TryPlaceHatchery(_dragStartCoord, endCoord);
                }
                _slimeHatcheryManager?.ClearPlacementPreview();
                return;
            }

            if (_isPlacingBeacon)
            {
                _isPlacingBeacon = false;
                _pendingPlacementAction = PlacementAction.None;
                if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
                {
                    _baconBeaconManager?.TryPlaceBeacon(_dragStartCoord, endCoord);
                }
                _baconBeaconManager?.ClearPlacementPreview();
                return;
            }

            if (!_isViewTap)
            {
                return;
            }

            var dragDistance = Vector2.Distance(_dragStartScreenPos, screenPos);
            if (dragDistance > _tapMaxDragPixels)
            {
                return;
            }

            if (TryGetCoordUnderScreenPos(screenPos, out var coord))
            {
                Inspect(coord);
            }
        }

        /// Consumes _pendingPlacementAction against coord if one is armed —
        /// takes priority over BuildMode entirely, and turns into exactly
        /// one placement per tap. PlaceLair, PlaceTreasury, and SellLair
        /// aren't handled here — all three are drag gestures, resolved in
        /// BeginGesture/ContinueGesture/EndGesture instead.
        private bool TryExecutePendingPlacement(Vector2Int coord)
        {
            switch (_pendingPlacementAction)
            {
                case PlacementAction.SpawnImpling:
                    _implingSpawner?.SpawnImplingAt(coord);
                    _pendingPlacementAction = PlacementAction.None;
                    return true;
                case PlacementAction.ToggleLairClaim:
                    _lairManager?.ToggleLairClaim(coord);
                    _pendingPlacementAction = PlacementAction.None;
                    return true;
                default:
                    return false;
            }
        }

        /// Whether coord is a valid target for the current BuildMode's job
        /// kind, and if so whether it's already queued (so the caller knows
        /// whether this tap should queue or un-queue). Mine/Reinforce only
        /// ever target Rock; Construct only ever targets Floor — a tap on
        /// the wrong tile type for the active mode just does nothing, no
        /// fallback to a different action.
        private bool TryGetModeTarget(Vector2Int coord, out bool isAlreadyQueued)
        {
            var tile = _grid.GetTile(coord);
            switch (_buildMode)
            {
                case BuildMode.Mine:
                    isAlreadyQueued = tile.IsQueuedForDig;
                    return tile.Type == TileType.Rock;
                case BuildMode.Reinforce:
                    isAlreadyQueued = tile.IsQueuedForReinforce;
                    return tile.Type == TileType.Rock;
                case BuildMode.Construct:
                    isAlreadyQueued = tile.IsQueuedForBuild;
                    return tile.Type == TileType.Floor;
                default:
                    isAlreadyQueued = false;
                    return false;
            }
        }

        /// Populates InspectedDescription with whatever's at coord — an
        /// impling if one's standing there, otherwise the tile itself.
        private void Inspect(Vector2Int coord)
        {
            foreach (var impling in ImplingAgent.All)
            {
                if (_grid.WorldToGrid(impling.Position) == coord)
                {
                    _inspectedDescription = $"Impling #{impling.Id}\nState: {impling.State}\nPosition: ({coord.x},{coord.y})";
                    return;
                }
            }

            var tile = _grid.GetTile(coord);
            if (tile.Type == TileType.Rock)
            {
                var kind = tile.IsReinforced ? "Reinforced wall" : "Wall";
                var queued = tile.IsQueuedForDig ? " (queued: mine)" : tile.IsQueuedForReinforce ? " (queued: reinforce)" : "";
                _inspectedDescription = $"{kind} ({coord.x},{coord.y}){queued}\nHP: {tile.Hp}/{tile.MaxHp}";
            }
            else
            {
                var room = tile.HasRoom ? $"\nRoom: {tile.RoomId}" : "";
                var queued = tile.IsQueuedForBuild ? " (queued: construct wall)" : "";
                _inspectedDescription = $"Floor ({coord.x},{coord.y}){queued}\nOwnership: {tile.Ownership}{room}";
            }
        }

        private void ApplyLineSelectionStep(Vector2Int coord)
        {
            if (_hasLastPaintedCoord && coord == _lastPaintedCoord)
            {
                return;
            }

            _hasLastPaintedCoord = true;
            _lastPaintedCoord = coord;
            ApplyGestureAction(coord);
        }

        private void ApplySquareSelection(Vector2Int startCoord, Vector2Int currentCoord)
        {
            var minX = Mathf.Max(Mathf.Min(startCoord.x, currentCoord.x), 0);
            var maxX = Mathf.Min(Mathf.Max(startCoord.x, currentCoord.x), _grid.Width - 1);
            var minY = Mathf.Max(Mathf.Min(startCoord.y, currentCoord.y), 0);
            var maxY = Mathf.Min(Mathf.Max(startCoord.y, currentCoord.y), _grid.Height - 1);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    ApplyGestureAction(new Vector2Int(x, y));
                }
            }
        }

        /// Safe to call on any coord regardless of tile type or current
        /// state — RequestDig/RequestReinforce/RequestBuild/CancelJob/
        /// TrySellRoom already no-op on anything that isn't a valid target
        /// (TrySellRoom specifically on any tile with no room), so
        /// line/square painting never needs to filter candidates itself.
        private void ApplyGestureAction(Vector2Int coord)
        {
            switch (_gestureMode)
            {
                case GestureMode.Queue:
                    switch (_buildMode)
                    {
                        case BuildMode.Mine:
                            _grid.RequestDig(coord);
                            break;
                        case BuildMode.Reinforce:
                            _grid.RequestReinforce(coord);
                            break;
                        case BuildMode.Construct:
                            _grid.RequestBuild(coord);
                            break;
                    }
                    break;
                case GestureMode.Unqueue:
                    if (_jobBoard == null)
                    {
                        break;
                    }

                    switch (_buildMode)
                    {
                        case BuildMode.Mine:
                            if (_jobBoard.CancelJob(coord))
                            {
                                _grid.CancelDig(coord);
                            }
                            break;
                        case BuildMode.Reinforce:
                            if (_jobBoard.CancelReinforceJob(coord))
                            {
                                _grid.CancelReinforce(coord);
                            }
                            break;
                        case BuildMode.Construct:
                            if (_jobBoard.CancelBuildJob(coord))
                            {
                                _grid.CancelBuild(coord);
                            }
                            break;
                    }
                    break;
                case GestureMode.Sell:
                    _lairManager?.TrySellRoom(coord);
                    break;
            }
        }

        private bool IsSquareModeActive()
        {
            return _squareModeToggle
                || (Keyboard.current != null && (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed));
        }
    }
}

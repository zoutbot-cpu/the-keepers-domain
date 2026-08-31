using UnityEngine;
using UnityEngine.InputSystem;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Implings;
using KeepersDomain.Monsters;
using KeepersDomain.Creatures;
using KeepersDomain.UI;

namespace KeepersDomain.Input
{
    /// A one-shot tool armed from the UI (see BottomMenuBar) — the very next
    /// gesture executes it and clears back to None. For most of these that's
    /// a single tap; PlaceLair, PlaceTreasury, PlaceSlimeHatchery, and
    /// PlaceTavern are drags instead (see TileInteractionController.
    /// BeginGesture/EndGesture), sizing the room's footprint to whatever
    /// rectangle gets dragged out — each room manager owns its own
    /// placement/preview logic for its room kind, but they share the same
    /// underlying validity rule (DungeonGrid.CanBuildRoomOn), plus their own
    /// minimum-size floor for the Hatchery/Tavern. SellLair is also a drag — every room
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
        PlaceTavern,
        PlaceTrainingRoom,
        PlaceLibrary,
        PlaceJail,
        PlaceConversionClass,
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
        Construct,
        /// Arms the grab hand (see MinionGrabController) — taps stop
        /// mining/reinforcing/constructing entirely, same "inert to
        /// everything but its own tap handling" carve-out View mode gets.
        Grab,
        /// Line/square-paints a Bridge onto Water/Lava tiles (see
        /// BridgeManager) — instant and gold-charged per tile, unlike
        /// Mine/Reinforce/Construct's queue-a-job shape.
        Bridge,
        /// Dev-only terrain placement (see DungeonGrid.SetTerrainFeature) —
        /// paints a Rock tile directly into Water/Lava/Chasm/HolyGround,
        /// standing in for the map generator that doesn't exist yet.
        PlaceWater,
        PlaceLava,
        PlaceChasm,
        PlaceHolyGround,
        /// Dev-only wall placement (see DungeonGrid.SetBedrock) — marks a
        /// Rock tile permanently unminable.
        PlaceBedrock
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
            Sell,
            /// Instant, gold-charged bridge placement — see BuildMode.Bridge.
            BuildBridge,
            /// Instant dev-only terrain painting — see BuildMode.PlaceWater/
            /// PlaceLava/PlaceChasm.
            PlaceTerrain
        }

        [SerializeField] private float _tapMaxDragPixels = 12f;

        private Camera _camera;
        private DungeonGrid _grid;
        private BuilderJobBoard _jobBoard;
        private LairManager _lairManager;
        private TreasuryManager _treasuryManager;
        private SlimeHatcheryManager _slimeHatcheryManager;
        private TavernManager _tavernManager;
        private TrainingRoomManager _trainingRoomManager;
        private LibraryManager _libraryManager;
        private JailManager _jailManager;
        private ConversionClassManager _conversionClassManager;
        private BridgeManager _bridgeManager;
        private ImplingSpawner _implingSpawner;
        private MinionGrabController _minionGrabController;

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

        // A Bridge gesture locks onto whichever axis (horizontal/vertical)
        // the drag first moves toward from _dragStartCoord, then every
        // further tile is projected onto that same axis regardless of how
        // the pointer actually wanders — see ContinueGesture's BuildBridge
        // branch. Keeps a bridge a straight line even if the player's drag
        // wobbles, rather than following the raw (possibly jagged) mouse
        // path the way ordinary line painting does.
        private bool _hasBridgeAxis;
        private bool _isBridgeAxisHorizontal;
        private bool _isViewTap;
        private bool _isGrabTap;
        private bool _isPlacingLair;
        private Vector2Int _lairDragCurrentCoord;
        private bool _isPlacingTreasury;
        private Vector2Int _treasuryDragCurrentCoord;
        private bool _isPlacingHatchery;
        private Vector2Int _hatcheryDragCurrentCoord;
        private bool _isPlacingTavern;
        private Vector2Int _tavernDragCurrentCoord;
        private bool _isPlacingTrainingRoom;
        private Vector2Int _trainingRoomDragCurrentCoord;
        private bool _isPlacingLibrary;
        private Vector2Int _libraryDragCurrentCoord;
        private bool _isPlacingJail;
        private Vector2Int _jailDragCurrentCoord;
        private bool _isPlacingConversionClass;
        private Vector2Int _conversionClassDragCurrentCoord;
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

        /// Same idea again, for a Tavern placement in progress.
        public bool IsPlacingTavern => _isPlacingTavern;
        public Vector2Int TavernDragStartCoord => _dragStartCoord;
        public Vector2Int TavernDragCurrentCoord => _tavernDragCurrentCoord;

        /// Same idea again, for a Training Room placement in progress.
        public bool IsPlacingTrainingRoom => _isPlacingTrainingRoom;
        public Vector2Int TrainingRoomDragStartCoord => _dragStartCoord;
        public Vector2Int TrainingRoomDragCurrentCoord => _trainingRoomDragCurrentCoord;

        /// Same idea again, for a Library placement in progress.
        public bool IsPlacingLibrary => _isPlacingLibrary;
        public Vector2Int LibraryDragStartCoord => _dragStartCoord;
        public Vector2Int LibraryDragCurrentCoord => _libraryDragCurrentCoord;

        /// Same idea again, for a Jail placement in progress.
        public bool IsPlacingJail => _isPlacingJail;
        public Vector2Int JailDragStartCoord => _dragStartCoord;
        public Vector2Int JailDragCurrentCoord => _jailDragCurrentCoord;

        /// Same idea again, for a Conversion Class placement in progress.
        public bool IsPlacingConversionClass => _isPlacingConversionClass;
        public Vector2Int ConversionClassDragStartCoord => _dragStartCoord;
        public Vector2Int ConversionClassDragCurrentCoord => _conversionClassDragCurrentCoord;

        public PlacementAction PendingPlacementAction => _pendingPlacementAction;
        public BuildMode BuildMode => _buildMode;
        public string InspectedDescription => _inspectedDescription;

        /// Whether the Grab hand is currently carrying a minion — read by
        /// BottomMenuBar to show the right instruction text for Grab mode.
        public bool IsCarryingMinion => _minionGrabController != null && _minionGrabController.IsCarrying;

        /// The grid coordinate currently under the pointer, if any — read
        /// by BottomMenuBar to draw a small (x, y) readout next to the
        /// cursor for troubleshooting. Updated every frame regardless of
        /// BuildMode/PendingPlacementAction.
        public Vector2Int? HoveredCoord { get; private set; }

        public void Initialize(Camera camera, DungeonGrid grid, BuilderJobBoard jobBoard, LairManager lairManager, TreasuryManager treasuryManager, SlimeHatcheryManager slimeHatcheryManager, TavernManager tavernManager, TrainingRoomManager trainingRoomManager, LibraryManager libraryManager, JailManager jailManager, ConversionClassManager conversionClassManager, BridgeManager bridgeManager, ImplingSpawner implingSpawner, MinionGrabController minionGrabController)
        {
            _camera = camera;
            _grid = grid;
            _jobBoard = jobBoard;
            _lairManager = lairManager;
            _treasuryManager = treasuryManager;
            _slimeHatcheryManager = slimeHatcheryManager;
            _tavernManager = tavernManager;
            _trainingRoomManager = trainingRoomManager;
            _libraryManager = libraryManager;
            _jailManager = jailManager;
            _conversionClassManager = conversionClassManager;
            _bridgeManager = bridgeManager;
            _implingSpawner = implingSpawner;
            _minionGrabController = minionGrabController;
        }

        public void SetSquareModeToggle(bool isEnabled)
        {
            _squareModeToggle = isEnabled;
        }

        public void SetBuildMode(BuildMode mode)
        {
            // Leaving Grab mode mid-carry drops the minion straight back
            // where it was grabbed from (see MinionGrabController.
            // CancelCarry) rather than leaving it stuck floating forever
            // just because the player switched tools.
            if (_buildMode == BuildMode.Grab && mode != BuildMode.Grab)
            {
                _minionGrabController?.CancelCarry();
            }

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
                _minionGrabController?.SetVisible(false);
                HoveredCoord = null;
                return;
            }

            HoveredCoord = TryGetCoordUnderScreenPos(PointerInput.PrimaryPosition, out var hoveredCoord) ? hoveredCoord : (Vector2Int?)null;

            UpdateSellPreview();

            var isGrabModeActive = _buildMode == BuildMode.Grab;
            _minionGrabController?.SetVisible(isGrabModeActive);
            if (isGrabModeActive)
            {
                _minionGrabController?.UpdateHover(PointerInput.PrimaryPosition);
            }

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
            _isGrabTap = false;

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

            if (_pendingPlacementAction == PlacementAction.PlaceTavern)
            {
                _isPlacingTavern = true;
                _tavernDragCurrentCoord = coord;
                _tavernManager?.UpdatePlacementPreview(coord, coord);
                return;
            }

            if (_pendingPlacementAction == PlacementAction.PlaceTrainingRoom)
            {
                _isPlacingTrainingRoom = true;
                _trainingRoomDragCurrentCoord = coord;
                _trainingRoomManager?.UpdatePlacementPreview(coord, coord);
                return;
            }

            if (_pendingPlacementAction == PlacementAction.PlaceLibrary)
            {
                _isPlacingLibrary = true;
                _libraryDragCurrentCoord = coord;
                _libraryManager?.UpdatePlacementPreview(coord, coord);
                return;
            }

            if (_pendingPlacementAction == PlacementAction.PlaceJail)
            {
                _isPlacingJail = true;
                _jailDragCurrentCoord = coord;
                _jailManager?.UpdatePlacementPreview(coord, coord);
                return;
            }

            if (_pendingPlacementAction == PlacementAction.PlaceConversionClass)
            {
                _isPlacingConversionClass = true;
                _conversionClassDragCurrentCoord = coord;
                _conversionClassManager?.UpdatePlacementPreview(coord, coord);
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

            if (_buildMode == BuildMode.Grab)
            {
                // Same tap-only, resolved-on-release shape as View mode —
                // grabbing/dropping mid-drag doesn't make sense either,
                // and this keeps Grab mode from ever queuing a dig/
                // reinforce/build job even if the player drags.
                _isGrabTap = true;
                return;
            }

            if (_buildMode == BuildMode.Bridge)
            {
                // Instant, gold-charged per tile — same one-tap-then-line-
                // continues shape GestureMode.Sell already uses, rather
                // than Mine/Reinforce/Construct's queue/unqueue toggle. The
                // axis isn't known yet — see ContinueGesture's BuildBridge
                // branch, which locks it in once the drag actually moves.
                _gestureMode = GestureMode.BuildBridge;
                _hasBridgeAxis = false;
                ApplyGestureAction(coord);
                _hasLastPaintedCoord = true;
                _lastPaintedCoord = coord;
                return;
            }

            if (_buildMode is BuildMode.PlaceWater or BuildMode.PlaceLava or BuildMode.PlaceChasm or BuildMode.PlaceHolyGround or BuildMode.PlaceBedrock)
            {
                _gestureMode = GestureMode.PlaceTerrain;
                ApplyGestureAction(coord);
                _hasLastPaintedCoord = true;
                _lastPaintedCoord = coord;
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

            if (_isPlacingTavern)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var tavernCoord) && tavernCoord != _tavernDragCurrentCoord)
                {
                    _tavernDragCurrentCoord = tavernCoord;
                    _tavernManager?.UpdatePlacementPreview(_dragStartCoord, _tavernDragCurrentCoord);
                }
                return;
            }

            if (_isPlacingTrainingRoom)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var trainingRoomCoord) && trainingRoomCoord != _trainingRoomDragCurrentCoord)
                {
                    _trainingRoomDragCurrentCoord = trainingRoomCoord;
                    _trainingRoomManager?.UpdatePlacementPreview(_dragStartCoord, _trainingRoomDragCurrentCoord);
                }
                return;
            }

            if (_isPlacingLibrary)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var libraryCoord) && libraryCoord != _libraryDragCurrentCoord)
                {
                    _libraryDragCurrentCoord = libraryCoord;
                    _libraryManager?.UpdatePlacementPreview(_dragStartCoord, _libraryDragCurrentCoord);
                }
                return;
            }

            if (_isPlacingJail)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var jailCoord) && jailCoord != _jailDragCurrentCoord)
                {
                    _jailDragCurrentCoord = jailCoord;
                    _jailManager?.UpdatePlacementPreview(_dragStartCoord, _jailDragCurrentCoord);
                }
                return;
            }

            if (_isPlacingConversionClass)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var conversionClassCoord) && conversionClassCoord != _conversionClassDragCurrentCoord)
                {
                    _conversionClassDragCurrentCoord = conversionClassCoord;
                    _conversionClassManager?.UpdatePlacementPreview(_dragStartCoord, _conversionClassDragCurrentCoord);
                }
                return;
            }

            if (_gestureMode == GestureMode.None || !TryGetCoordUnderScreenPos(screenPos, out var currentCoord))
            {
                return;
            }

            if (_gestureMode == GestureMode.BuildBridge)
            {
                // Never square-fills (a bridge is 1 tile wide) and never
                // follows the raw pointer path either — locks onto whichever
                // axis the drag first moves toward from _dragStartCoord, then
                // every further tile is that axis's projection of the
                // pointer, so a wobbly drag still comes out as a straight
                // line instead of a jagged one.
                if (!_hasBridgeAxis)
                {
                    var dx = currentCoord.x - _dragStartCoord.x;
                    var dy = currentCoord.y - _dragStartCoord.y;
                    if (dx == 0 && dy == 0)
                    {
                        return;
                    }

                    _isBridgeAxisHorizontal = Mathf.Abs(dx) >= Mathf.Abs(dy);
                    _hasBridgeAxis = true;
                }

                var projectedCoord = _isBridgeAxisHorizontal
                    ? new Vector2Int(currentCoord.x, _dragStartCoord.y)
                    : new Vector2Int(_dragStartCoord.x, currentCoord.y);
                ApplyLineSelectionStep(projectedCoord);
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

            if (_isPlacingTavern)
            {
                _isPlacingTavern = false;
                _pendingPlacementAction = PlacementAction.None;
                if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
                {
                    _tavernManager?.TryPlaceTavern(_dragStartCoord, endCoord);
                }
                _tavernManager?.ClearPlacementPreview();
                return;
            }

            if (_isPlacingTrainingRoom)
            {
                _isPlacingTrainingRoom = false;
                _pendingPlacementAction = PlacementAction.None;
                if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
                {
                    _trainingRoomManager?.TryPlaceTrainingRoom(_dragStartCoord, endCoord);
                }
                _trainingRoomManager?.ClearPlacementPreview();
                return;
            }

            if (_isPlacingLibrary)
            {
                _isPlacingLibrary = false;
                _pendingPlacementAction = PlacementAction.None;
                if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
                {
                    _libraryManager?.TryPlaceLibrary(_dragStartCoord, endCoord);
                }
                _libraryManager?.ClearPlacementPreview();
                return;
            }

            if (_isPlacingJail)
            {
                _isPlacingJail = false;
                _pendingPlacementAction = PlacementAction.None;
                if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
                {
                    _jailManager?.TryPlaceJail(_dragStartCoord, endCoord);
                }
                _jailManager?.ClearPlacementPreview();
                return;
            }

            if (_isPlacingConversionClass)
            {
                _isPlacingConversionClass = false;
                _pendingPlacementAction = PlacementAction.None;
                if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
                {
                    _conversionClassManager?.TryPlaceConversionClass(_dragStartCoord, endCoord);
                }
                _conversionClassManager?.ClearPlacementPreview();
                return;
            }

            if (_isGrabTap)
            {
                _isGrabTap = false;
                var grabDragDistance = Vector2.Distance(_dragStartScreenPos, screenPos);
                if (grabDragDistance <= _tapMaxDragPixels && TryGetCoordUnderScreenPos(screenPos, out var grabCoord))
                {
                    _minionGrabController?.HandleTap(grabCoord);
                }
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
        /// Also drives the wall selection outline (see DungeonGrid.
        /// SetSelectedWall): cleared up front so every non-wall result
        /// below (a creature, a non-Rock tile) leaves it cleared, and only
        /// the Rock-tile branches re-select.
        private void Inspect(Vector2Int coord)
        {
            _grid.SetSelectedWall(null);

            foreach (var impling in ImplingAgent.All)
            {
                if (_grid.WorldToGrid(impling.Position) == coord)
                {
                    _inspectedDescription = $"{impling.Name}\n{impling.State} — Position: ({coord.x},{coord.y})\n"
                        + $"{impling.Creature.DescribeStats()}\n"
                        + $"Carrying — Gold: {impling.Inventory.Gold}  Mana Crystals: {impling.Inventory.ManaCrystals}  Slimes: {impling.Inventory.Slimes}";
                    return;
                }
            }

            foreach (var gremlin in GremlinAgent.All)
            {
                if (_grid.WorldToGrid(gremlin.Position) == coord)
                {
                    _inspectedDescription = DescribeMonster(gremlin.Name, gremlin.Task.ToString(), coord, gremlin.Creature, gremlin.Hunger, gremlin.Pay, gremlin.Happiness);
                    return;
                }
            }

            foreach (var warlock in WarlockAgent.All)
            {
                if (_grid.WorldToGrid(warlock.Position) == coord)
                {
                    _inspectedDescription = DescribeMonster(warlock.Name, warlock.Task.ToString(), coord, warlock.Creature, warlock.Hunger, warlock.Pay, warlock.Happiness);
                    return;
                }
            }

            foreach (var mazeRattler in MazeRattlerAgent.All)
            {
                if (_grid.WorldToGrid(mazeRattler.Position) == coord)
                {
                    _inspectedDescription = DescribeMonster(mazeRattler.Name, mazeRattler.Task.ToString(), coord, mazeRattler.Creature, mazeRattler.Hunger, mazeRattler.Pay, mazeRattler.Happiness);
                    return;
                }
            }

            foreach (var beanCounter in BeanCounterAgent.All)
            {
                if (_grid.WorldToGrid(beanCounter.Position) == coord)
                {
                    _inspectedDescription = DescribeMonster(beanCounter.Name, beanCounter.Task.ToString(), coord, beanCounter.Creature, beanCounter.Hunger, beanCounter.Pay, beanCounter.Happiness);
                    return;
                }
            }

            foreach (var elf in ElfAgent.All)
            {
                if (_grid.WorldToGrid(elf.Position) == coord)
                {
                    _inspectedDescription = DescribeMonster(elf.Name, elf.Task.ToString(), coord, elf.Creature, elf.Hunger, elf.Pay, elf.Happiness);
                    return;
                }
            }

            var tile = _grid.GetTile(coord);
            if (tile.Type == TileType.Rock && tile.IsBedrock)
            {
                _grid.SetSelectedWall(coord);
                _inspectedDescription = $"Bedrock ({coord.x},{coord.y})\nUnminable";
            }
            else if (tile.Type == TileType.Rock)
            {
                _grid.SetSelectedWall(coord);
                var kind = tile.IsReinforced ? "Reinforced wall" : "Wall";
                var queued = tile.IsQueuedForDig ? " (queued: mine)" : tile.IsQueuedForReinforce ? " (queued: reinforce)" : "";
                _inspectedDescription = $"{kind} ({coord.x},{coord.y}){queued}\nHP: {tile.Hp}/{tile.MaxHp}";
            }
            else if (tile.Type is TileType.Water or TileType.Lava or TileType.Chasm)
            {
                var bridged = tile.HasRoom ? "Bridged" : "Not bridged";
                _inspectedDescription = tile.Type == TileType.Chasm
                    ? $"Chasm ({coord.x},{coord.y})\nImpassable — no bridge can be built here"
                    : $"{tile.Type} ({coord.x},{coord.y})\n{bridged}";
            }
            else if (tile.Type == TileType.HolyGround)
            {
                _inspectedDescription = $"Holy Ground ({coord.x},{coord.y})\nUnclaimable";
            }
            else
            {
                var room = tile.HasRoom ? $"\nRoom: {tile.RoomId}" : "";
                var queued = tile.IsQueuedForBuild ? " (queued: construct wall)" : "";
                _inspectedDescription = $"Floor ({coord.x},{coord.y}){queued}\nOwnership: {tile.Ownership}{room}";
            }
        }

        /// Shared by Gremlin/Warlock inspection — both expose the same
        /// shape (Creature/Hunger/Pay/Happiness) despite not sharing a base
        /// class, so this just takes them as separate arguments rather than
        /// duplicating the formatting per creature type.
        private static string DescribeMonster(string name, string task, Vector2Int coord, Creature creature, Hunger hunger, Pay pay, Happiness happiness)
        {
            var hungryTag = hunger.IsHungry ? " (hungry)" : "";
            var unpaidTag = pay.IsUnhappy ? " (unpaid!)" : "";
            return $"{name}\n{task} — Position: ({coord.x},{coord.y})\n"
                + $"{creature.DescribeStats()}\n"
                + $"Hunger: {hunger.Value:0}{hungryTag}\n"
                + $"Wage: {Pay.WageFor(creature.Level)}g/10min{unpaidTag}\n"
                + $"Happiness: {happiness.Value:0} ({happiness.Tier})";
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
                            _grid.SetSelectedWall(coord);
                            break;
                        case BuildMode.Reinforce:
                            _grid.RequestReinforce(coord);
                            _grid.SetSelectedWall(coord);
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
                            _grid.SetSelectedWall(coord);
                            break;
                        case BuildMode.Reinforce:
                            if (_jobBoard.CancelReinforceJob(coord))
                            {
                                _grid.CancelReinforce(coord);
                            }
                            _grid.SetSelectedWall(coord);
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
                case GestureMode.BuildBridge:
                    _bridgeManager?.TryPlaceBridgeTile(coord);
                    break;
                case GestureMode.PlaceTerrain:
                    switch (_buildMode)
                    {
                        case BuildMode.PlaceWater:
                            _grid.SetTerrainFeature(coord, TileType.Water);
                            break;
                        case BuildMode.PlaceLava:
                            _grid.SetTerrainFeature(coord, TileType.Lava);
                            break;
                        case BuildMode.PlaceChasm:
                            _grid.SetTerrainFeature(coord, TileType.Chasm);
                            break;
                        case BuildMode.PlaceHolyGround:
                            _grid.SetTerrainFeature(coord, TileType.HolyGround);
                            break;
                        case BuildMode.PlaceBedrock:
                            _grid.SetBedrock(coord);
                            break;
                    }
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

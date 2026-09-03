using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Rooms;
using KeepersDomain.UI;

namespace KeepersDomain.Input
{
    /// Every wall/terrain/floor tool the level designer's Map Design menu
    /// offers. Wall variants map straight to DungeonGrid.EditorPaintWall's
    /// own EditorWallVariant; UnclaimedFloor/ClaimedFloor map to
    /// EditorPaintFloor. ClaimedFloor is the only one that reads the
    /// owner selector (see LevelDesignerMenuBar) — every other tool
    /// ignores it.
    public enum MapDesignTool
    {
        None,
        PlainWall,
        ReinforcedWall,
        GoldWall,
        RegeneratingGoldWall,
        ManaCrystalWall,
        Bedrock,
        Water,
        Lava,
        Chasm,
        HolyGround,
        UnclaimedFloor,
        ClaimedFloor
    }

    /// Every room a `{RoomDesignTool}_{index}` roomId prefix can resolve to
    /// (see RoomReconstruction.ResolveRoomManager) — the 8 rectangular room
    /// types the level designer's Rooms menu stamps down, plus Bridge.
    /// Bridge has no Rooms-menu button (it's a painted line over Water/Lava,
    /// not a rectangular drag) and can't be authored in the level designer
    /// yet — it's only here so a *saved* bridge tile reconstructs through
    /// the same IRestorableRoomManager path every other room uses.
    public enum RoomDesignTool
    {
        None,
        Lair,
        Treasury,
        SlimeHatchery,
        Tavern,
        TrainingRoom,
        Library,
        Jail,
        ConversionClass,
        Bridge
    }

    /// What Edit mode's last tap actually selected — read by
    /// LevelDesignerMenuBar to decide what to show/label in the Edit
    /// panel, and by LevelDesignerInteractionController.ReassignSelectedOwner
    /// to know which underlying data to mutate.
    public enum EditSelectionKind
    {
        None,
        Tile,
        Room,
        Structure,
        Creature
    }

    /// Turns raw pointer input into level-authoring actions — the Level
    /// Designer's own equivalent of TileInteractionController, but every
    /// action applies instantly and unconditionally (see DungeonGrid's
    /// Editor* methods) rather than queuing a job or charging gold, since
    /// this is authoring finished level state, not simulating play. Map
    /// design tools line-paint while dragging, the same feel gameplay's
    /// Mine mode has; room tools drag out a rectangle, the same feel
    /// gameplay's PlaceLair has; creature tools place one at a time on tap.
    public class LevelDesignerInteractionController : MonoBehaviour
    {
        // Floats well above the tallest possible tile (a full-height Rock
        // cube) so the divider line stays visible over any terrain rather
        // than getting buried under it.
        private const float MirrorLineHeight = 1.3f;
        private const float MirrorLineThickness = 0.08f;
        private static readonly Color MirrorXLineColor = new Color(0.2f, 0.9f, 0.3f);
        private static readonly Color MirrorYLineColor = new Color(0.9f, 0.2f, 0.2f);

        private Camera _camera;
        private DungeonGrid _grid;
        private LevelDesignerSession _session;
        private Dictionary<RoomDesignTool, IRestorableRoomManager> _roomManagers;
        // Pulled out of _roomManagers in Initialize — the Remove tool sells
        // rooms through the same LairManager.TrySellRoom gameplay's Sell
        // tool uses (fires RoomSold, so every room manager tears down its
        // own decoration), then the editor resets the footprint to Rock.
        private LairManager _lairManager;

        private MapDesignTool _mapDesignTool = MapDesignTool.None;
        private RoomDesignTool _roomTool = RoomDesignTool.None;
        private StructureKind? _structureTool;
        private EditorCreatureKind? _creatureTool;
        private bool _editMode;
        private bool _removeMode;
        private int _selectedOwnerId = -1;
        private int _nextRoomId;

        // Edit mode's current tap selection — see EditSelectionKind's own
        // header. Only the fields relevant to _selectionKind are
        // meaningful at any given time; the rest just sit at their
        // cleared/default value.
        private EditSelectionKind _selectionKind = EditSelectionKind.None;
        private Vector2Int _selectedCoord;
        private string _selectedRoomId;
        private int _selectedStructureIndex = -1;
        private int _selectedCreatureIndex = -1;
        private int _selectedCurrentOwnerId = -1;

        private bool _mirrorX;
        private bool _mirrorY;
        private GameObject _mirrorXLine;
        private GameObject _mirrorYLine;

        private bool _isDragging;
        private Vector2Int _dragStartCoord;
        private bool _isDraggingRoom;
        private Vector2Int _roomDragCurrentCoord;
        private Vector2Int _lastPaintedCoord;
        private bool _hasLastPaintedCoord;

        public MapDesignTool MapDesignTool => _mapDesignTool;
        public RoomDesignTool RoomTool => _roomTool;
        public StructureKind? StructureTool => _structureTool;
        public EditorCreatureKind? CreatureTool => _creatureTool;
        public bool EditMode => _editMode;
        public bool RemoveMode => _removeMode;
        public bool MirrorX => _mirrorX;
        public bool MirrorY => _mirrorY;

        /// What Edit mode's last tap selected, if anything — read by
        /// LevelDesignerMenuBar to draw the Edit panel's "currently
        /// selected: ..." readout and owner-reassign control.
        public EditSelectionKind SelectionKind => _selectionKind;
        public Vector2Int? SelectedCoord => _selectionKind == EditSelectionKind.None ? (Vector2Int?)null : _selectedCoord;
        public int SelectedCurrentOwnerId => _selectedCurrentOwnerId;

        /// The grid coordinate currently under the pointer, if any — read
        /// by LevelDesignerMenuBar to draw a small (x, y) readout next to
        /// the cursor for troubleshooting. Updated every frame regardless
        /// of which tool (if any) is active.
        public Vector2Int? HoveredCoord { get; private set; }

        /// While a room placement is being dragged out, the rectangle so
        /// far — read by LevelDesignerMenuBar to show the player what
        /// they're about to place, same idea as TileInteractionController's
        /// own IsPlacingLair/LairDragCurrentCoord.
        public bool IsPlacingRoom => _isDraggingRoom;
        public Vector2Int RoomDragStartCoord => _dragStartCoord;
        public Vector2Int RoomDragCurrentCoord => _roomDragCurrentCoord;

        public void Initialize(Camera camera, DungeonGrid grid, LevelDesignerSession session, Dictionary<RoomDesignTool, IRestorableRoomManager> roomManagers)
        {
            _camera = camera;
            _grid = grid;
            _session = session;
            _roomManagers = roomManagers;
            _lairManager = roomManagers != null && roomManagers.TryGetValue(RoomDesignTool.Lair, out var lair)
                ? lair as LairManager
                : null;
        }

        public void SetMapDesignTool(MapDesignTool tool)
        {
            _mapDesignTool = tool;
            _roomTool = RoomDesignTool.None;
            _structureTool = null;
            _creatureTool = null;
            _removeMode = false;
            SetEditMode(false);
        }

        public void SetRoomTool(RoomDesignTool tool)
        {
            _roomTool = tool;
            _mapDesignTool = MapDesignTool.None;
            _structureTool = null;
            _creatureTool = null;
            _removeMode = false;
            SetEditMode(false);
        }

        public void SetStructureTool(StructureKind? kind)
        {
            _structureTool = kind;
            _mapDesignTool = MapDesignTool.None;
            _roomTool = RoomDesignTool.None;
            _creatureTool = null;
            _removeMode = false;
            SetEditMode(false);
        }

        public void SetCreatureTool(EditorCreatureKind? kind)
        {
            _creatureTool = kind;
            _mapDesignTool = MapDesignTool.None;
            _roomTool = RoomDesignTool.None;
            _structureTool = null;
            _removeMode = false;
            SetEditMode(false);
        }

        /// A 5th tool category, mutually exclusive with the other four
        /// same as they already are with each other — lets the author tap
        /// an already-placed tile/wall/room/structure/creature and
        /// reassign which player it belongs to (see SelectAt/
        /// ReassignSelectedOwner), instead of only ever painting/placing
        /// new things.
        public void SetEditMode(bool enabled)
        {
            _editMode = enabled;
            if (enabled)
            {
                _mapDesignTool = MapDesignTool.None;
                _roomTool = RoomDesignTool.None;
                _structureTool = null;
                _creatureTool = null;
                _removeMode = false;
            }
            else
            {
                ClearSelection();
            }
        }

        /// A 6th tool category, mutually exclusive with the other five —
        /// tap an already-placed wall/terrain/floor tile, room, structure,
        /// or creature to delete it (see RemoveAt). Rooms and structures
        /// take their whole footprint back to plain Rock; a lone tile just
        /// resets. No mirroring (same reasoning SelectAt gives) and no
        /// undo, same as every other destructive editor action.
        public void SetRemoveMode(bool enabled)
        {
            _removeMode = enabled;
            if (enabled)
            {
                _mapDesignTool = MapDesignTool.None;
                _roomTool = RoomDesignTool.None;
                _structureTool = null;
                _creatureTool = null;
                SetEditMode(false);
            }
        }

        public void SetSelectedOwner(int ownerId)
        {
            _selectedOwnerId = ownerId;
        }

        /// Toggles the vertical (green) divider that splits the map into
        /// left/right halves — every map-design/room/structure/creature
        /// edit from here on also gets applied to the coordinate reflected
        /// across it (see GetMirroredCoords/GetMirroredRects), until this
        /// is toggled off again.
        public void SetMirrorX(bool enabled)
        {
            _mirrorX = enabled;
            RefreshMirrorLine(ref _mirrorXLine, enabled, isVertical: true);
        }

        /// Same idea as SetMirrorX, for the horizontal (red) divider that
        /// splits the map into top/bottom halves instead.
        public void SetMirrorY(bool enabled)
        {
            _mirrorY = enabled;
            RefreshMirrorLine(ref _mirrorYLine, enabled, isVertical: false);
        }

        /// Builds (once) or just shows/hides the line marking where
        /// Mirror X/Y currently splits the map — a thin stretched cube,
        /// same "cheap primitives-only decoration" convention every other
        /// visual in this prototype uses (see DungeonGrid's own gold
        /// nuggets/chasm spikes/holy ground star).
        private void RefreshMirrorLine(ref GameObject line, bool visible, bool isVertical)
        {
            if (!visible)
            {
                if (line != null)
                {
                    line.SetActive(false);
                }
                return;
            }

            if (line == null)
            {
                line = GameObject.CreatePrimitive(PrimitiveType.Cube);
                line.name = isVertical ? "MirrorXLine" : "MirrorYLine";
                line.transform.SetParent(transform, false);
                Destroy(line.GetComponent<Collider>());
                line.GetComponent<Renderer>().material.color = isVertical ? MirrorXLineColor : MirrorYLineColor;

                var mapWidthWorld = _grid.Width * _grid.CellSize;
                var mapHeightWorld = _grid.Height * _grid.CellSize;

                if (isVertical)
                {
                    // Runs along Z at the map's horizontal midpoint —
                    // mirrors x-coordinates left/right of it.
                    var lineX = _grid.Width * 0.5f * _grid.CellSize;
                    line.transform.position = new Vector3(lineX, MirrorLineHeight, mapHeightWorld * 0.5f);
                    line.transform.localScale = new Vector3(MirrorLineThickness, MirrorLineThickness, mapHeightWorld);
                }
                else
                {
                    // Runs along X at the map's vertical midpoint —
                    // mirrors y-coordinates above/below it.
                    var lineZ = _grid.Height * 0.5f * _grid.CellSize;
                    line.transform.position = new Vector3(mapWidthWorld * 0.5f, MirrorLineHeight, lineZ);
                    line.transform.localScale = new Vector3(mapWidthWorld, MirrorLineThickness, MirrorLineThickness);
                }
            }

            line.SetActive(true);
        }

        private Vector2Int MirrorXCoord(Vector2Int coord)
        {
            return new Vector2Int(_grid.Width - 1 - coord.x, coord.y);
        }

        private Vector2Int MirrorYCoord(Vector2Int coord)
        {
            return new Vector2Int(coord.x, _grid.Height - 1 - coord.y);
        }

        /// Every coordinate the current mirror toggles imply also applying
        /// this edit to, including coord itself — up to 4 total when both
        /// Mirror X and Mirror Y are on (the 4th being both reflections
        /// combined, for 4-way symmetry). Deduplicated so a coord sitting
        /// exactly on a mirror line (only possible on an odd map
        /// dimension) never gets the same edit applied to it twice.
        private List<Vector2Int> GetMirroredCoords(Vector2Int coord)
        {
            var results = new List<Vector2Int> { coord };
            if (_mirrorX)
            {
                AddIfNew(results, MirrorXCoord(coord));
            }
            if (_mirrorY)
            {
                AddIfNew(results, MirrorYCoord(coord));
            }
            if (_mirrorX && _mirrorY)
            {
                AddIfNew(results, MirrorXCoord(MirrorYCoord(coord)));
            }
            return results;
        }

        private static void AddIfNew(List<Vector2Int> list, Vector2Int coord)
        {
            if (!list.Contains(coord))
            {
                list.Add(coord);
            }
        }

        /// Same idea as GetMirroredCoords, but for a room's drag rectangle
        /// (its two corners) rather than a single tap coordinate — used by
        /// PlaceRoomFootprint.
        private List<(Vector2Int start, Vector2Int end)> GetMirroredRects(Vector2Int start, Vector2Int end)
        {
            var rects = new List<(Vector2Int, Vector2Int)> { (start, end) };
            if (_mirrorX)
            {
                rects.Add((MirrorXCoord(start), MirrorXCoord(end)));
            }
            if (_mirrorY)
            {
                rects.Add((MirrorYCoord(start), MirrorYCoord(end)));
            }
            if (_mirrorX && _mirrorY)
            {
                rects.Add((MirrorXCoord(MirrorYCoord(start)), MirrorXCoord(MirrorYCoord(end))));
            }
            return rects;
        }

        private void Update()
        {
            if (_camera == null || _grid == null || LevelDesignerMenuBar.PointerOverPanel)
            {
                HoveredCoord = null;
                return;
            }

            HoveredCoord = TryGetCoordUnderScreenPos(PointerInput.PrimaryPosition, out var hoveredCoord) ? hoveredCoord : (Vector2Int?)null;

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
            _hasLastPaintedCoord = false;

            if (!TryGetCoordUnderScreenPos(screenPos, out var coord))
            {
                return;
            }

            _dragStartCoord = coord;

            if (_editMode)
            {
                // Tap-to-select, not a drag — SelectAt is single-tap
                // semantics same as Structure/Creature placement below,
                // so this returns immediately rather than tracking a
                // gesture. No mirroring: reassigning a mirrored copy too
                // would silently reassign an unrelated, possibly-
                // differently-owned tile the author didn't tap.
                SelectAt(coord);
                return;
            }

            if (_removeMode)
            {
                // Single tap, not a drag — same as Edit's SelectAt above,
                // and unmirrored for the same reason (a reflected tap could
                // silently delete something unrelated the author wants).
                RemoveAt(coord);
                return;
            }

            if (_roomTool != RoomDesignTool.None)
            {
                _isDraggingRoom = true;
                _roomDragCurrentCoord = coord;
                return;
            }

            if (_structureTool.HasValue)
            {
                // Single placement, fixed footprint — unlike the drag-sized
                // room tools above, a Core/Portal Room is always the same
                // size (see LevelDesignerSession.PlaceStructure), so there's
                // nothing to drag out. Mirrored the same way every other
                // tool is (see GetMirroredCoords) — each reflected tap
                // places its own full structure.
                foreach (var mirroredCoord in GetMirroredCoords(coord))
                {
                    _session.PlaceStructure(_structureTool.Value, mirroredCoord, _selectedOwnerId);
                }
                return;
            }

            if (_creatureTool.HasValue)
            {
                // Single placement, not drag-paint — otherwise a slow drag
                // would spawn a trail of creatures instead of just one.
                foreach (var mirroredCoord in GetMirroredCoords(coord))
                {
                    _session.PlaceCreature(_creatureTool.Value, mirroredCoord, _selectedOwnerId);
                }
                return;
            }

            if (_mapDesignTool != MapDesignTool.None)
            {
                ApplyMapDesignTool(coord);
                _hasLastPaintedCoord = true;
                _lastPaintedCoord = coord;
            }
        }

        private void ContinueGesture(Vector2 screenPos)
        {
            if (_isDraggingRoom)
            {
                if (TryGetCoordUnderScreenPos(screenPos, out var roomCoord) && roomCoord != _roomDragCurrentCoord)
                {
                    _roomDragCurrentCoord = roomCoord;
                }
                return;
            }

            if (_mapDesignTool == MapDesignTool.None || !TryGetCoordUnderScreenPos(screenPos, out var coord))
            {
                return;
            }

            if (_hasLastPaintedCoord && coord == _lastPaintedCoord)
            {
                return;
            }

            _hasLastPaintedCoord = true;
            _lastPaintedCoord = coord;
            ApplyMapDesignTool(coord);
        }

        private void EndGesture(Vector2 screenPos)
        {
            _isDragging = false;

            if (!_isDraggingRoom)
            {
                return;
            }

            _isDraggingRoom = false;
            if (TryGetCoordUnderScreenPos(screenPos, out var endCoord))
            {
                PlaceRoomFootprint(_dragStartCoord, endCoord);
            }
        }

        /// Places one room per mirrored copy of the dragged rectangle (see
        /// GetMirroredRects) through the real room manager for _roomTool
        /// (see IRestorableRoomManager) so it gets its actual decoration —
        /// carpet, nest, bookcases, dummies, coop, shrine, bench, pit/
        /// fence — instead of a bare placeholder-colored cube. A room
        /// needs an owner in this game's model (only Structures — Core/
        /// Portal Room — can be ownerless), the same requirement
        /// DungeonGrid.TryAssignRoom already enforces (Ownership must be
        /// Claimed) for every real room manager this now routes through —
        /// so this no-ops with no owner selected rather than silently
        /// placing an unowned, undecorated room.
        private void PlaceRoomFootprint(Vector2Int start, Vector2Int end)
        {
            if (_selectedOwnerId < 0)
            {
                return;
            }

            var manager = _roomManagers != null && _roomManagers.TryGetValue(_roomTool, out var found) ? found : null;

            foreach (var rect in GetMirroredRects(start, end))
            {
                var minX = Mathf.Min(rect.start.x, rect.end.x);
                var maxX = Mathf.Max(rect.start.x, rect.end.x);
                var minY = Mathf.Min(rect.start.y, rect.end.y);
                var maxY = Mathf.Max(rect.start.y, rect.end.y);
                var minCoord = new Vector2Int(minX, minY);
                var maxCoord = new Vector2Int(maxX, maxY);

                // Claim the footprint first — the real room managers
                // require Claimed Floor (see DungeonGrid.TryAssignRoom),
                // same as LevelDesignerSession.PlaceStructure already does
                // for Core/Portal Room.
                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        _grid.EditorPaintFloor(new Vector2Int(x, y), claimed: true, ownerId: _selectedOwnerId);
                    }
                }

                if (manager != null && manager.RestoreRoom(minCoord, maxCoord, _selectedOwnerId))
                {
                    continue;
                }

                // Fallback — no manager wired for this tool (shouldn't
                // happen for any real RoomDesignTool value), or
                // RestoreRoom rejected the footprint (e.g. smaller than a
                // hard-minimum room type's own size floor) — same bare
                // placeholder tagging every room used before real
                // managers were wired in here.
                var roomId = $"{_roomTool}_{_nextRoomId++}";
                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        _grid.EditorPlaceRoomTile(new Vector2Int(x, y), roomId);
                    }
                }
            }
        }

        /// Edit mode's tap handler — identifies whatever's at coord
        /// (checking creatures/structures first, since those sit on top
        /// of a tile, then falling back to the tile itself) and records
        /// it as the current selection for LevelDesignerMenuBar's Edit
        /// panel to show and ReassignSelectedOwner to act on. Clears the
        /// selection instead if coord holds nothing reassignable (plain
        /// Rock, Unclaimed floor, terrain, ...).
        private void SelectAt(Vector2Int coord)
        {
            if (_session.TryFindCreatureAt(coord, out var creatureIndex))
            {
                _selectionKind = EditSelectionKind.Creature;
                _selectedCoord = coord;
                _selectedCreatureIndex = creatureIndex;
                _selectedCurrentOwnerId = _session.Creatures[creatureIndex].OwnerId;
                _grid.SetSelectedWall(null);
                return;
            }

            if (_session.TryFindStructureAt(coord, out var structureIndex))
            {
                _selectionKind = EditSelectionKind.Structure;
                _selectedCoord = coord;
                _selectedStructureIndex = structureIndex;
                _selectedCurrentOwnerId = _session.Structures[structureIndex].OwnerId;
                _grid.SetSelectedWall(null);
                return;
            }

            if (!_grid.InBounds(coord))
            {
                ClearSelection();
                return;
            }

            var tile = _grid.GetTile(coord);
            if (tile.HasRoom)
            {
                _selectionKind = EditSelectionKind.Room;
                _selectedCoord = coord;
                _selectedRoomId = tile.RoomId;
                _selectedCurrentOwnerId = tile.OwnerId;
                _grid.SetSelectedWall(coord);
                return;
            }

            var isReassignableTile = (tile.Type == TileType.Floor && tile.Ownership == TileOwnership.Claimed) || tile.IsReinforced;
            if (!isReassignableTile)
            {
                ClearSelection();
                return;
            }

            _selectionKind = EditSelectionKind.Tile;
            _selectedCoord = coord;
            _selectedCurrentOwnerId = tile.OwnerId;
            _grid.SetSelectedWall(coord);
        }

        /// Remove mode's tap handler — deletes whatever's at coord, checking
        /// creatures/structures first (they sit on top of a tile), then the
        /// tile's room, then the bare tile itself. Rooms are torn down
        /// through LairManager.TrySellRoom (the same path gameplay's Sell
        /// tool uses, so every room manager cleans up its own decoration
        /// via RoomSold) and their footprint is then reset to plain Rock,
        /// which TrySellRoom on its own doesn't do (it leaves bare Claimed
        /// Floor). A no-op on untouched Rock / empty tiles.
        private void RemoveAt(Vector2Int coord)
        {
            if (!_grid.InBounds(coord))
            {
                return;
            }

            if (_session.RemoveCreatureAt(coord) || _session.RemoveStructureAt(coord))
            {
                return;
            }

            var tile = _grid.GetTile(coord);
            if (tile.HasRoom)
            {
                var footprint = _grid.GetRoomFootprint(tile.RoomId);
                // EditorRemoveRoomAt, not TrySellRoom — the latter refuses a
                // room owned by another player (a gameplay-Sell guard), which
                // would leave that room's manager still holding its visuals
                // while the loop below clears the grid tiles out from under
                // them.
                _lairManager?.EditorRemoveRoomAt(coord);
                foreach (var footprintCoord in footprint)
                {
                    _grid.EditorResetToRock(footprintCoord);
                }
                return;
            }

            _grid.EditorResetToRock(coord);
        }

        private void ClearSelection()
        {
            _selectionKind = EditSelectionKind.None;
            _selectedRoomId = null;
            _selectedStructureIndex = -1;
            _selectedCreatureIndex = -1;
            _selectedCurrentOwnerId = -1;
            if (_grid != null)
            {
                _grid.SetSelectedWall(null);
            }
        }

        /// Applies ownerId to whatever Edit mode currently has selected
        /// (see SelectAt) and updates the readout to match — a no-op if
        /// nothing is selected. Room reassigns every tile sharing that
        /// RoomId at once (see DungeonGrid.EditorReassignRoomOwner), not
        /// just the one tile that happened to be tapped, since a room has
        /// one owner conceptually. ownerId < 0 is the "Unclaimed" pseudo-
        /// player: valid for a tile/structure/creature (which just become
        /// unowned), ignored for a Room since a room must belong to someone.
        public void ReassignSelectedOwner(int ownerId)
        {
            switch (_selectionKind)
            {
                case EditSelectionKind.Tile:
                    _grid.EditorReassignOwner(_selectedCoord, ownerId);
                    _selectedCurrentOwnerId = ownerId;
                    break;
                case EditSelectionKind.Room:
                    if (ownerId < 0)
                    {
                        return;
                    }
                    _grid.EditorReassignRoomOwner(_selectedRoomId, ownerId);
                    _selectedCurrentOwnerId = ownerId;
                    break;
                case EditSelectionKind.Structure:
                    _session.SetStructureOwner(_selectedStructureIndex, ownerId);
                    _selectedCurrentOwnerId = ownerId;
                    break;
                case EditSelectionKind.Creature:
                    _session.SetCreatureOwner(_selectedCreatureIndex, ownerId);
                    _selectedCurrentOwnerId = ownerId;
                    break;
            }
        }

        /// Applies the active map-design tool to coord and, if Mirror X/Y
        /// is on, to every coordinate reflected across the active
        /// divider(s) too (see GetMirroredCoords) — the actual per-tile
        /// logic lives in ApplyMapDesignToolAt.
        private void ApplyMapDesignTool(Vector2Int coord)
        {
            foreach (var mirroredCoord in GetMirroredCoords(coord))
            {
                ApplyMapDesignToolAt(mirroredCoord);
            }
        }

        private void ApplyMapDesignToolAt(Vector2Int coord)
        {
            switch (_mapDesignTool)
            {
                case MapDesignTool.PlainWall:
                    _grid.EditorPaintWall(coord, EditorWallVariant.Plain);
                    break;
                case MapDesignTool.ReinforcedWall:
                    _grid.EditorPaintWall(coord, EditorWallVariant.Reinforced, _selectedOwnerId);
                    break;
                case MapDesignTool.GoldWall:
                    _grid.EditorPaintWall(coord, EditorWallVariant.GoldWall);
                    break;
                case MapDesignTool.RegeneratingGoldWall:
                    _grid.EditorPaintWall(coord, EditorWallVariant.RegeneratingGoldWall);
                    break;
                case MapDesignTool.ManaCrystalWall:
                    _grid.EditorPaintWall(coord, EditorWallVariant.ManaCrystalWall);
                    break;
                case MapDesignTool.Bedrock:
                    _grid.EditorPaintWall(coord, EditorWallVariant.Bedrock);
                    break;
                case MapDesignTool.Water:
                    _grid.EditorPaintTerrain(coord, TileType.Water);
                    break;
                case MapDesignTool.Lava:
                    _grid.EditorPaintTerrain(coord, TileType.Lava);
                    break;
                case MapDesignTool.Chasm:
                    _grid.EditorPaintTerrain(coord, TileType.Chasm);
                    break;
                case MapDesignTool.HolyGround:
                    _grid.EditorPaintTerrain(coord, TileType.HolyGround);
                    break;
                case MapDesignTool.UnclaimedFloor:
                    _grid.EditorPaintFloor(coord, claimed: false, ownerId: -1);
                    break;
                case MapDesignTool.ClaimedFloor:
                    // "Unclaimed" picked in the owner selector (ownerId < 0)
                    // means exactly that — paint plain unclaimed floor, same
                    // as the dedicated Unclaimed tool, rather than a
                    // contradictory claimed-but-unowned tile.
                    _grid.EditorPaintFloor(coord, claimed: _selectedOwnerId >= 0, ownerId: _selectedOwnerId);
                    break;
            }
        }
    }
}

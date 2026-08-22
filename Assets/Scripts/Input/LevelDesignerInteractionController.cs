using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.LevelDesigner;
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

    /// Every room the level designer's Rooms menu can stamp down — the
    /// same 8 room types GameBootstrap's own gameplay setup wires up,
    /// minus Bridge (a painted line over Water/Lava, not a rectangular
    /// footprint — out of scope for this first pass).
    public enum RoomDesignTool
    {
        None,
        Lair,
        Treasury,
        SlimeHatchery,
        BaconBeacon,
        TrainingRoom,
        Library,
        Jail,
        ConversionClass
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

        private MapDesignTool _mapDesignTool = MapDesignTool.None;
        private RoomDesignTool _roomTool = RoomDesignTool.None;
        private StructureKind? _structureTool;
        private EditorCreatureKind? _creatureTool;
        private int _selectedOwnerId = -1;
        private int _nextRoomId;

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
        public bool MirrorX => _mirrorX;
        public bool MirrorY => _mirrorY;

        /// While a room placement is being dragged out, the rectangle so
        /// far — read by LevelDesignerMenuBar to show the player what
        /// they're about to place, same idea as TileInteractionController's
        /// own IsPlacingLair/LairDragCurrentCoord.
        public bool IsPlacingRoom => _isDraggingRoom;
        public Vector2Int RoomDragStartCoord => _dragStartCoord;
        public Vector2Int RoomDragCurrentCoord => _roomDragCurrentCoord;

        public void Initialize(Camera camera, DungeonGrid grid, LevelDesignerSession session)
        {
            _camera = camera;
            _grid = grid;
            _session = session;
        }

        public void SetMapDesignTool(MapDesignTool tool)
        {
            _mapDesignTool = tool;
            _roomTool = RoomDesignTool.None;
            _structureTool = null;
            _creatureTool = null;
        }

        public void SetRoomTool(RoomDesignTool tool)
        {
            _roomTool = tool;
            _mapDesignTool = MapDesignTool.None;
            _structureTool = null;
            _creatureTool = null;
        }

        public void SetStructureTool(StructureKind? kind)
        {
            _structureTool = kind;
            _mapDesignTool = MapDesignTool.None;
            _roomTool = RoomDesignTool.None;
            _creatureTool = null;
        }

        public void SetCreatureTool(EditorCreatureKind? kind)
        {
            _creatureTool = kind;
            _mapDesignTool = MapDesignTool.None;
            _roomTool = RoomDesignTool.None;
            _structureTool = null;
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
                return;
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

        private void PlaceRoomFootprint(Vector2Int start, Vector2Int end)
        {
            // Each mirrored copy of the dragged rectangle becomes its own
            // room (own roomId) — see GetMirroredRects.
            foreach (var rect in GetMirroredRects(start, end))
            {
                var roomId = $"{_roomTool}_{_nextRoomId++}";
                var minX = Mathf.Min(rect.start.x, rect.end.x);
                var maxX = Mathf.Max(rect.start.x, rect.end.x);
                var minY = Mathf.Min(rect.start.y, rect.end.y);
                var maxY = Mathf.Max(rect.start.y, rect.end.y);

                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        _grid.EditorPlaceRoomTile(new Vector2Int(x, y), roomId);
                    }
                }
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
                    _grid.EditorPaintWall(coord, EditorWallVariant.Reinforced);
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
                    _grid.EditorPaintFloor(coord, claimed: true, ownerId: _selectedOwnerId);
                    break;
            }
        }
    }
}

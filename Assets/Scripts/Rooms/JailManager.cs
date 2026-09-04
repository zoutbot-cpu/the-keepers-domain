using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Creatures;
using KeepersDomain.Grid;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Rooms
{
    /// Storage for defeated creatures the player wishes to keep, per the
    /// design doc — a sunken pit inset one tile from every edge of the
    /// room's own footprint, so a placed Jail is a walkable ground-level
    /// walkway ring around a lower pit, not pit wall-to-wall. Hard 5x5
    /// minimum footprint (see MinFootprintSize) — same
    /// SlimeHatcheryManager-style "reject anything smaller than
    /// MinFootprintSize in either dimension" check every hard-minimum
    /// room uses — which guarantees at least a 3x3 pit inside the 1-tile
    /// ring. Ringed by a low fence around the pit's own boundary (not the
    /// room's outer edge) with a single staircase-and-gate entrance.
    /// Attracts the Maze Rattler (haunts the pit tiles, no interaction with
    /// prisoners). Capture is real now: dropping a carried Gremlin/Warlock/
    /// Maze Rattler/Elf onto a pit tile via the Grab hand (see
    /// MinionGrabController.TryDrop/JailManager.TryCapture) imprisons it as
    /// inert JailedPrisoner data — no longer a live agent — until Conversion
    /// Class's Bean Counter processes it (ConversionClassManager.
    /// TryTormentRandomPrisoner/JailManager.TryReleaseRandomPrisoner). This
    /// class only owns the prisoner bookkeeping and its "bound blob"
    /// visual; the actual torment/outcome logic lives in
    /// ConversionClassManager. Placement/visuals otherwise follow the same
    /// "player-placed, subscribes to RoomSold" shape TrainingRoomManager/
    /// LibraryManager use, including their adjacent-placement merge rule
    /// (see TryFindMergeableRoom): a
    /// footprint that exactly completes a rectangle together with an
    /// existing Jail extends that Jail instead of starting a separate
    /// one. A merge can only ever grow the room, which can only ever grow
    /// or hold steady each tile's margin to the room's own edges — so a
    /// merge can turn a former ring tile into a pit tile (if the seam
    /// between the two merged rectangles was previously an outer edge and
    /// is now interior) but never the reverse; see IsPitTile and its call
    /// site in TryPlaceJailInternal, which re-evaluates every tile in the
    /// room (not just the newly added footprint) on every placement for
    /// exactly this reason.
    ///
    /// Two things set a Jail apart from every other room type:
    /// - It can be placed directly on undug Rock — CanPlaceFootprint
    ///   accepts Rock tiles (dug and claimed on the spot, see
    ///   TryPlaceJailInternal) alongside the usual already-dug
    ///   DungeonGrid.CanBuildRoomOn tiles every other room requires. Per
    ///   the brief: "not needed to be dug out, placing the room tiles
    ///   will do that."
    /// - Its floor renders one level lower (DungeonGrid.SetPitDepth) than
    ///   ordinary floor. That's a render-time Y offset only —
    ///   DungeonGrid.IsWalkable checks Type/IsBlocked, never Y — so a Jail
    ///   tile is exactly as walkable as any other Floor tile despite
    ///   sitting visually in a pit; prisoners/implings can path across it
    ///   like any room. The pit's own rim is the real dungeon_pack
    ///   jail_wall_inside mesh (see BuildRimWallVisual), just deep enough
    ///   to close the gap under the fence — deliberately NOT a slab under
    ///   the whole tile: a full-footprint column reaching up to ground
    ///   level would sit physically in front of (above) the sunk floor
    ///   from any downward-looking angle and bury it, which is exactly
    ///   the "solid black box" bug an earlier, much deeper version of
    ///   this wall had. Interior pit tiles have no elevation seam against
    ///   their neighbors and don't need a wall at all, same as ordinary
    ///   DungeonGrid floor never does.
    ///
    /// Real dungeon_pack art now throughout (Assets/Art/DungeonPack/Jail —
    /// wall_inside/fence_half/gate/stairs_wood meshes, see
    /// DungeonPackPropSetup; floor_gravel_dirty/floor_grate_floor
    /// textures, see DungeonPackRoomArt), each with the same graceful
    /// fallback to this class's original flat-colored primitives every
    /// other room's real-mesh integration uses, in case a prop hasn't
    /// been set up yet (Tools > DungeonPack > Setup Props).
    public class JailManager : MonoBehaviour, IRestorableRoomManager
    {
        /// Gold cost per tile of a placed Jail — no design-brief value
        /// exists yet, so this matches Training Room/Library's own
        /// unbalanced 20g/tile placeholder rather than inventing a new
        /// number.
        public const int CostPerTile = 20;

        // Hard minimum, same enforcement shape as
        // SlimeHatcheryManager.MinFootprintSize — checked against the
        // dragged footprint itself in TryPlaceJailInternal, not the
        // merged result (a merge-extension can be smaller, same as every
        // other mergeable room type allows). Guarantees the 1-tile ring
        // always has a real pit (at least 3x3) inside it.
        private const int MinFootprintSize = 5;

        // One full grid cell lower than the surrounding ground — "one
        // level deeper" per the brief. DungeonGrid's cellSize is 1 world
        // unit per grid step, so this is exactly one step down.
        private const float PitDepth = 1f;

        // How far down the rim wall reaches — just 2 tile-heights (cellSize
        // is 1 world unit per grid step, same "world units == grid steps"
        // assumption PitDepth relies on). Doesn't need to fake bottomless
        // depth any more: the pit's actual rim marker is the light gray
        // fence below, not this wall — the wall now only needs to close
        // the immediate gap under that fence.
        private const float RimWallDepth = 2f;
        private const float RimWallThickness = 0.1f;

        // Right at the tile's true edge (half a cell is 0.5) rather than
        // the fence's own inset — the wall and the low fence rail below
        // sit at slightly different depths along the same edge so their
        // faces don't coplanar-z-fight with each other.
        private const float RimWallEdgeInset = 0.49f;

        // Real dungeon_pack retaining-wall mesh (Assets/Art/DungeonPack/
        // Jail/WallInside, built by Tools > DungeonPack > Setup Props into
        // Dungeon/Prop_JailWallInside) — grimy stone, 1w x 2h with its
        // pivot at the TOP (Y=0 = ground level, extends down 2 units —
        // matches RimWallDepth exactly, no scale correction needed).
        // Falls back to the original plain dark box below if the prop
        // hasn't been set up yet, same graceful-degradation pattern every
        // other room's real-mesh prop uses. _rimWallColor is fallback-only
        // now, but still doubles as the ring floor grate's own fallback
        // panel color (see BuildGrateFloorVisual) so the two still read as
        // the same material when neither real asset has loaded.
        private GameObject _jailWallInsidePrefab;
        [SerializeField] private Color _rimWallColor = new Color(0.05f, 0.05f, 0.05f);

        // Real dungeon_pack perimeter-rail mesh (Assets/Art/DungeonPack/
        // Jail/FenceHalf, built into Dungeon/Prop_JailFenceHalf) — light
        // wood-brown, ~0.95w x 1h with its pivot at the bottom (ground
        // level). The pit's only decorated rim marker along every
        // outward-facing edge except the one gate tile/edge, grounded at
        // ordinary FloorSurfaceY regardless of the pit's own sunk floor,
        // since it's a guard rail marking the rim, not something that
        // spans the pit's depth. Falls back to the original plain gray
        // box below if the prop hasn't been set up yet.
        private GameObject _jailFenceHalfPrefab;
        [SerializeField] private Color _fenceColor = new Color(0.75f, 0.75f, 0.75f);
        private const float FenceRailHeight = 0.35f;
        private const float FenceRailThickness = 0.06f;
        private const float FenceEdgeInset = 0.42f;

        // Ring floor "grate" — every walkway tile around the pit gets the
        // real dungeon_pack grate floor texture (see _floorGrateMaterial)
        // now; _grateCrossColor/GrateCrossBarThickness/
        // GrateCrossReliefOffset are fallback-only, used only if that
        // texture failed to load — see BuildGrateFloorVisual.
        [SerializeField] private Color _grateCrossColor = new Color(0.75f, 0.75f, 0.75f);
        private const float GrateFloorHeight = 0.17f;
        private const float GrateFloorFootprintScale = 0.95f;
        private const float GrateCrossBarThickness = 0.09f;

        // How far the cross bars sit proud of the panel beneath them —
        // without this they'd sit exactly coplanar with the panel and
        // z-fight/flicker. Fallback-only, same as the cross itself.
        private const float GrateCrossReliefOffset = 0.02f;

        // Staircase + gate — one designated boundary tile (the south
        // edge's middle tile — always a real boundary edge regardless of
        // footprint size, since origin is the rectangle's min corner)
        // descends from ground level down to the pit floor. Real
        // dungeon_pack meshes now (Assets/Art/DungeonPack/Jail/
        // StairsWood + .../Gate, built into Dungeon/Prop_JailStairsWood +
        // Dungeon/Prop_JailGate) — the stairs' own pivot sits near ground
        // level and descends ~1.6 units into the room as it drops the
        // full PitDepth, positioned at the gate tile's own outward edge
        // (best-guess placement, no in-Editor render to confirm against,
        // same honesty this session's other per-room props carry); the
        // gate is a barred 1w x 2h topper, pivot at the bottom, sitting at
        // that same edge. Both only ever get built for the one hardcoded
        // south-facing gate edge, same as the primitive fallbacks below
        // (StepCount ascending cubes + two gate posts) they replace when
        // the prop hasn't been set up yet.
        private GameObject _jailStairsWoodPrefab;
        private GameObject _jailGatePrefab;
        [SerializeField] private Color _stepColor = new Color(0.42f, 0.38f, 0.32f);
        [SerializeField] private Color _gatePostColor = new Color(0.3f, 0.26f, 0.2f);
        private const int StepCount = 3;
        private const float GatePostHeight = 0.6f;
        private const float GatePostRadius = 0.05f;

        // Real dungeon_pack pit-floor texture (Assets/Resources/Dungeon/
        // Jail/floor_gravel_dirty — a plain texture, no prefab/material
        // build step needed, same as DungeonGrid's own Floors set), built
        // into a real URP/Lit material once in Initialize (see
        // DungeonPackRoomArt.BuildMaterial). Sits flush on top of
        // DungeonGrid's own (purple, HasRoom-colored) floor cube for
        // every pit tile, same "textured floor overlay flush on the base
        // tile" convention every other room's own floor uses (taller than
        // DungeonGrid's own 0.15 floor height so its top face wins the
        // z-fight instead of flickering). A full-cell gray Seam sits
        // beneath it (see its own field header below) so the gap this
        // overlay's own 0.95 footprint would otherwise leave at every
        // tile edge — previously showing DungeonGrid's own untextured
        // floor cube through, rendering pink — reads as a mortar line
        // instead, same fix already applied to every other room this
        // session. _dirtFloorColor is fallback-only now, used only if the
        // texture itself failed to load. Persists across a merge same as
        // the base pit sink does — only fence/rim wall/gate get torn down
        // and rebuilt for a bigger shape.
        private Material _floorGravelMaterial;
        [SerializeField] private Color _dirtFloorColor = new Color(0.36f, 0.27f, 0.17f);
        private const float DirtFloorHeight = 0.17f;
        private const float DirtFloorFootprintScale = 0.95f;

        // Real dungeon_pack ring-floor texture (Assets/Resources/Dungeon/
        // Jail/floor_grate_floor), built the same way as
        // _floorGravelMaterial above — replaces the old procedural black-
        // panel-with-a-gray-cross look (still built as the fallback if
        // this texture failed to load, see BuildGrateFloorVisual).
        private Material _floorGrateMaterial;

        // Both floor overlays' own 0.95 footprint leaves the same thin
        // edge gap every other room's Seam layer already fixes — shared
        // here since DirtFloorHeight and GrateFloorHeight are the same
        // 0.17, so one Seam height/color works for both. Sits between
        // DungeonGrid's own 0.15 hidden-tile height and the overlay's own
        // 0.17 (tall enough to fully hide that tile, short enough that
        // the overlay still visibly "pops out" above it).
        [SerializeField] private Color _seamColor = new Color(0.32f, 0.32f, 0.32f);
        private const float SeamFootprintScale = 1.0f;
        private const float SeamHeight = 0.16f;

        [SerializeField] private Color _previewValidColor = new Color(0.35f, 0.95f, 0.4f);
        [SerializeField] private Color _previewInvalidColor = new Color(0.95f, 0.25f, 0.25f);
        private const float PreviewClearance = 0.02f;
        private const float PreviewHeight = 0.08f;
        private const float PreviewFootprintScale = 0.8f;
        private const float RockTopY = 0.5f;

        private DungeonGrid _grid;
        private BuilderJobBoard _jobBoard;
        private TreasuryManager _treasuryManager;
        private int _ownerId;
        private int _nextRoomId;
        private readonly Dictionary<string, List<Vector2Int>> _roomTiles = new Dictionary<string, List<Vector2Int>>();

        // Per-tile — the dirt floor (pit tiles) and grate floor (ring
        // tiles) doesn't depend on the room's overall shape the way the
        // rim structures below do, EXCEPT that a merge can promote a
        // former ring tile to pit (see IsPitTile's own comment), which is
        // the one case that has to swap a tile from _ringFloorVisuals
        // into _floorVisuals — see the per-tile loop in
        // TryPlaceJailInternal.
        private readonly Dictionary<Vector2Int, GameObject> _floorVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly Dictionary<Vector2Int, GameObject> _ringFloorVisuals = new Dictionary<Vector2Int, GameObject>();

        // Per-room — fence rails, rim walls, and the one staircase/gate,
        // torn down and rebuilt in full every time this room's footprint
        // changes (initial placement or a merge-extension), since which
        // tiles are boundary vs. interior — and where the gate lands —
        // depends on the room's overall shape.
        private readonly Dictionary<string, List<GameObject>> _rimStructures = new Dictionary<string, List<GameObject>>();

        // "Half wall" display mode — the counterpart to DungeonGrid.
        // SetHalfWalls for ordinary Rock walls, wired to the same Settings
        // toggle (see BottomMenuBar). The pit's rim wall and fence rail get
        // squashed to half height about the *pit floor* — not the normal
        // floor surface everything else sits on, which is the whole reason
        // the Jail needs its own handling — pressing their tops down so an
        // isometric camera can see over the rim into the pit. Purely
        // cosmetic; the pit stays exactly as deep and as walkable as before.
        // _rimFullTransforms remembers each affected structure's untouched
        // world position + local scale so the toggle is exactly reversible
        // however many times it's flipped.
        private bool _halfWalls;
        private const float HalfWallHeightScale = 0.5f;
        private readonly Dictionary<GameObject, (Vector3 Position, Vector3 Scale)> _rimFullTransforms =
            new Dictionary<GameObject, (Vector3, Vector3)>();

        // The squash-toward-pit-floor math above (position moves toward
        // pitFloorY as scale shrinks) only keeps the right edge fixed for
        // a CENTER-pivoted object, like the fallback fence/wall's own
        // primitive cubes. The real jail_fence_half mesh is bottom-
        // pivoted instead (see BuildFenceRailVisual) — for that one,
        // ApplyHalfWallState squashes by scale alone and leaves position
        // untouched, so its own bottom stays flush with the ground (and
        // the gate's own bottom) in both full and half mode, rather than
        // sinking into the pit. Populated only for the real-mesh fence
        // instance, never the fallback box.
        private readonly HashSet<GameObject> _bottomPivotHalfWallStructures = new HashSet<GameObject>();

        // Per-room snapshot of that room's current pit tiles — recomputed
        // alongside _rimStructures every placement/merge (see
        // TryPlaceJailInternal), so MazeRattlerAgent's haunting behavior
        // (TryFindRandomPitTile) always sees the up-to-date set without
        // re-deriving it from scratch on every call.
        private readonly Dictionary<string, HashSet<Vector2Int>> _pitTilesByRoom = new Dictionary<string, HashSet<Vector2Int>>();

        private readonly List<GameObject> _previewMarkers = new List<GameObject>();

        // Prisoner color/size — a small bound gray blob sitting on its own
        // pit tile, distinct from the "haunting" Maze Rattler capsules that
        // pass through the same pit tiles (see TryFindRandomPitTile) so the
        // two never read as the same thing.
        [SerializeField] private Color _prisonerColor = new Color(0.55f, 0.55f, 0.58f);
        private const float PrisonerRadiusScale = 0.16f;
        private const float PrisonerHeightScale = 0.22f;

        /// A captured creature, pulled out of Grab-and-drop onto a pit tile
        /// (see MinionGrabController.TryDrop) and held here as inert data —
        /// not a live agent/GameObject — until Conversion Class's own Bean
        /// Counter processes it (see ConversionClassManager.
        /// TryTormentRandomPrisoner). IsGoodAlignment exists for the torment
        /// table's "Good creature" branch, but nothing in the game can
        /// produce a Good creature yet — every creature captured today
        /// (Gremlin/Warlock/Maze Rattler/Elf) is Evil, same "real code,
        /// currently unreachable" honesty this class's own header used to
        /// carry for the whole capture mechanic before this existed.
        private class JailedPrisoner
        {
            public string CreatureKind;
            public string Name;
            public int Level;
            public bool IsGoodAlignment;
            public Vector2Int PitCoord;
        }

        // Keyed by the pit tile the prisoner occupies — occupancy is just
        // key presence, same "dictionary keyed by coord" shape
        // _floorVisuals/_ringFloorVisuals already use.
        private readonly Dictionary<Vector2Int, JailedPrisoner> _prisoners = new Dictionary<Vector2Int, JailedPrisoner>();
        private readonly Dictionary<Vector2Int, GameObject> _prisonerVisuals = new Dictionary<Vector2Int, GameObject>();

        /// jobBoard may be null — the Level Designer wires Jail with no
        /// BuilderJobBoard at all (it has no dig-job queue and shouldn't
        /// run one just to host this room), so the one place that uses it
        /// (auto-dig-and-claim on placement, see TryPlaceJailInternal)
        /// guards with a null-conditional instead of assuming it exists.
        public void Initialize(DungeonGrid grid, BuilderJobBoard jobBoard, LairManager lairManager, TreasuryManager treasuryManager, int ownerId = 0)
        {
            _grid = grid;
            _jobBoard = jobBoard;
            _treasuryManager = treasuryManager;
            _ownerId = ownerId;
            _nextRoomId = ownerId * DungeonGrid.RoomIdOwnerStride;
            lairManager.RoomSold += OnRoomSold;

            _jailWallInsidePrefab = Resources.Load<GameObject>("Dungeon/Prop_JailWallInside");
            _jailFenceHalfPrefab = Resources.Load<GameObject>("Dungeon/Prop_JailFenceHalf");
            _jailGatePrefab = Resources.Load<GameObject>("Dungeon/Prop_JailGate");
            _jailStairsWoodPrefab = Resources.Load<GameObject>("Dungeon/Prop_JailStairsWood");
            _floorGravelMaterial = DungeonPackRoomArt.BuildMaterial("Dungeon/Jail/floor_gravel_dirty");
            _floorGrateMaterial = DungeonPackRoomArt.BuildMaterial("Dungeon/Jail/floor_grate_floor");
        }

        /// How many prisoners are currently held across every placed Jail —
        /// read by BeanCounterAgent to decide whether there's anyone to
        /// torment this lecture session.
        public int PrisonerCount => _prisoners.Count;

        /// Whether coord is a pit tile of any placed Jail — a public wrapper
        /// over the per-room _pitTilesByRoom bookkeeping (previously only
        /// checked internally), needed by MinionGrabController to recognize
        /// a "drop onto Jail" gesture the same way it already recognizes a
        /// "drop onto Training Room" one.
        public bool IsPitTile(Vector2Int coord)
        {
            foreach (var pitTiles in _pitTilesByRoom.Values)
            {
                if (pitTiles.Contains(coord))
                {
                    return true;
                }
            }

            return false;
        }

        /// Whether any placed Jail has at least one unoccupied pit tile —
        /// checked before an Imp starts a Capture Enemy job (see
        /// ImplingAgent.TryStartDownedBodyJob / design-doc.md's Combat
        /// section).
        public bool HasFreePitTile()
        {
            foreach (var pitTiles in _pitTilesByRoom.Values)
            {
                foreach (var coord in pitTiles)
                {
                    if (!_prisoners.ContainsKey(coord))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// Nearest reachable unoccupied pit tile — where an Imp hauling a
        /// captured enemy walks to before calling TryCapture.
        public bool TryFindNearestFreePitTile(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var bestDistance = int.MaxValue;
            targetCoord = default;
            var found = false;

            foreach (var pitTiles in _pitTilesByRoom.Values)
            {
                foreach (var coord in pitTiles)
                {
                    if (_prisoners.ContainsKey(coord))
                    {
                        continue;
                    }

                    if (distances.TryGetValue(coord, out var distance) && distance < bestDistance)
                    {
                        bestDistance = distance;
                        targetCoord = coord;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// Captures a creature into the nearest reachable, currently-empty
        /// pit tile of any placed Jail — called by MinionGrabController
        /// when a carried creature is dropped onto a Jail pit tile.
        /// Opportunistic: returns false (never forces the caller to fail
        /// the drop) if there's no Jail, every pit tile is already
        /// occupied, or none is reachable from nearCoord.
        public bool TryCapture(Vector2Int nearCoord, string creatureKind, string name, int level, bool isGoodAlignment)
        {
            var distances = _grid.GetReachableFloorDistances(nearCoord);
            var bestDistance = int.MaxValue;
            var found = false;
            var targetCoord = default(Vector2Int);

            foreach (var pitTiles in _pitTilesByRoom.Values)
            {
                foreach (var coord in pitTiles)
                {
                    if (_prisoners.ContainsKey(coord))
                    {
                        continue;
                    }

                    if (distances.TryGetValue(coord, out var distance) && distance < bestDistance)
                    {
                        bestDistance = distance;
                        targetCoord = coord;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                return false;
            }

            _prisoners[targetCoord] = new JailedPrisoner
            {
                CreatureKind = creatureKind,
                Name = name,
                Level = level,
                IsGoodAlignment = isGoodAlignment,
                PitCoord = targetCoord
            };
            _prisonerVisuals[targetCoord] = BuildPrisonerVisual(targetCoord);
            GameplayLog.Write(_ownerId, $"{name} was thrown in the Jail at ({targetCoord.x},{targetCoord.y})");
            return true;
        }

        /// Hauls an already-knocked-out creature (its DownedBody, carried in
        /// by the Grab hand or an Imp's Capture Enemy job — see
        /// design-doc.md's Combat section) into the nearest reachable empty
        /// pit tile. Unlike TryCapture (which turns a still-live creature
        /// into an inert gray blob), this parks the creature's own capsule
        /// in the pit as the prisoner visual, tracked in _prisonerVisuals so
        /// the existing release/sell cleanup tears it down. Opportunistic:
        /// false (caller keeps the body) if no reachable empty pit tile.
        public bool TryCaptureBody(Vector2Int nearCoord, DownedBody body)
        {
            if (body == null || !TryFindNearestFreePitTile(nearCoord, out var targetCoord))
            {
                return false;
            }

            _prisoners[targetCoord] = new JailedPrisoner
            {
                CreatureKind = body.Species,
                Name = body.DisplayName,
                Level = body.Level,
                IsGoodAlignment = false,
                PitCoord = targetCoord
            };

            var worldPos = _grid.GridToWorld(targetCoord);
            body.transform.position = new Vector3(worldPos.x, _grid.FloorSurfaceY - PitDepth + 0.1f, worldPos.z);
            body.MarkJailed();
            _prisonerVisuals[targetCoord] = body.gameObject;

            GameplayLog.Write(_ownerId, $"{body.DisplayName} was hauled into the Jail at ({targetCoord.x},{targetCoord.y})");
            return true;
        }

        /// Pops one random currently-held prisoner (across every placed
        /// Jail) for Conversion Class's Bean Counter to process — see
        /// ConversionClassManager.TryTormentRandomPrisoner. Frees the pit
        /// tile and tears down its visual as part of the release, whatever
        /// the torment's eventual outcome turns out to be.
        public bool TryReleaseRandomPrisoner(out string creatureKind, out string name, out int level, out bool isGoodAlignment, out Vector2Int pitCoord)
        {
            if (_prisoners.Count == 0)
            {
                creatureKind = null;
                name = null;
                level = 0;
                isGoodAlignment = false;
                pitCoord = default;
                return false;
            }

            var index = UnityEngine.Random.Range(0, _prisoners.Count);
            var i = 0;
            Vector2Int keyCoord = default;
            foreach (var coord in _prisoners.Keys)
            {
                if (i == index)
                {
                    keyCoord = coord;
                    break;
                }
                i++;
            }

            var prisoner = _prisoners[keyCoord];
            creatureKind = prisoner.CreatureKind;
            name = prisoner.Name;
            level = prisoner.Level;
            isGoodAlignment = prisoner.IsGoodAlignment;
            pitCoord = prisoner.PitCoord;

            ReleasePrisonerAt(keyCoord);
            return true;
        }

        private void ReleasePrisonerAt(Vector2Int coord)
        {
            if (_prisonerVisuals.TryGetValue(coord, out var visual) && visual != null)
            {
                Destroy(visual);
            }
            _prisonerVisuals.Remove(coord);
            _prisoners.Remove(coord);
        }

        /// A small bound gray blob sitting on its own pit tile — same
        /// primitives-only, collider-stripped convention every structure in
        /// this class already uses.
        private GameObject BuildPrisonerVisual(Vector2Int coord)
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = $"JailPrisoner_{coord.x}_{coord.y}";
            visual.transform.SetParent(transform, false);
            visual.transform.localScale = new Vector3(PrisonerRadiusScale, PrisonerHeightScale, PrisonerRadiusScale);
            var groundY = _grid.FloorSurfaceY - PitDepth;
            var worldPos = _grid.GridToWorld(coord);
            visual.transform.position = new Vector3(worldPos.x, groundY + PrisonerHeightScale, worldPos.z);
            Prims.Tint(visual, _prisonerColor);
            Destroy(visual.GetComponent<Collider>());
            return visual;
        }

        /// Total tile count across every placed Jail — mirrors the
        /// TotalTileCount convention SlimeHatcheryManager/
        /// TrainingRoomManager/LibraryManager/LairManager all expose.
        public int TotalTileCount
        {
            get
            {
                var total = 0;
                foreach (var tiles in _roomTiles.Values)
                {
                    total += tiles.Count;
                }
                return total;
            }
        }

        /// Number of distinct placed Jail rooms (not tiles) — read by
        /// MazeRattlerSpawner as its join requirement ("1 Jail per 5 Maze
        /// Rattlers"). A room count rather than a tile count on purpose:
        /// Jail's 5x5 minimum footprint (25+ tiles) makes a per-tile ratio
        /// meaningless for a "5 per Jail" rule the way it works for the
        /// smaller utility rooms' per-tile population caps.
        public int RoomCount => _roomTiles.Count;

        /// Places a Jail spanning the rectangle between startCoord and
        /// endCoord inclusive. Unlike every other room, footprint tiles
        /// don't need to already be dug — see CanPlaceFootprint. Fails
        /// atomically, same as every other room manager's Try* method.
        public bool TryPlaceJail(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceJailInternal(startCoord, endCoord, chargeGold: true);
        }

        /// Places a Jail exactly like TryPlaceJail but without charging
        /// gold — for terrain generation, not a player purchase, same as
        /// TreasuryManager.PlaceStartingTreasury. Not currently called by
        /// GameBootstrap (no starting Jail exists yet), kept for parity
        /// with every other room manager's Place* pair.
        public bool PlaceStartingJail(Vector2Int startCoord, Vector2Int endCoord)
        {
            return TryPlaceJailInternal(startCoord, endCoord, chargeGold: false);
        }

        /// IRestorableRoomManager — see its own header. ownerId is unused
        /// here; the footprint is expected to already be Claimed Floor
        /// (owned correctly) by the time this runs.
        public bool RestoreRoom(Vector2Int start, Vector2Int end, int ownerId)
        {
            return PlaceStartingJail(start, end);
        }

        private bool TryPlaceJailInternal(Vector2Int startCoord, Vector2Int endCoord, bool chargeGold)
        {
            var footprint = GetFootprint(startCoord, endCoord, out var newWidth, out var newHeight, out var newOrigin);
            if (newWidth < MinFootprintSize || newHeight < MinFootprintSize || !CanPlaceFootprint(footprint))
            {
                return false;
            }

            if (chargeGold && _treasuryManager != null && !_treasuryManager.TrySpendGold(footprint.Count * CostPerTile))
            {
                return false;
            }

            string roomId;
            var origin = newOrigin;
            var width = newWidth;
            var height = newHeight;

            // Extends an existing Jail instead of starting a separate one
            // if footprint exactly completes a rectangle together with
            // it — see TryFindMergeableRoom. Only the fence/wall/gate need
            // rebuilding for the new shape; each tile's own pit sink,
            // once applied, is never undone by a merge (see IsPitTile).
            if (TryFindMergeableRoom(footprint, out var existingRoomId, out var mergedOrigin, out var mergedWidth, out var mergedHeight))
            {
                roomId = existingRoomId;
                origin = mergedOrigin;
                width = mergedWidth;
                height = mergedHeight;
                _roomTiles[roomId].AddRange(footprint);
                ClearRimStructures(roomId);
            }
            else
            {
                roomId = $"Jail_{_nextRoomId++}";
                _roomTiles[roomId] = footprint;
                _rimStructures[roomId] = new List<GameObject>();
            }

            foreach (var coord in footprint)
            {
                // Dig-and-claim on the spot rather than requiring a
                // pre-dug Claimed Floor — "not needed to be dug out,
                // placing the room tiles will do that." ApplyClaim (not
                // a bare DungeonGrid.ClaimTile) so the claim job
                // CompleteDig just queued (DungeonGrid.FloorNeedsClaim)
                // is cleared immediately too, instead of lingering as a
                // phantom already-satisfied job in BuilderJobBoard's
                // Tasks list.
                if (_grid.GetTile(coord).Type == TileType.Rock)
                {
                    _grid.CompleteDig(coord, _ownerId);
                    _jobBoard?.ApplyClaim(coord);
                }

                _grid.TryAssignRoom(coord, roomId);
            }

            // Re-evaluated over every tile currently in the room, not
            // just the newly dragged footprint — a merge can turn a
            // former ring tile into a pit tile (see IsPitTile's own
            // comment), and this is the only place that needs to notice.
            // Guarded by _floorVisuals/_ringFloorVisuals.ContainsKey so an
            // already-treated tile from an earlier placement is left
            // untouched rather than rebuilt.
            var allTiles = _roomTiles[roomId];
            var pitTiles = new HashSet<Vector2Int>();
            foreach (var coord in allTiles)
            {
                if (!IsPitTile(coord, origin, width, height))
                {
                    if (!_ringFloorVisuals.ContainsKey(coord))
                    {
                        _ringFloorVisuals[coord] = BuildGrateFloorVisual(coord);
                    }
                    continue;
                }

                pitTiles.Add(coord);
                _grid.SetPitDepth(coord, PitDepth);
                if (!_floorVisuals.ContainsKey(coord))
                {
                    _floorVisuals[coord] = BuildDirtFloorVisual(coord);
                }

                // A merge can promote a former ring tile straight to pit
                // (see IsPitTile's own comment) — clear whatever grate
                // floor it picked up while it was still ring, since a
                // tile is never both at once.
                if (_ringFloorVisuals.TryGetValue(coord, out var grate) && grate != null)
                {
                    Destroy(grate);
                }
                _ringFloorVisuals.Remove(coord);
            }

            _pitTilesByRoom[roomId] = pitTiles;

            // The fence/rim wall/gate ring the pit's own boundary, not
            // the room's outer edge — a pit tile's neighbor is always
            // either another pit tile or one of this same room's ring
            // tiles (the pit is always inset exactly 1 tile from every
            // edge), never a tile outside the room.
            var pitOrigin = origin + Vector2Int.one;
            var pitWidth = width - 2;
            var gateCoord = pitOrigin + new Vector2Int(pitWidth / 2, 0);
            var rimStructures = _rimStructures[roomId];
            foreach (var coord in pitTiles)
            {
                BuildRimStructures(coord, pitTiles, gateCoord, rimStructures);
            }

            return true;
        }

        /// Whether coord sits strictly inside origin/width/height's
        /// rectangle — inset by exactly 1 tile from every edge, i.e. not
        /// on the room's own outer ring. A merge only ever grows the
        /// room's rectangle from wherever it already was, which can only
        /// ever grow or hold steady a given tile's margin to the
        /// rectangle's edges — so a tile already inside (pit) always
        /// stays inside, while a tile on the old ring can newly qualify
        /// as pit once the seam it sat on becomes interior to the bigger
        /// merged rectangle. Never the other direction.
        private static bool IsPitTile(Vector2Int coord, Vector2Int origin, int width, int height)
        {
            return coord.x > origin.x && coord.x < origin.x + width - 1
                && coord.y > origin.y && coord.y < origin.y + height - 1;
        }

        /// Whether footprint, combined with some single already-placed
        /// Jail, would exactly fill a rectangle — i.e. footprint extends
        /// an existing Jail rather than starting a fresh one. Same shape
        /// as TrainingRoomManager.TryFindMergeableRoom: only ever merges
        /// with one existing room at a time, and returns false (footprint
        /// becomes its own new room) if it doesn't cleanly complete a
        /// rectangle with any single one — including simply not being
        /// adjacent to one at all.
        private bool TryFindMergeableRoom(List<Vector2Int> footprint, out string roomId, out Vector2Int mergedOrigin, out int mergedWidth, out int mergedHeight)
        {
            foreach (var entry in _roomTiles)
            {
                var minX = int.MaxValue;
                var maxX = int.MinValue;
                var minY = int.MaxValue;
                var maxY = int.MinValue;

                foreach (var coord in entry.Value)
                {
                    minX = Mathf.Min(minX, coord.x);
                    maxX = Mathf.Max(maxX, coord.x);
                    minY = Mathf.Min(minY, coord.y);
                    maxY = Mathf.Max(maxY, coord.y);
                }

                foreach (var coord in footprint)
                {
                    minX = Mathf.Min(minX, coord.x);
                    maxX = Mathf.Max(maxX, coord.x);
                    minY = Mathf.Min(minY, coord.y);
                    maxY = Mathf.Max(maxY, coord.y);
                }

                var width = maxX - minX + 1;
                var height = maxY - minY + 1;

                if (width * height == entry.Value.Count + footprint.Count)
                {
                    roomId = entry.Key;
                    mergedOrigin = new Vector2Int(minX, minY);
                    mergedWidth = width;
                    mergedHeight = height;
                    return true;
                }
            }

            roomId = null;
            mergedOrigin = default;
            mergedWidth = 0;
            mergedHeight = 0;
            return false;
        }

        /// Tears down this room's current fence/wall/staircase/gate —
        /// used right before rebuilding them for a merged, bigger
        /// footprint.
        private void ClearRimStructures(string roomId)
        {
            if (!_rimStructures.TryGetValue(roomId, out var structures))
            {
                return;
            }

            foreach (var go in structures)
            {
                if (go != null)
                {
                    _rimFullTransforms.Remove(go);
                    _bottomPivotHalfWallStructures.Remove(go);
                    Destroy(go);
                }
            }
            structures.Clear();
        }

        /// Toggles "half wall" display mode for every placed Jail — see the
        /// _halfWalls field comment. Squashes each pit's rim wall and fence
        /// rail to half height about the pit floor (tops pressed down),
        /// leaving the staircase and gate posts alone so the entrance still
        /// reads as reaching ground level. Wired to BottomMenuBar's Settings
        /// menu alongside DungeonGrid.SetHalfWalls.
        public void SetHalfWalls(bool enabled)
        {
            if (_halfWalls == enabled)
            {
                return;
            }

            _halfWalls = enabled;

            foreach (var structures in _rimStructures.Values)
            {
                foreach (var go in structures)
                {
                    if (go != null && IsHalfWallStructure(go))
                    {
                        ApplyHalfWallState(go);
                    }
                }
            }
        }

        /// Only the rim wall and fence rail follow the half-wall toggle —
        /// the staircase/gate-post containers (JailStair_/JailGate_) are
        /// left full height.
        private static bool IsHalfWallStructure(GameObject go)
        {
            return go.name.StartsWith("JailRimWall_") || go.name.StartsWith("JailFence_");
        }

        /// Sets go to its half-height or full-height transform for the
        /// current _halfWalls state, scaling about the pit floor. Records
        /// the structure's original (full) world position + local scale on
        /// first sight so repeated toggles always rebuild from the true
        /// values rather than compounding. Called both by SetHalfWalls (for
        /// structures that already exist) and by the rim builders (for
        /// structures raised while the mode is already on).
        private void ApplyHalfWallState(GameObject go)
        {
            if (!_rimFullTransforms.TryGetValue(go, out var full))
            {
                full = (go.transform.position, go.transform.localScale);
                _rimFullTransforms[go] = full;
            }

            if (!_halfWalls)
            {
                go.transform.position = full.Position;
                go.transform.localScale = full.Scale;
                return;
            }

            if (_bottomPivotHalfWallStructures.Contains(go))
            {
                // Bottom-pivoted real mesh (see this field's own header
                // above) — squashing by scale alone keeps its bottom
                // exactly at full.Position, unlike the center-pivoted
                // fallback box below, which needs its position pulled
                // toward the pit floor too to keep ITS OWN bottom fixed
                // while its center (the actual pivot) comes down.
                go.transform.position = full.Position;
                go.transform.localScale = new Vector3(full.Scale.x, full.Scale.y * HalfWallHeightScale, full.Scale.z);
                return;
            }

            var pitFloorY = _grid.FloorSurfaceY - PitDepth;
            var pos = full.Position;
            pos.y = pitFloorY + (pos.y - pitFloorY) * HalfWallHeightScale;
            go.transform.position = pos;
            go.transform.localScale = new Vector3(full.Scale.x, full.Scale.y * HalfWallHeightScale, full.Scale.z);
        }

        /// Whether footprint could become a Jail right now. Unlike every
        /// other room's CanPlaceFootprint (which funnels entirely through
        /// DungeonGrid.CanBuildRoomOn), an undug Rock tile passes too — it
        /// gets dug and claimed as part of placement instead of needing
        /// to be pre-dug.
        private bool CanPlaceFootprint(List<Vector2Int> footprint)
        {
            foreach (var coord in footprint)
            {
                if (!_grid.InBounds(coord))
                {
                    return false;
                }

                var tile = _grid.GetTile(coord);
                if (tile.HasRoom)
                {
                    return false;
                }

                if (tile.Type == TileType.Rock)
                {
                    continue;
                }

                if (!_grid.CanBuildRoomOn(coord, _ownerId))
                {
                    return false;
                }
            }

            return true;
        }

        /// LairManager.RoomSold fires for every sold room — only react to
        /// our own (by roomId prefix, same convention every other room
        /// manager uses).
        private void OnRoomSold(string roomId)
        {
            if (!_roomTiles.TryGetValue(roomId, out var tiles))
            {
                return;
            }
            _roomTiles.Remove(roomId);

            foreach (var coord in tiles)
            {
                // TrySellRoom leaves the tile Claimed Floor, "ready to
                // build something else on" — reset the pit sink so
                // whatever gets built there next (or the bare floor
                // itself) doesn't inherit a leftover sunken tile.
                _grid.SetPitDepth(coord, 0f);

                // A prisoner held on a sold tile is simply lost, same
                // "stored contents don't get refunded" convention
                // Treasury's gold and Tavern's bacon already follow.
                if (_prisoners.ContainsKey(coord))
                {
                    ReleasePrisonerAt(coord);
                }

                if (_floorVisuals.TryGetValue(coord, out var floor) && floor != null)
                {
                    Destroy(floor);
                }
                _floorVisuals.Remove(coord);

                if (_ringFloorVisuals.TryGetValue(coord, out var grate) && grate != null)
                {
                    Destroy(grate);
                }
                _ringFloorVisuals.Remove(coord);
            }

            ClearRimStructures(roomId);
            _rimStructures.Remove(roomId);
            _pitTilesByRoom.Remove(roomId);
        }

        /// A random reachable pit tile (any placed Jail's) — read by
        /// MazeRattlerAgent's haunting behavior ("go haunt the prisoners in
        /// the jail"), same flat unweighted-random-pick shape
        /// GremlinAgent.TryBeginRoam uses for its own "wander to a random
        /// reachable floor tile" fallback.
        public bool TryFindRandomPitTile(Vector2Int fromCoord, out Vector2Int targetCoord)
        {
            var distances = _grid.GetReachableFloorDistances(fromCoord);
            var candidates = new List<Vector2Int>();

            foreach (var pitTiles in _pitTilesByRoom.Values)
            {
                foreach (var coord in pitTiles)
                {
                    if (distances.ContainsKey(coord))
                    {
                        candidates.Add(coord);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                targetCoord = default;
                return false;
            }

            targetCoord = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            return true;
        }

        /// A real dungeon_pack-textured gravel floor (falls back to a flat
        /// dirt-colored slab if _floorGravelMaterial failed to load)
        /// sitting flush on top of DungeonGrid's own floor cube for coord
        /// (which stays the shared purple HasRoom color underneath, same
        /// as every other room) — grounded at the sunk pit level
        /// (FloorSurfaceY - PitDepth), not ordinary FloorSurfaceY, so it
        /// lands on the actual pit floor rather than floating at the
        /// surrounding ground's height. A full-cell gray Seam sits
        /// beneath it (see its own field header) so the gap this floor's
        /// own 0.95 footprint would otherwise leave at every tile edge
        /// reads as a mortar line instead of a void gap showing
        /// DungeonGrid's own floor cube through.
        private GameObject BuildDirtFloorVisual(Vector2Int coord)
        {
            var container = new GameObject($"JailDirtFloor_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * (0.5f + PitDepth);

            var seam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seam.name = "Seam";
            seam.transform.SetParent(container.transform, false);
            seam.transform.position = basePosition;
            seam.transform.localScale = new Vector3(cellSize * SeamFootprintScale, SeamHeight, cellSize * SeamFootprintScale);
            Prims.Tint(seam, _seamColor);
            Destroy(seam.GetComponent<Collider>());

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.SetParent(container.transform, false);
            floor.transform.position = basePosition;
            floor.transform.localScale = new Vector3(cellSize * DirtFloorFootprintScale, DirtFloorHeight, cellSize * DirtFloorFootprintScale);
            if (_floorGravelMaterial != null)
            {
                // Shared, pre-built material (see DungeonPackRoomArt.
                // BuildMaterial) — no color tint, the gravel art already
                // carries its own correct look.
                floor.GetComponent<Renderer>().sharedMaterial = _floorGravelMaterial;
            }
            else
            {
                Prims.Tint(floor, _dirtFloorColor);
            }
            Destroy(floor.GetComponent<Collider>());

            return container;
        }

        /// The ring's own floor treatment — the real dungeon_pack grate
        /// floor texture (falls back to the original black panel with a
        /// light gray plus/cross centered on it, arms reaching to the
        /// middle of each of the tile's four sides, if _floorGrateMaterial
        /// failed to load). Sits flush on top of DungeonGrid's own floor
        /// cube at ordinary FloorSurfaceY — the ring is never sunk, unlike
        /// BuildDirtFloorVisual's pit tiles. Same full-cell gray Seam
        /// underneath as BuildDirtFloorVisual, for the same reason.
        private GameObject BuildGrateFloorVisual(Vector2Int coord)
        {
            var container = new GameObject($"JailGrateFloor_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            var cellSize = _grid.CellSize;
            var basePosition = _grid.GridToWorld(coord) + Vector3.down * 0.5f;

            var seam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seam.name = "Seam";
            seam.transform.SetParent(container.transform, false);
            seam.transform.position = basePosition;
            seam.transform.localScale = new Vector3(cellSize * SeamFootprintScale, SeamHeight, cellSize * SeamFootprintScale);
            Prims.Tint(seam, _seamColor);
            Destroy(seam.GetComponent<Collider>());

            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "Panel";
            panel.transform.SetParent(container.transform, false);
            panel.transform.position = basePosition;
            panel.transform.localScale = new Vector3(cellSize * GrateFloorFootprintScale, GrateFloorHeight, cellSize * GrateFloorFootprintScale);
            if (_floorGrateMaterial != null)
            {
                // Shared, pre-built material (see DungeonPackRoomArt.
                // BuildMaterial) — no color tint, the grate art already
                // carries its own correct look.
                panel.GetComponent<Renderer>().sharedMaterial = _floorGrateMaterial;
                Destroy(panel.GetComponent<Collider>());
                return container;
            }

            Prims.Tint(panel, _rimWallColor);
            Destroy(panel.GetComponent<Collider>());

            // The cross sits proud of the panel (raised further up by
            // GrateCrossReliefOffset) so it never coplanar-z-fights with
            // the panel beneath it. Fallback-only, same as the panel's
            // own flat color above.
            var crossY = basePosition.y + GrateFloorHeight * 0.5f + GrateCrossReliefOffset;

            var barX = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barX.name = "CrossX";
            barX.transform.SetParent(container.transform, false);
            barX.transform.position = new Vector3(basePosition.x, crossY, basePosition.z);
            barX.transform.localScale = new Vector3(cellSize, GrateCrossBarThickness, GrateCrossBarThickness);
            Prims.Tint(barX, _grateCrossColor);
            Destroy(barX.GetComponent<Collider>());

            var barZ = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barZ.name = "CrossZ";
            barZ.transform.SetParent(container.transform, false);
            barZ.transform.position = new Vector3(basePosition.x, crossY, basePosition.z);
            barZ.transform.localScale = new Vector3(GrateCrossBarThickness, GrateCrossBarThickness, cellSize);
            Prims.Tint(barZ, _grateCrossColor);
            Destroy(barZ.GetComponent<Collider>());

            return container;
        }

        /// Every outward-facing edge of coord (a pit tile) gets a short
        /// plain rim wall plus either a fence rail or — for the one
        /// designated gate edge — a staircase down into the pit flanked
        /// by gate posts instead of a fence. An edge whose neighbor is
        /// also a pit tile is an interior tile boundary, not a rim, so it
        /// gets nothing — two pit tiles sitting at the same depth have no
        /// seam to fill. Every other neighbor is one of this room's own
        /// ring tiles (the pit is always inset exactly 1 tile from the
        /// room's outer edge), never a tile outside the room entirely.
        private void BuildRimStructures(Vector2Int coord, HashSet<Vector2Int> pitTiles, Vector2Int gateCoord, List<GameObject> structures)
        {
            foreach (var direction in GridDirections.Cardinal)
            {
                if (pitTiles.Contains(coord + direction))
                {
                    continue;
                }

                structures.Add(BuildRimWallVisual(coord, direction));

                if (coord == gateCoord && direction == Vector2Int.down)
                {
                    structures.Add(BuildStaircaseVisual(coord));
                    structures.Add(BuildGatePostsVisual(coord));
                    continue;
                }

                structures.Add(BuildFenceRailVisual(coord, direction));
            }
        }

        /// The real jail_wall_inside mesh standing right at one outward-
        /// facing edge of coord (falls back to the original plain dark
        /// box below if the prop hasn't been set up yet) — from ordinary
        /// ground level (FloorSurfaceY — the walkway ring's own floor
        /// height, not Rock's taller top face; using Rock's height here
        /// used to poke a visible black slab up above the ring's floor
        /// surface, since the ring is walkable Floor now, not Rock) down
        /// RimWallDepth (2 tile-heights, matching the real mesh's own
        /// natural 2-unit depth exactly — see this class's own field
        /// header) — just enough to close the immediate gap under the
        /// fence without any void showing through below ground, now that
        /// the pit's real rim marker is the light gray fence rather than
        /// a deep textured wall (see this class's own header comment).
        /// Inset to the tile's true edge like the fence rail rather than
        /// covering the tile's own footprint, so it never sits in front
        /// of (and hides) the sunk floor.
        private GameObject BuildRimWallVisual(Vector2Int coord, Vector2Int direction)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var outward = new Vector3(direction.x, 0f, direction.y);
            var basePosition = worldPos + outward * (cellSize * RimWallEdgeInset);
            var isEastWestEdge = direction.x != 0;
            var wallTopY = _grid.FloorSurfaceY;

            GameObject wall;
            if (_jailWallInsidePrefab != null)
            {
                wall = Instantiate(_jailWallInsidePrefab, transform, false);
                // Pivot sits at the mesh's own top (Y=0 = ground, extends
                // down RimWallDepth on its own), so no extra Y offset is
                // needed the way the fallback box below needs (its pivot
                // is at its own center).
                wall.transform.position = new Vector3(basePosition.x, wallTopY, basePosition.z);
                // The mesh's own local X axis is its 1-unit width, already
                // aligned with a north/south-facing edge (direction along
                // Z) — rotate 90 degrees for an east/west-facing edge
                // instead.
                wall.transform.rotation = isEastWestEdge ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
            }
            else
            {
                wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.transform.SetParent(transform, false);
                var centerY = wallTopY - RimWallDepth * 0.5f;
                wall.transform.position = new Vector3(basePosition.x, centerY, basePosition.z);
                wall.transform.localScale = isEastWestEdge
                    ? new Vector3(RimWallThickness, RimWallDepth, cellSize * 0.98f)
                    : new Vector3(cellSize * 0.98f, RimWallDepth, RimWallThickness);
                Prims.Tint(wall, _rimWallColor);
                Destroy(wall.GetComponent<Collider>());
            }

            wall.name = $"JailRimWall_{coord.x}_{coord.y}_{direction.x}_{direction.y}";
            if (_halfWalls)
            {
                ApplyHalfWallState(wall);
            }
            return wall;
        }

        /// The real jail_fence_half mesh along a single outward-facing
        /// edge of coord (falls back to the original plain gray box below
        /// if the prop hasn't been set up yet) — grounded at ordinary
        /// FloorSurfaceY (ground level) regardless of how deep the pit
        /// itself sits, since it's a rim guard rail, not a wall spanning
        /// the drop.
        private GameObject BuildFenceRailVisual(Vector2Int coord, Vector2Int direction)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var edgeOffset = new Vector3(direction.x, 0f, direction.y) * (cellSize * FenceEdgeInset);
            var basePosition = worldPos + edgeOffset;
            var groundY = _grid.FloorSurfaceY;
            // A rail runs along the edge, perpendicular to `direction` —
            // an east/west-facing edge (direction along X) is long in Z,
            // a north/south-facing edge (direction along Z) is long in X.
            var isEastWestEdge = direction.x != 0;

            GameObject rail;
            if (_jailFenceHalfPrefab != null)
            {
                rail = Instantiate(_jailFenceHalfPrefab, transform, false);
                // Pivot sits at the mesh's own bottom (Y=0 = ground
                // level, matching the source .obj's own vertex bounds —
                // y: 0 to 1 — and JAIL_README.txt's own "y = 0 to 1...
                // -> perimeter"), so no extra Y offset is needed: this
                // lands its bottom flush with the gate mesh's own bottom
                // (see BuildGatePostsVisual), confirmed in-Editor. Half-
                // wall mode needs its own bottom-anchored squash instead
                // of the generic center-pivot one — see
                // _bottomPivotHalfWallStructures below.
                rail.transform.position = new Vector3(basePosition.x, groundY, basePosition.z);
                // The mesh's own local X axis is its ~0.95-unit width,
                // already aligned with a north/south-facing edge —
                // rotate 90 degrees for an east/west-facing edge instead,
                // same as the rim wall above.
                rail.transform.rotation = isEastWestEdge ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
                _bottomPivotHalfWallStructures.Add(rail);
            }
            else
            {
                rail = GameObject.CreatePrimitive(PrimitiveType.Cube);
                rail.transform.SetParent(transform, false);
                rail.transform.position = new Vector3(basePosition.x, groundY + FenceRailHeight * 0.5f, basePosition.z);
                rail.transform.localScale = isEastWestEdge
                    ? new Vector3(FenceRailThickness, FenceRailHeight, cellSize * 0.9f)
                    : new Vector3(cellSize * 0.9f, FenceRailHeight, FenceRailThickness);
                Prims.Tint(rail, _fenceColor);
                Destroy(rail.GetComponent<Collider>());
            }

            rail.name = $"JailFence_{coord.x}_{coord.y}_{direction.x}_{direction.y}";
            if (_halfWalls)
            {
                ApplyHalfWallState(rail);
            }
            return rail;
        }

        /// The real jail_stairs_wood mesh descending from ground level
        /// down to the pit floor, running along the tile's south
        /// (Vector2Int.down) edge — the one fixed direction
        /// BuildRimStructures ever calls this for (falls back to the
        /// original 3-ascending-step-cube version below if the prop
        /// hasn't been set up yet).
        private GameObject BuildStaircaseVisual(Vector2Int coord)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);

            if (_jailStairsWoodPrefab != null)
            {
                var stairs = Instantiate(_jailStairsWoodPrefab, transform, false);
                stairs.name = $"JailStair_{coord.x}_{coord.y}";
                // Pivot sits near ground level at the ramp's own top end
                // (local Z=0) — positioned at the tile's true south/
                // outward edge, matching the "right at the tile's true
                // edge" convention BuildRimWallVisual's own inset uses.
                // The mesh's local +Z axis already runs toward the room's
                // interior (matching this hardcoded south-gate case,
                // where +world-Z is "into the room"), so no rotation is
                // needed. The mesh's own authored drop is 2 units (this
                // pack's stated pit depth), but this project's own pit is
                // only PitDepth (1 unit) — Y-only scale down to match, X/Z
                // (the tile-width and the ramp's own run) stay natural
                // scale. Best-guess placement, no in-Editor render to
                // confirm the run reads right at this shallower depth.
                stairs.transform.position = new Vector3(worldPos.x, _grid.FloorSurfaceY, worldPos.z - cellSize * 0.5f);
                stairs.transform.localScale = new Vector3(1f, PitDepth / 2f, 1f);
                return stairs;
            }

            var container = new GameObject($"JailStair_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);
            container.transform.position = worldPos;

            var pitFloorY = _grid.FloorSurfaceY - PitDepth;
            var stepDepth = cellSize / StepCount;

            for (int i = 0; i < StepCount; i++)
            {
                // i=0 is innermost/deepest (pit floor level, toward the
                // room's interior), i=StepCount-1 is outermost/highest
                // (ground level, at the south/gate edge).
                var stepTopY = pitFloorY + PitDepth * (i + 1) / (float)StepCount;
                var stepHeight = stepTopY - pitFloorY;
                var offsetZ = (i - (StepCount - 1) / 2f) * -stepDepth;

                var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
                step.name = $"JailStep_{i}";
                step.transform.SetParent(container.transform, false);
                step.transform.localPosition = new Vector3(0f, pitFloorY + stepHeight * 0.5f, offsetZ);
                step.transform.localScale = new Vector3(cellSize * 0.9f, stepHeight, stepDepth * 0.95f);
                Prims.Tint(step, _stepColor);
                Destroy(step.GetComponent<Collider>());
            }

            return container;
        }

        /// The real jail_gate mesh framing the staircase opening at
        /// coord's south edge (falls back to the original two-post
        /// version below if the prop hasn't been set up yet) — the "gate"
        /// half of "one staircase with a gate." Cosmetic only, same as
        /// the staircase itself: the tile stays fully walkable
        /// underneath.
        private GameObject BuildGatePostsVisual(Vector2Int coord)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var edgeZ = worldPos.z - cellSize * FenceEdgeInset;
            var groundY = _grid.FloorSurfaceY;

            if (_jailGatePrefab != null)
            {
                var gate = Instantiate(_jailGatePrefab, transform, false);
                gate.name = $"JailGate_{coord.x}_{coord.y}";
                // Pivot sits at the mesh's own bottom (Y=0 = ground
                // level) — no extra Y offset needed. Only ever built for
                // the one hardcoded south-facing gate edge (see
                // BuildRimStructures), so no per-direction rotation is
                // needed here.
                gate.transform.position = new Vector3(worldPos.x, groundY, edgeZ);
                return gate;
            }

            var container = new GameObject($"JailGate_{coord.x}_{coord.y}");
            container.transform.SetParent(transform, false);

            foreach (var side in new[] { -1f, 1f })
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                post.name = $"JailGatePost_{side}";
                post.transform.SetParent(container.transform, false);
                post.transform.position = new Vector3(worldPos.x + side * cellSize * 0.4f, groundY + GatePostHeight * 0.5f, edgeZ);
                // Cylinder primitive is 2 units tall at scale 1, so the
                // height scale needed for a world-space length is half
                // that length (same trick TrainingRoomManager.
                // BuildDummyVisual's post uses).
                post.transform.localScale = new Vector3(GatePostRadius * 2f, GatePostHeight * 0.5f, GatePostRadius * 2f);
                Prims.Tint(post, _gatePostColor);
                Destroy(post.GetComponent<Collider>());
            }

            return container;
        }

        public void UpdatePlacementPreview(Vector2Int startCoord, Vector2Int endCoord)
        {
            ClearPlacementPreview();

            var footprint = GetFootprint(startCoord, endCoord, out var width, out var height, out _);
            var isValidSize = width >= MinFootprintSize && height >= MinFootprintSize;
            var color = isValidSize && CanPlaceFootprint(footprint) ? _previewValidColor : _previewInvalidColor;

            foreach (var coord in footprint)
            {
                if (!_grid.InBounds(coord))
                {
                    continue;
                }

                _previewMarkers.Add(CreatePreviewMarker(coord, color));
            }
        }

        public void ClearPlacementPreview()
        {
            foreach (var marker in _previewMarkers)
            {
                if (marker != null)
                {
                    Destroy(marker);
                }
            }
            _previewMarkers.Clear();
        }

        private GameObject CreatePreviewMarker(Vector2Int coord, Color color)
        {
            var cellSize = _grid.CellSize;
            var worldPos = _grid.GridToWorld(coord);
            var centerY = GetGroundTopY(coord) + PreviewClearance + PreviewHeight * 0.5f;

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = $"JailPreview_{coord.x}_{coord.y}";
            marker.transform.SetParent(transform, false);
            marker.transform.localPosition = new Vector3(worldPos.x, centerY, worldPos.z);
            marker.transform.localScale = new Vector3(cellSize * PreviewFootprintScale, PreviewHeight, cellSize * PreviewFootprintScale);
            Prims.Tint(marker, color);
            Destroy(marker.GetComponent<Collider>());
            return marker;
        }

        private float GetGroundTopY(Vector2Int coord)
        {
            return _grid.GetTile(coord).Type == TileType.Rock ? RockTopY : _grid.FloorSurfaceY;
        }

        private List<Vector2Int> GetFootprint(Vector2Int startCoord, Vector2Int endCoord, out int width, out int height, out Vector2Int origin)
        {
            var minX = Mathf.Min(startCoord.x, endCoord.x);
            var maxX = Mathf.Max(startCoord.x, endCoord.x);
            var minY = Mathf.Min(startCoord.y, endCoord.y);
            var maxY = Mathf.Max(startCoord.y, endCoord.y);

            width = maxX - minX + 1;
            height = maxY - minY + 1;
            origin = new Vector2Int(minX, minY);

            var footprint = new List<Vector2Int>(width * height);
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    footprint.Add(new Vector2Int(x, y));
                }
            }

            return footprint;
        }
    }
}

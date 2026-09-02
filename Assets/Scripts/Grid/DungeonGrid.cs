using System;
using System.Collections.Generic;
using UnityEngine;

namespace KeepersDomain.Grid
{
    /// Owns tile data and their placeholder visuals. Isometric look comes entirely
    /// from the camera angle (see CameraControl/IsoCameraController) — the grid
    /// itself is a plain XZ plane, which keeps dig/territory/room logic 2D and simple.
    public class DungeonGrid : MonoBehaviour
    {
        /// Each room manager is per-player now (one per KeeperContext) and
        /// mints roomIds from its own `_nextRoomId` counter. Seeding that
        /// counter at `ownerId * RoomIdOwnerStride` keeps every player's ids
        /// in a disjoint band ("Lair_2000003" etc.) so one keeper selling a
        /// room can never resolve to — and tear down — another keeper's
        /// tiles via the shared roomId → tile map. The "{Type}_{n}" shape is
        /// unchanged, so LairManager.GetCostPerTileForRoomId's StartsWith
        /// and RoomReconstruction.ResolveRoomManager's LastIndexOf('_') both
        /// still parse it.
        public const int RoomIdOwnerStride = 1_000_000;

        [SerializeField] private int _width = 24;
        [SerializeField] private int _height = 24;
        [SerializeField] private float _cellSize = 1f;

        [SerializeField] private Color _rockColor = new Color(0.35f, 0.32f, 0.3f);
        // Was a near-black tint from back when Reinforced was just a
        // flat-colored cube and needed a dark hue of its own to read as
        // distinct from plain Rock. Now that it's the dungeon_pack's own
        // grey-brick mesh (which already reads as visually distinct on
        // its own), that same dark tint was instead crushing the brick
        // texture toward black — lightened to near-neutral so the
        // texture's own gray shows through, while still leaving headroom
        // for the damage-lerp tint to read clearly against it.
        [SerializeField] private Color _rockReinforcedColor = new Color(0.85f, 0.85f, 0.85f);

        /// Tints the Reinforced wall mesh's glowing orb sub-part (see
        /// ApplyTint) — same placeholder-blue value as ThroneRoom's own
        /// _playerColor field, kept in sync manually since there's no
        /// real per-player color selection system yet for either to read
        /// from instead (both are marked as stand-ins for one).
        [SerializeField] private Color _playerColor = new Color(0.25f, 0.55f, 0.95f);

        /// Bedrock — darker than a reinforced wall, per the brief.
        [SerializeField] private Color _bedrockColor = new Color(0.07f, 0.06f, 0.055f);

        [SerializeField] private Color _rockDamagedColor = new Color(0.25f, 0.12f, 0.1f);
        [SerializeField] private Color _rockUnreachableColor = new Color(0.2f, 0.28f, 0.38f);
        [SerializeField] private Color _floorUnclaimedColor = new Color(0.2f, 0.18f, 0.15f);
        [SerializeField] private Color _floorClaimedColor = new Color(0.25f, 0.4f, 0.25f);
        [SerializeField] private Color _roomColor = new Color(0.55f, 0.15f, 0.5f);
        [SerializeField] private Color _roomDamagedColor = new Color(0.2f, 0.05f, 0.18f);
        [SerializeField] private Color _goldWallColor = new Color(0.5f, 0.42f, 0.2f);
        [SerializeField] private Color _regeneratingGoldWallColor = new Color(0.55f, 0.45f, 0.15f);
        [SerializeField] private Color _manaCrystalWallColor = new Color(0.15f, 0.5f, 0.55f);
        [SerializeField] private Color _goldNuggetColor = new Color(0.85f, 0.7f, 0.15f);
        [SerializeField] private Color _regeneratingGoldNuggetColor = new Color(0.95f, 0.8f, 0.2f);

        // New terrain tiles (see TileType/SetTerrainFeature) — placed today
        // by a dev-only Build-menu tool pending a real map generator.
        [SerializeField] private Color _waterColor = new Color(0.15f, 0.35f, 0.75f);
        [SerializeField] private Color _lavaColor = new Color(0.8f, 0.3f, 0.1f);
        [SerializeField] private Color _chasmColor = new Color(0.03f, 0.03f, 0.04f);
        [SerializeField] private Color _chasmSpikeColor = new Color(0.25f, 0.23f, 0.22f);
        [SerializeField] private Color _holyGroundColor = new Color(0.92f, 0.92f, 0.88f);
        [SerializeField] private Color _holyGroundStarColor = new Color(0.85f, 0.7f, 0.15f);

        // "Make it as deep as the Jail" — same one-full-grid-level sink
        // JailManager's own PitDepth constant uses (DungeonGrid.SetPitDepth
        // is a render-only offset either way, see its own header).
        private const float ChasmPitDepth = 1f;
        private const int ChasmSpikeCount = 4;
        private const float ChasmSpikeHeight = 0.5f;
        private const float ChasmSpikeRadius = 0.06f;

        // An 8-pointed star — 4 bars through the tile's center, each
        // already double-ended (0°/180°, 45°/225°, ...), so 4 bars at
        // 0/45/90/135 degrees give all 8 points. Same cheap primitives-only
        // placeholder convention every other decoration in this class uses
        // (see RebuildWallDecoration's gold nuggets, BuildChasmSpikes).
        private static readonly float[] HolyGroundStarAngles = { 0f, 45f, 90f, 135f };
        private const float HolyGroundStarLength = 0.75f;
        private const float HolyGroundStarThickness = 0.06f;

        // Sits proud of the floor tile beneath it (like Jail's grate
        // cross), so it never coplanar-z-fights with it.
        private const float HolyGroundStarReliefOffset = 0.02f;

        // "add some gold in a random pattern" — RegeneratingGoldWall gets
        // more nuggets than plain GoldWall so it visually reads as the
        // richer, gold-heavier vein ("switch the amount of gold & rock").
        private const int GoldNuggetCount = 5;
        private const int RegeneratingGoldNuggetCount = 10;

        private TileState[,] _tiles;

        // Stable per-tile anchor, positioned once via GridToWorld and never
        // moved again — decorations (RebuildWallDecoration, below) parent
        // under this. The tile's actual geometry (flat colored cube, or a
        // dungeon_pack wall mesh — see GetWallMeshPrefab) lives in
        // _visualChildren as a single swappable child, so it can change
        // (dug out, reinforced toggled, resource type changed, ...)
        // without disturbing decorations parented alongside it.
        private GameObject[,] _visuals;
        private GameObject[,] _visualChildren;

        // Parallel to _visualChildren — which prefab (if any) the current
        // child was instantiated from; null means it's the plain flat
        // cube. Lets RefreshVisual tell whether it needs to destroy/
        // recreate the child (the target prefab actually changed — e.g.
        // Gold -> RegeneratingGold, not just any two mesh tiles) or just
        // re-tint the existing one (e.g. a damage tick), since RefreshVisual
        // fires on every single dig-damage hit.
        private GameObject[,] _currentWallPrefab;

        // Loaded once from Resources (DungeonGrid is built entirely
        // procedurally by GameBootstrap — there's no scene object to
        // hand-wire these references onto). Null is a valid, supported
        // state per field: a tile whose corresponding mesh isn't loaded
        // just falls back to the plain colored cube every other tile type
        // already uses, rather than throwing (see GetWallMeshPrefab).
        private GameObject _wallMeshStone;
        private GameObject _wallMeshGold;
        private GameObject _wallMeshGoldRegen;
        private GameObject _wallMeshManaCrystal;
        private GameObject _wallMeshBedrock;
        private GameObject _wallMeshReinforced;

        // Same graceful-fallback rule as the wall meshes above — null
        // just means Water/Lava keep the plain colored cube every other
        // non-mesh tile type uses. Unlike walls these need no
        // scale/margin correction at all: the dungeon_pack tiles are
        // already an exact 1x1 quad, and (unlike a wall's height) there's
        // no vertical dimension to preserve either.
        private GameObject _waterMesh;
        private GameObject _lavaMesh;

        // Plain Claimed/Unclaimed floor tiles are still the same primitive
        // cube every non-mesh tile uses, just textured now (see
        // RefreshVisual's isPlainFloor branch) instead of flat-colored —
        // no mesh import involved, so no catalog of prefabs like the wall
        // fields above. _plainFloorMaterial is what every other non-mesh
        // case (rooms, water/lava/chasm/holy ground, build-queued) keeps
        // using — untextured, tinted flat via ApplyTint exactly like
        // before, just via a shared material instead of the auto-instanced
        // one .material used to hand back. _claimedTileTextures holds the
        // 4 paved-floor variants DungeonGrid picks between per-tile (seeded
        // by coord, see ApplyTint's baseMapOverride) so a large claimed
        // territory doesn't read as one obviously repeating texture.
        private Material _plainFloorMaterial;
        private Material _floorUnclaimedMaterial;
        private Material _floorClaimedMaterial;
        private Texture2D[] _claimedTileTextures;

        // A full-cell dark-gray slab set just under each Claimed floor tile,
        // showing through the ~5% gap the 0.95-scale floor cube leaves on
        // every side so a paved area reads as grouted tiles instead of
        // cubes hovering over a void — same trick JailManager's "Seam" runs
        // under its room-floor panels. Parallel to _visuals; only ever
        // non-null for a plain Claimed floor tile (see UpdateFloorGrout).
        [SerializeField] private Color _claimedGroutColor = new Color(0.16f, 0.16f, 0.17f);
        private GameObject[,] _floorGrout;

        /// Which floating icon (if any) a queued Rock/Floor tile shows —
        /// replaces the old flat queued-color tint (see RefreshVisual's
        /// Rock/Floor color branches) so the wall/floor itself just reads
        /// as its ordinary type color, with the pending action called out
        /// by a small icon on top instead.
        private enum QueuedIcon
        {
            None,
            Pickaxe,
            Shield,
            Hammer
        }

        // Parallel to _visuals — the current queued-action icon (if any),
        // and which kind it is, so UpdateQueuedActionIcon only rebuilds
        // when that actually changes rather than every RefreshVisual call
        // (which fires per dig-damage hit).
        private GameObject[,] _queuedActionIcons;
        private QueuedIcon[,] _queuedActionIconKind;

        // Floats the Mine/Reinforce icon just above a wall's peak
        // (dungeon_pack wall meshes stand ~2.03 units tall on a 1-unit
        // cell — see DungeonPackWallSetup's bounds log — based at
        // floorSurfaceY - 0.5, i.e. topping out around 1.53).
        private const float QueuedIconFloatHeight = 1.75f;

        // dungeon_pack wall meshes are pivoted at their base (min Y ≈ 0) —
        // see DungeonPackWallSetup's bounds log — and RefreshVisual seats
        // that base half a unit below the tile centre so it sits flush with
        // the floor. "Half wall" mode then scales height about this base.
        private const float WallBaseLocalY = -0.5f;

        // "Half wall" display mode (see SetHalfWalls, wired to BottomMenuBar's
        // Settings menu): every wall mesh is squashed to half height on Y
        // about its base, so the bottom half stays put and the top is pressed
        // down to the midpoint — a see-over view that changes nothing about
        // the walls themselves. Purely cosmetic.
        private bool _halfWalls;

        // The Construct-wall frame stands roughly where the future wall
        // will rise, base at the floor surface rather than floating high
        // like the Mine/Reinforce icons — see BuildConstructIcon.
        private const float ConstructFrameWidth = 0.8f;
        private const float ConstructFrameHeight = 1.6f;
        private const float ConstructFrameBarThickness = 0.05f;

        /// The wall tile currently selected (see SetSelectedWall) — null
        /// when nothing is. Only ever one at a time; _selectionOutline is
        /// the single "inverted hull" visual for it (see SetSelectedWall).
        private Vector2Int? _selectedWallCoord;
        private GameObject _selectionOutline;
        private Material _selectionOutlineMaterial;
        private const float SelectionOutlineScale = 1.04f;

        private static MaterialPropertyBlock _sharedPropertyBlock;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");

        // Parallel to _visuals — small decorative child objects (currently
        // just gold nuggets) parented to a tile's cube, built once when a
        // wall becomes a resource type (not on every RefreshVisual, which
        // fires every hit and would otherwise re-randomize/flicker them)
        // and cleared once the tile stops being Rock at all.
        private GameObject[,] _wallDecorations;

        public int Width => _width;
        public int Height => _height;
        public float CellSize => _cellSize;

        /// Convenience single-owner setter for ordinary (non-Level-
        /// Designer) gameplay, where there's exactly one implicit player
        /// and TileState.OwnerId is never explicitly set (stays at its
        /// struct default, 0) — wraps that one color as OwnerColors[0],
        /// the same per-owner array RefreshVisual's Claimed-floor tint and
        /// a Reinforced wall's orb coloring (see ApplyOrbOwnerColor) both
        /// read from, so GameBootstrap.BuildWorld's one call site
        /// (`grid.PlayerColor = Color.green`) needs no change even though
        /// a Reinforced wall's orb is no longer one material mutated
        /// globally for the whole grid — it's now looked up per-tile by
        /// owner, the same mechanism the Level Designer's multi-player
        /// case already used for Claimed floor. RefreshAllVisuals so every
        /// already-placed Reinforced wall (not just ones painted from now
        /// on) picks up the new color immediately.
        public Color PlayerColor
        {
            get => _playerColor;
            set
            {
                _playerColor = value;
                OwnerColors = new[] { value };
                RefreshAllVisuals();
            }
        }

        /// Per-owner colors, indexed by TileState.OwnerId — used
        /// unconditionally by a Reinforced wall's glowing orb (see
        /// ApplyOrbOwnerColor, always needs *some* color) and, only when
        /// TintFloorByOwner is also set, to additionally tint Claimed
        /// floor (see RefreshVisual). Populated either by PlayerColor's
        /// own convenience setter (ordinary single-player gameplay, one
        /// entry at index 0) or by the Level Designer's
        /// LevelDesignerSession.RefreshGridOwnerColors (one entry per
        /// player) — renamed from EditorOwnerColors since it's no longer
        /// Level-Designer-exclusive. Null only before either has ever run.
        public Color[] OwnerColors { get; set; }

        /// Gates Claimed-floor owner-tinting specifically (RefreshVisual)
        /// — separate from OwnerColors itself, which a Reinforced wall's
        /// orb always uses regardless of this flag. Only
        /// LevelDesignerSession.RefreshGridOwnerColors ever sets this
        /// true; PlayerColor's own convenience setter (ordinary gameplay)
        /// deliberately leaves it false, so gameplay's Claimed floor
        /// (e.g. the Throne Room/Portal footprints, which CarveRoom already
        /// claims with OwnerId 0) keeps rendering the plain, untinted
        /// claimed color exactly as before — populating OwnerColors for
        /// the orb's sake must not, by itself, start tinting floor that
        /// never opted into per-owner coloring.
        public bool TintFloorByOwner { get; set; }

        // One material instance per owner (plus one at key -1 for "no
        // owner"/fallback), lazily created and reused/recolored in place
        // across every Reinforced wall tile that owner occupies — see
        // GetOwnerOrbMaterial/ApplyOrbOwnerColor. Never per-tile, so this
        // stays exactly as cheap (draw-call/batching-wise) per owner as
        // the old single-shared-material-for-the-whole-grid approach was.
        private readonly Dictionary<int, Material> _reinforcedOrbMaterialsByOwner = new Dictionary<int, Material>();
        private Material _reinforcedOrbTemplateMaterial;

        /// The color a Reinforced wall's orb should show for ownerId —
        /// looked up in OwnerColors when it's a valid index, otherwise the
        /// same single-owner _playerColor fallback (the old placeholder
        /// blue default) an unowned/-1 Reinforced wall (e.g. one placed in
        /// the Level Designer with no owner selected) falls back to.
        private Color ResolveOwnerColor(int ownerId)
        {
            return OwnerColors != null && ownerId >= 0 && ownerId < OwnerColors.Length
                ? OwnerColors[ownerId]
                : _playerColor;
        }

        /// Public read of the same per-owner color a Reinforced wall's orb
        /// uses — for anything outside DungeonGrid that needs to tint by
        /// owner (see CreatureHealthRing). Out-of-range / -1 falls back to
        /// the single-player color, so ordinary gameplay (one implicit
        /// owner 0) just gets PlayerColor.
        public Color GetOwnerColor(int ownerId) => ResolveOwnerColor(ownerId);

        /// The (cached, reused) orb material for ownerId, recolored in
        /// place to ResolveOwnerColor(ownerId) on every call so a color
        /// change (PlayerColor's setter, or the Level Designer's
        /// RefreshGridOwnerColors) retints every tile sharing that owner's
        /// material without needing to touch each tile's renderer again.
        private Material GetOwnerOrbMaterial(int ownerId)
        {
            var color = ResolveOwnerColor(ownerId);
            if (_reinforcedOrbMaterialsByOwner.TryGetValue(ownerId, out var cached))
            {
                cached.SetColor(BaseColorId, color);
                cached.SetColor(EmissionColorId, color);
                return cached;
            }

            var template = FindReinforcedOrbTemplate();
            if (template == null)
            {
                return null;
            }

            var material = new Material(template) { name = "M_ReinforcedOrb" };
            material.SetColor(BaseColorId, color);
            material.SetColor(EmissionColorId, color);
            _reinforcedOrbMaterialsByOwner[ownerId] = material;
            return material;
        }

        /// M_ReinforcedOrb by name (not slot index — index order isn't
        /// worth relying on) among _wallMeshReinforced's own original
        /// materials — the un-owner-tinted template every owner-specific
        /// clone (see GetOwnerOrbMaterial) is cloned from, found once and
        /// cached. Safe to call before _wallMeshReinforced has loaded
        /// (Resources.Load can run after this in Initialize, or the
        /// prefab can simply be missing) — returns null until it exists.
        private Material FindReinforcedOrbTemplate()
        {
            if (_reinforcedOrbTemplateMaterial != null)
            {
                return _reinforcedOrbTemplateMaterial;
            }

            if (_wallMeshReinforced == null)
            {
                return null;
            }

            foreach (var renderer in _wallMeshReinforced.GetComponentsInChildren<Renderer>())
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material != null && material.name == "M_ReinforcedOrb")
                    {
                        _reinforcedOrbTemplateMaterial = material;
                        return material;
                    }
                }
            }

            return null;
        }

        /// Swaps a Reinforced wall instance's orb material slot (found by
        /// name, same reasoning as FindReinforcedOrbTemplate) to
        /// ownerId's own material — called every RefreshVisual for a
        /// Reinforced tile (not just when its mesh is freshly
        /// instantiated), since the owner can change without the mesh
        /// itself needing to rebuild (see the Level Designer's edit-mode
        /// reassignment). Reassigning the same already-correct material
        /// reference when nothing changed is a harmless no-op.
        private void ApplyOrbOwnerColor(GameObject visualChild, int ownerId)
        {
            var orbMaterial = GetOwnerOrbMaterial(ownerId);
            if (orbMaterial == null)
            {
                return;
            }

            foreach (var renderer in visualChild.GetComponentsInChildren<Renderer>())
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null && materials[i].name == "M_ReinforcedOrb")
                    {
                        materials[i] = orbMaterial;
                        changed = true;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = materials;
                }
            }
        }

        /// World-space Y of a dug (Floor) tile's top surface. Floor tiles sit
        /// with their center at y=-0.5 and a height of 0.15 (see RefreshVisual)
        /// so they read as "excavated" against Rock's raised full-height
        /// block — the visible ground is not y=0. Anything meant to sit flush
        /// on the floor (e.g. Portal's staircase) should be grounded here
        /// rather than assuming y=0.
        public float FloorSurfaceY => -0.5f + 0.15f * 0.5f;

        // The int on the request/claim/damage events is the owning player
        // (see RequestDig et al., ClaimTile, TileState.OwnerId) — every
        // BuilderJobBoard is per-player now (one KeeperContext each) and
        // early-returns on a mismatch, so a dig queued in P1's territory
        // never lands on P2's job board. Cancel/TileChanged stay coord-only:
        // a cancel broadcast is a harmless no-op on any board that doesn't
        // track that tile, so there's nothing to route.
        public event Action<Vector2Int, int> DigRequested;
        public event Action<Vector2Int> DigCanceled;
        public event Action<Vector2Int, int> ReinforceRequested;
        public event Action<Vector2Int> ReinforceCanceled;
        public event Action<Vector2Int, int> BuildRequested;
        public event Action<Vector2Int> BuildCanceled;
        public event Action<Vector2Int> TileChanged;

        /// Fired when a Rock tile finishes digging out as Unclaimed floor —
        /// i.e. always, since digging no longer auto-claims by proximity to
        /// the portal. BuilderJobBoard listens for this to queue a claim job.
        /// The int is the digger's owner — the claim job is queued on that
        /// player's board (and only actually claimed if it borders that
        /// player's own frontier, see BuilderJobBoard.TryClaimClaimJob).
        public event Action<Vector2Int, int> FloorNeedsClaim;

        /// Fired whenever a room tile takes damage and survives (see
        /// ApplyRoomDamage) — not fired on the hit that destroys it, since
        /// at that point the whole room is about to be torn down rather
        /// than needing a repair job. BuilderJobBoard listens for this to
        /// queue a repair job, the same way it listens to FloorNeedsClaim.
        /// The int is the room tile's own owner — repairing a damaged room
        /// is that player's job, not the attacker's.
        public event Action<Vector2Int, int> RoomDamaged;

        public void Initialize(int width, int height, float cellSize)
        {
            _width = width;
            _height = height;
            _cellSize = cellSize;

            _tiles = new TileState[_width, _height];
            _visuals = new GameObject[_width, _height];
            _visualChildren = new GameObject[_width, _height];
            _currentWallPrefab = new GameObject[_width, _height];
            _wallDecorations = new GameObject[_width, _height];
            _wallMeshStone = Resources.Load<GameObject>("Dungeon/Wall_Stone");
            _wallMeshGold = Resources.Load<GameObject>("Dungeon/Wall_Gold");
            _wallMeshGoldRegen = Resources.Load<GameObject>("Dungeon/Wall_GoldRegen");
            _wallMeshManaCrystal = Resources.Load<GameObject>("Dungeon/Wall_ManaCrystal");
            _wallMeshBedrock = Resources.Load<GameObject>("Dungeon/Wall_Bedrock");
            _wallMeshReinforced = Resources.Load<GameObject>("Dungeon/Wall_Reinforced");
            _waterMesh = Resources.Load<GameObject>("Dungeon/Tile_Water");
            _lavaMesh = Resources.Load<GameObject>("Dungeon/Tile_Lava");
            _plainFloorMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _floorUnclaimedMaterial = Resources.Load<Material>("Dungeon/Floors/M_FloorUnclaimed");
            _floorClaimedMaterial = Resources.Load<Material>("Dungeon/Floors/M_FloorClaimed");
            _claimedTileTextures = new[]
            {
                Resources.Load<Texture2D>("Dungeon/Floors/claimed_tile_1"),
                Resources.Load<Texture2D>("Dungeon/Floors/claimed_tile_2"),
                Resources.Load<Texture2D>("Dungeon/Floors/claimed_tile_3"),
                Resources.Load<Texture2D>("Dungeon/Floors/claimed_tile_4"),
            };
            _queuedActionIcons = new GameObject[_width, _height];
            _queuedActionIconKind = new QueuedIcon[_width, _height];
            _floorGrout = new GameObject[_width, _height];

            _selectionOutlineMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            _selectionOutlineMaterial.SetColor(BaseColorId, Color.yellow);
            // Front-face culled so only the back faces of the scaled-up
            // duplicate (see SetSelectedWall) show — the classic
            // "inverted hull" outline trick, since this project has no
            // custom render-feature/outline shader to reach for instead.
            _selectionOutlineMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Front);

            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    _tiles[x, y] = TileState.Rock;
                }
            }

            BuildAllVisuals();
        }

        /// Carves a square room (halfSize=0 carves just the single center
        /// tile — handy for a one-tile corridor) as Floor+Claimed. Used for
        /// the fixed starting rooms (Throne Room, Portal room, the corridor
        /// between them) built directly by GameBootstrap, as opposed to
        /// tiles dug out during play. isBuildable defaults to true for
        /// ordinary rooms; GameBootstrap passes false for rooms that already
        /// have their own fixed structure, so a Lair can't be placed on them.
        public void CarveRoom(Vector2Int center, int halfSize, bool isBuildable = true, int ownerId = 0)
        {
            for (int x = -halfSize; x <= halfSize; x++)
            {
                for (int y = -halfSize; y <= halfSize; y++)
                {
                    var coord = center + new Vector2Int(x, y);
                    if (!InBounds(coord))
                    {
                        continue;
                    }

                    _tiles[coord.x, coord.y].Type = TileType.Floor;
                    _tiles[coord.x, coord.y].Ownership = TileOwnership.Claimed;
                    _tiles[coord.x, coord.y].OwnerId = ownerId;
                    _tiles[coord.x, coord.y].IsBuildable = isBuildable;
                    RefreshVisual(coord);
                }
            }
        }

        /// Same idea as CarveRoom (Floor+Claimed), but width x height from
        /// origin (its min-corner, not a center) rather than a symmetric
        /// halfSize — CarveRoom's (2*halfSize+1) span can only ever produce
        /// odd dimensions, so this is what an even-sized room (e.g. a 4x4)
        /// needs instead.
        public void CarveRect(Vector2Int origin, int width, int height, bool isBuildable = true, int ownerId = 0)
        {
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    var coord = origin + new Vector2Int(x, y);
                    if (!InBounds(coord))
                    {
                        continue;
                    }

                    _tiles[coord.x, coord.y].Type = TileType.Floor;
                    _tiles[coord.x, coord.y].Ownership = TileOwnership.Claimed;
                    _tiles[coord.x, coord.y].OwnerId = ownerId;
                    _tiles[coord.x, coord.y].IsBuildable = isBuildable;
                    RefreshVisual(coord);
                }
            }
        }

        /// Marks an as-yet-undug Rock tile as a resource vein (see
        /// WallResourceType) — a level-generation-time operation
        /// (GameBootstrap's resource scatter pass), not a player action, so
        /// it only bothers guarding against a non-Rock target. Resets Hp to
        /// the new type's max and (re)builds the tile's decorative visual.
        public void SetWallResourceType(Vector2Int coord, WallResourceType wallResourceType)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock)
            {
                return;
            }

            tile.WallResourceType = wallResourceType;
            tile.Hp = tile.MaxHp;
            RefreshVisual(coord);
            RebuildWallDecoration(coord, wallResourceType);
        }

        /// Dev-only placement (see TileInteractionController's
        /// PlaceBedrock BuildMode) — marks an as-yet-undug, otherwise plain
        /// Rock tile as permanently unminable (RequestDig/RequestReinforce
        /// both refuse a Bedrock tile outright). No-ops on anything that
        /// isn't a plain, unqueued Rock tile — same guard shape as every
        /// other "assign a wall variant" method here, so a tile can never
        /// end up Bedrock and reinforced/resource-veined/queued at once.
        public void SetBedrock(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || tile.IsQueuedForDig || tile.IsQueuedForReinforce
                || tile.IsReinforced || tile.WallResourceType != WallResourceType.None)
            {
                return;
            }

            tile.IsBedrock = true;
            tile.Hp = tile.MaxHp;
            ClearWallDecoration(coord);
            RefreshVisual(coord);
        }

        /// Dev-only terrain placement (see TileInteractionController's
        /// PlaceWater/PlaceLava/PlaceChasm BuildModes) — converts a bare
        /// Rock tile directly into Water/Lava/Chasm, standing in for the
        /// real map generator that doesn't exist yet. No-ops on anything
        /// that isn't currently Rock, same guard SetWallResourceType uses.
        /// Chasm additionally sinks its floor exactly like a Jail's pit
        /// (see ChasmPitDepth) and grows a few spike decorations.
        public void SetTerrainFeature(Vector2Int coord, TileType terrainType)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock)
            {
                return;
            }

            tile.Type = terrainType;
            tile.Ownership = TileOwnership.Unclaimed;
            tile.IsBuildable = false;
            tile.WallResourceType = WallResourceType.None;
            tile.Hp = 0;
            ClearWallDecoration(coord);

            RefreshVisual(coord);

            if (terrainType == TileType.Chasm)
            {
                SetPitDepth(coord, ChasmPitDepth);
                BuildChasmSpikes(coord);
            }
            else if (terrainType == TileType.HolyGround)
            {
                BuildHolyGroundStar(coord);
            }
        }

        public bool InBounds(Vector2Int coord)
        {
            return coord.x >= 0 && coord.x < _width && coord.y >= 0 && coord.y < _height;
        }

        /// Unlike every other accessor in this class, callers of GetTile
        /// span both this component's own tools and every creature agent's
        /// own Update loop (SlimeAgent, ImplingAgent, ...) — a single bad
        /// coord or a grid that's mid-teardown (Object.Destroy is deferred
        /// to end of frame, so an agent can still tick once against a
        /// grid that's pending destruction) would otherwise throw and
        /// break that agent's Update permanently. Falls back to plain Rock
        /// (the same value every tile starts as) rather than crashing.
        public TileState GetTile(Vector2Int coord)
        {
            if (_tiles == null || !InBounds(coord))
            {
                return TileState.Rock;
            }

            return _tiles[coord.x, coord.y];
        }

        public Vector3 GridToWorld(Vector2Int coord)
        {
            return new Vector3((coord.x + 0.5f) * _cellSize, 0f, (coord.y + 0.5f) * _cellSize);
        }

        public Vector2Int WorldToGrid(Vector3 world)
        {
            return new Vector2Int(Mathf.FloorToInt(world.x / _cellSize), Mathf.FloorToInt(world.z / _cellSize));
        }

        /// isImp narrows this for Imps specifically (see ImplingAgent/
        /// BuilderJobBoard's own callers) — everyone else gets the default
        /// false. Floor is walkable by all; Water is walkable by anyone but
        /// an Imp unless it's been Bridged (TryAssignBridgeRoom); Lava is
        /// walkable by nobody at all — Imp included — unless Bridged, since
        /// no creature is fire-resistant yet; Chasm is never walkable, by
        /// anyone. HolyGround is walkable by everyone, same as Floor — it's
        /// just never Claimable (see TileType.HolyGround). Rock is never
        /// walkable either way.
        public bool IsWalkable(Vector2Int coord, bool isImp = false)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            var tile = GetTile(coord);
            if (tile.IsBlocked)
            {
                return false;
            }

            switch (tile.Type)
            {
                case TileType.Floor:
                case TileType.HolyGround:
                    return true;
                case TileType.Water:
                    return !isImp || tile.HasRoom;
                case TileType.Lava:
                    return tile.HasRoom;
                default:
                    // Rock, Chasm.
                    return false;
            }
        }

        /// Marks a Floor tile as off-limits to pathfinding without changing
        /// its type/ownership — used by ThroneRoom to keep its center tile
        /// (the raised orb pedestal) out of reach for implings while it
        /// stays ordinary Claimed Floor for room-placement purposes.
        public void SetBlocked(Vector2Int coord, bool isBlocked)
        {
            if (!InBounds(coord))
            {
                return;
            }

            _tiles[coord.x, coord.y].IsBlocked = isBlocked;
        }

        /// Sinks (or restores) a Floor tile's visual by `depth` world units
        /// below the ordinary FloorSurfaceY — a render-time offset only
        /// (see RefreshVisual); IsWalkable/CanBuildRoomOn/pathfinding never
        /// look at this, so a sunk tile is exactly as walkable as any
        /// other Floor tile. Used by JailManager to render its pit one
        /// full level below the surrounding ground (depth = 1), and to
        /// restore ordinary floor (depth = 0) once a Jail is sold.
        public void SetPitDepth(Vector2Int coord, float depth)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (Mathf.Approximately(tile.PitDepth, depth))
            {
                return;
            }

            tile.PitDepth = depth;
            RefreshVisual(coord);
        }

        public bool IsBuildable(Vector2Int coord)
        {
            return InBounds(coord) && GetTile(coord) is { Type: TileType.Floor, IsBuildable: true };
        }

        /// Whether a brand-new room (Lair, Treasury, ...) could go on coord
        /// right now — Claimed, dug, room-free Floor with the buildable
        /// flag set. LairManager and TreasuryManager both funnel their own
        /// per-footprint placement checks through this single rule rather
        /// than each re-deriving it, so "what makes a tile buildable" only
        /// has one definition to keep in sync.
        public bool CanBuildRoomOn(Vector2Int coord)
        {
            return InBounds(coord) && GetTile(coord) is { Type: TileType.Floor, Ownership: TileOwnership.Claimed, IsBuildable: true, HasRoom: false };
        }

        /// Same as CanBuildRoomOn, but the tile must also be claimed by
        /// ownerId — so a player (or an AI creature) can only place a room
        /// on their own territory, not on a rival keeper's claimed floor.
        /// ownerId -1 falls back to the owner-agnostic check (nothing to
        /// match against).
        public bool CanBuildRoomOn(Vector2Int coord, int ownerId)
        {
            return CanBuildRoomOn(coord) && (ownerId < 0 || GetTile(coord).OwnerId == ownerId);
        }

        /// Whether coord has at least one cardinal neighbor that's already
        /// Claimed floor — or a Claimed bridge tile (see TryAssignBridgeRoom;
        /// a bridged Water/Lava tile is Claimed too, even though it can
        /// never host an ordinary room — see CanBuildRoomOn's own Floor-only
        /// type check). Gates claim jobs so territory only ever grows
        /// outward from what's already claimed, one ring at a time — now
        /// including outward across a bridge — instead of an impling being
        /// able to claim any reachable dug-out tile regardless of whether it
        /// actually borders the claimed frontier.
        public bool BordersClaimedTile(Vector2Int coord)
        {
            return BordersClaimedTile(coord, ownerId: -1);
        }

        /// As above, but a neighbor only counts when it's claimed BY ownerId
        /// — so a player's territory grows outward only from their own
        /// frontier (and across their own bridges), never by butting up
        /// against a rival keeper's claimed floor. ownerId -1 counts any
        /// owner (the plain single-player / HUD-status case).
        public bool BordersClaimedTile(Vector2Int coord, int ownerId)
        {
            foreach (var offset in GridDirections.Cardinal)
            {
                var neighbor = coord + offset;
                if (!InBounds(neighbor))
                {
                    continue;
                }

                var neighborTile = GetTile(neighbor);
                if (neighborTile.Ownership != TileOwnership.Claimed)
                {
                    continue;
                }

                if (ownerId >= 0 && neighborTile.OwnerId != ownerId)
                {
                    continue;
                }

                if (neighborTile.Type is TileType.Floor or TileType.Water or TileType.Lava)
                {
                    return true;
                }
            }

            return false;
        }

        /// Flood-fills connected walkable (Floor) tiles starting from fromCoord,
        /// recording each one's step distance from fromCoord along the way (a
        /// BFS visits tiles in non-decreasing distance order, so this comes for
        /// free — no separate pathfind needed just to rank candidates by real
        /// travel distance instead of misleading-around-walls straight-line
        /// distance). Used both to check whether a dig job has a walkable tile
        /// next to it at all, and how far away that tile actually is to walk to.
        public Dictionary<Vector2Int, int> GetReachableFloorDistances(Vector2Int fromCoord, bool isImp = false)
        {
            var distances = new Dictionary<Vector2Int, int>();
            if (!InBounds(fromCoord))
            {
                return distances;
            }

            var frontier = new Queue<Vector2Int>();
            distances[fromCoord] = 0;
            frontier.Enqueue(fromCoord);

            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                var currentDistance = distances[current];

                foreach (var offset in GridDirections.Cardinal)
                {
                    var neighbor = current + offset;
                    if (distances.ContainsKey(neighbor) || !IsWalkable(neighbor, isImp))
                    {
                        continue;
                    }

                    distances[neighbor] = currentDistance + 1;
                    frontier.Enqueue(neighbor);
                }
            }

            return distances;
        }

        /// Purely cosmetic — tints a queued tile to show BuilderJobBoard couldn't
        /// find a walkable path to it. Guarded so it only touches visuals on an
        /// actual state change, since this gets called every frame per idle impling.
        public void SetUnreachable(Vector2Int coord, bool isUnreachable)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.IsUnreachable == isUnreachable)
            {
                return;
            }

            tile.IsUnreachable = isUnreachable;
            RefreshVisual(coord);
        }

        /// Queuing a tile for digging and queuing it for reinforcement are
        /// mutually exclusive — rejects if the other is already queued (or
        /// the tile is already reinforced/not Rock), same no-op-on-invalid
        /// pattern as the rest of these request methods. Also rejects
        /// Bedrock outright — "unminable" — see SetBedrock.
        public void RequestDig(Vector2Int coord, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || tile.IsQueuedForDig || tile.IsQueuedForReinforce || tile.IsBedrock)
            {
                return;
            }

            tile.IsQueuedForDig = true;
            RefreshVisual(coord);
            DigRequested?.Invoke(coord, ownerId);
        }

        /// Un-queues a Rock tile. Only meaningful while BuilderJobBoard still
        /// considers the job cancelable (see BuilderJobBoard.CancelJob) — the
        /// caller is expected to check that before calling this.
        public bool CancelDig(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || !tile.IsQueuedForDig)
            {
                return false;
            }

            tile.IsQueuedForDig = false;
            RefreshVisual(coord);
            DigCanceled?.Invoke(coord);
            return true;
        }

        /// Queues a Rock tile to be reinforced (see CompleteReinforce).
        /// Mutually exclusive with digging — same reasoning as RequestDig —
        /// and a no-op on a tile that's already reinforced, since there's no
        /// repair mechanic yet to make re-reinforcing meaningful. Also
        /// rejects resource walls (WallResourceType != None) — reinforcing
        /// a gold seam isn't a thing implings do — and Bedrock, which is
        /// already permanently unminable and has nothing to gain from it.
        public void RequestReinforce(Vector2Int coord, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || tile.IsQueuedForDig || tile.IsQueuedForReinforce || tile.IsReinforced || tile.WallResourceType != WallResourceType.None || tile.IsBedrock)
            {
                return;
            }

            tile.IsQueuedForReinforce = true;
            RefreshVisual(coord);
            ReinforceRequested?.Invoke(coord, ownerId);
        }

        /// Un-queues a Rock tile's reinforce job. Only meaningful while
        /// BuilderJobBoard still considers it cancelable (see
        /// BuilderJobBoard.CancelReinforceJob) — the caller is expected to
        /// check that before calling this.
        public bool CancelReinforce(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || !tile.IsQueuedForReinforce)
            {
                return false;
            }

            tile.IsQueuedForReinforce = false;
            RefreshVisual(coord);
            ReinforceCanceled?.Invoke(coord);
            return true;
        }

        /// Finishes a reinforce job: the wall becomes IsReinforced (darker,
        /// see RefreshVisual) and its HP is topped up to ReinforcedMaxHp —
        /// meaningful even on a tile that's already taken some dig damage,
        /// since reinforcing represents thickening the wall back up.
        /// ownerId stamps the wall's owner so its glowing orb renders in that
        /// player's color (see ApplyOrbOwnerColor / ResolveOwnerColor). -1
        /// leaves whatever owner the tile already had (the plain
        /// single-player / unowned-fallback case).
        public void CompleteReinforce(Vector2Int coord, int ownerId = -1)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            tile.IsQueuedForReinforce = false;
            tile.IsReinforced = true;
            tile.Hp = TileState.ReinforcedMaxHp;
            if (ownerId >= 0)
            {
                tile.OwnerId = ownerId;
            }
            RefreshVisual(coord);
        }

        /// Queues a Claimed, room-free Floor tile to become a wall (see
        /// CompleteBuild) — the reverse of digging, for walling off part of
        /// an already-dug-out domain. Requiring Claimed ownership means a
        /// tile can never be both a pending claim job and a pending build
        /// job at once, with no extra exclusion check needed.
        public void RequestBuild(Vector2Int coord, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Floor || tile.Ownership != TileOwnership.Claimed || tile.HasRoom || tile.IsQueuedForBuild)
            {
                return;
            }

            tile.IsQueuedForBuild = true;
            RefreshVisual(coord);
            BuildRequested?.Invoke(coord, ownerId);
        }

        /// Un-queues a Floor tile's build job. Only meaningful while
        /// BuilderJobBoard still considers it cancelable (see
        /// BuilderJobBoard.CancelBuildJob) — the caller is expected to check
        /// that before calling this.
        public bool CancelBuild(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Floor || !tile.IsQueuedForBuild)
            {
                return false;
            }

            tile.IsQueuedForBuild = false;
            RefreshVisual(coord);
            BuildCanceled?.Invoke(coord);
            return true;
        }

        /// Finishes a build job: the tile becomes ordinary Rock again at
        /// full HP. Ownership is left untouched (still Claimed, since
        /// RequestBuild only ever allowed queuing an already-Claimed tile)
        /// so digging it back out later doesn't need a fresh claim job.
        public void CompleteBuild(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            tile.Type = TileType.Rock;
            tile.Hp = TileState.RockMaxHp;
            tile.IsQueuedForBuild = false;
            tile.IsBuildable = false;
            tile.IsReinforced = false;
            tile.IsQueuedForReinforce = false;
            tile.IsQueuedForDig = false;

            RefreshVisual(coord);
        }

        /// Every tile currently tagged with roomId — the level editor's
        /// Remove tool grabs this before selling a room so it can then
        /// reset that exact footprint back to plain Rock (TrySellRoom
        /// leaves the tiles as bare Claimed Floor). Same whole-grid scan
        /// RemoveRoomTiles uses, and must be called before it since
        /// TrySellRoom clears every RoomId as part of the sale.
        public List<Vector2Int> GetRoomFootprint(string roomId)
        {
            var tiles = new List<Vector2Int>();
            if (string.IsNullOrEmpty(roomId))
            {
                return tiles;
            }

            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    if (_tiles[x, y].RoomId == roomId)
                    {
                        tiles.Add(new Vector2Int(x, y));
                    }
                }
            }

            return tiles;
        }

        /// Clears RoomId off every tile belonging to roomId — used to sell a
        /// placed room. Scans the whole grid rather than tracking footprints
        /// separately, which is fine at prototype scale and stays correct
        /// even if a future room type's footprint is bigger than the
        /// current 1x1 Lair. Returns how many tiles it actually cleared, so
        /// the caller (LairManager.TrySellRoom) can refund gold per tile
        /// without needing its own separate footprint count.
        public int RemoveRoomTiles(string roomId)
        {
            var clearedCount = 0;
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    if (_tiles[x, y].RoomId == roomId)
                    {
                        _tiles[x, y].RoomId = null;
                        RefreshVisual(new Vector2Int(x, y));
                        clearedCount++;
                    }
                }
            }

            return clearedCount;
        }

        /// Deals dig damage to a Rock tile. Returns true once the job is done —
        /// either this hit broke through, or the tile was already dug out from
        /// under the caller by another worker sharing the same job.
        /// resourceType/resourceAmount report what a resource-wall tile
        /// (see WallResourceType) yielded from this specific hit — None/0
        /// for a plain wall. The yield is based on however much HP this hit
        /// actually removed (capped at what was left), not the raw damage
        /// number, so an overkill final hit doesn't over-report.
        public bool ApplyDigDamage(Vector2Int coord, int amount, out ResourceType resourceType, out int resourceAmount, int diggerOwnerId = 0)
        {
            resourceType = ResourceType.None;
            resourceAmount = 0;

            if (!InBounds(coord))
            {
                return true;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || tile.IsBedrock)
            {
                // Bedrock is never actually queueable (see RequestDig), so
                // this should never fire in practice — guarded defensively
                // anyway, same "treat as already done" no-op every other
                // invalid-target case here follows, rather than silently
                // applying damage to something meant to be permanent.
                return true;
            }

            if (tile.WallResourceType != WallResourceType.None)
            {
                var hpRemoved = Mathf.Min(amount, tile.Hp);
                resourceType = ToResourceType(tile.WallResourceType);
                resourceAmount = hpRemoved * TileState.ResourceDropPerHp;
            }

            tile.Hp -= amount;
            if (tile.Hp <= 0)
            {
                CompleteDig(coord, diggerOwnerId);
                return true;
            }

            // "regenerate 15hp per hit" — only while the wall survives the
            // hit; a killing blow completes the dig instead (see above), it
            // doesn't get regenerated back to life.
            if (tile.WallResourceType == WallResourceType.RegeneratingGoldWall)
            {
                tile.Hp = Mathf.Min(tile.MaxHp, tile.Hp + TileState.RegeneratingGoldWallRegenPerHit);
            }

            RefreshVisual(coord);
            return false;
        }

        private static ResourceType ToResourceType(WallResourceType wallResourceType)
        {
            switch (wallResourceType)
            {
                case WallResourceType.GoldWall:
                case WallResourceType.RegeneratingGoldWall:
                    return ResourceType.Gold;
                case WallResourceType.ManaCrystalWall:
                    return ResourceType.ManaCrystal;
                default:
                    return ResourceType.None;
            }
        }

        /// Deals damage to a room tile's HP (see TileState.RoomMaxHp) — e.g.
        /// a hostile Gremlin/Warlock attack (see GremlinAgent/WarlockAgent).
        /// Returns true once that tile's HP is fully depleted. This only
        /// tracks the HP — it deliberately doesn't remove anything itself,
        /// since actually tearing down a room correctly (cleaning up every
        /// manager's own tile list/visuals/structures) only works through
        /// LairManager.TrySellRoom; the caller is expected to call that once
        /// this returns true. There's no per-tile removal — depleting one
        /// tile's HP is a signal to destroy the whole room, not just that
        /// tile (see design-doc.md's Happiness section for why).
        public bool ApplyRoomDamage(Vector2Int coord, int amount)
        {
            if (!InBounds(coord))
            {
                return true;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (!tile.HasRoom)
            {
                return true;
            }

            tile.Hp -= amount;
            if (tile.Hp <= 0)
            {
                return true;
            }

            RefreshVisual(coord);
            RoomDamaged?.Invoke(coord, tile.OwnerId);
            return false;
        }

        /// Restores HP to a damaged room tile (see ApplyRoomDamage) — an
        /// impling's repair "jump" (see BuilderJobBoard.ApplyRepairJump/
        /// ImplingAgent's RepairingRoom state). Returns true once the tile
        /// is back at full RoomMaxHp. A no-op-but-true return on a tile
        /// that no longer HasRoom (e.g. sold/destroyed since the repair job
        /// was queued) so the caller's job-completion check doesn't get
        /// stuck waiting on a room that isn't there anymore.
        public bool ApplyRoomRepair(Vector2Int coord, int amount)
        {
            if (!InBounds(coord))
            {
                return true;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (!tile.HasRoom)
            {
                return true;
            }

            tile.Hp = Mathf.Min(TileState.RoomMaxHp, tile.Hp + amount);
            RefreshVisual(coord);
            return tile.Hp >= TileState.RoomMaxHp;
        }

        /// diggerOwnerId is the owner of the impling (or creature) that dug
        /// this tile out — passed straight through to FloorNeedsClaim so the
        /// claim job lands on that player's board.
        public void CompleteDig(Vector2Int coord, int diggerOwnerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            tile.Type = TileType.Floor;
            tile.IsQueuedForDig = false;
            tile.IsBuildable = true;
            // A Floor tile never turns back into Rock, so leftover Rock-only
            // flags/decoration would just be permanently meaningless data.
            tile.IsReinforced = false;
            tile.IsQueuedForReinforce = false;
            tile.WallResourceType = WallResourceType.None;
            ClearWallDecoration(coord);

            RefreshVisual(coord);
            FloorNeedsClaim?.Invoke(coord, diggerOwnerId);
        }

        /// ownerId stamps the claiming player onto the tile — in ordinary
        /// single-player this is 0; on a multi-player map it's whichever
        /// keeper's impling completed the claim job (see
        /// BuilderJobBoard.ApplyClaim). Without this a live-claimed tile
        /// kept OwnerId at its Rock default (-1) and never tinted.
        public void ClaimTile(Vector2Int coord, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type == TileType.Floor && tile.Ownership != TileOwnership.Claimed)
            {
                tile.Ownership = TileOwnership.Claimed;
                tile.OwnerId = ownerId;
                RefreshVisual(coord);
            }
        }

        public bool TryAssignRoom(Vector2Int coord, string roomId)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Floor || tile.Ownership != TileOwnership.Claimed || tile.HasRoom)
            {
                return false;
            }

            tile.RoomId = roomId;
            tile.Hp = TileState.RoomMaxHp;
            RefreshVisual(coord);
            return true;
        }

        /// Same shape as TryAssignRoom, but for Bridge (see BridgeManager),
        /// which sits on Water/Lava instead of ordinary dug Floor — no
        /// Ownership/Claimed requirement to place one (unlike TryAssignRoom,
        /// see BridgeManager.CanPlaceBridgeTile's own adjacency rule
        /// instead). Building the bridge DOES claim its own tile, though —
        /// "bridges claim the lava tile" — so BordersClaimedTile treats it
        /// as claimed territory too, letting an impling claim onward past
        /// it. It still can never host an ordinary room: CanBuildRoomOn
        /// requires Type == Floor, which a bridged Water/Lava tile never is.
        public bool TryAssignBridgeRoom(Vector2Int coord, string roomId, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if ((tile.Type != TileType.Water && tile.Type != TileType.Lava) || tile.HasRoom)
            {
                return false;
            }

            tile.RoomId = roomId;
            tile.Hp = TileState.RoomMaxHp;
            tile.Ownership = TileOwnership.Claimed;
            tile.OwnerId = ownerId;
            RefreshVisual(coord);
            return true;
        }

        // ---- Level-editor-only tile authoring ----
        // Unlike the gameplay-facing methods above (RequestDig,
        // SetWallResourceType, SetTerrainFeature, SetBedrock, ...), these
        // unconditionally overwrite a tile to the exact requested state
        // with no "must already be Rock" precondition — the level
        // designer needs to freely repaint any tile into any other type,
        // authoring finished level data rather than simulating how it got
        // that way (no dig jobs, no gold cost, no implings).

        /// Resets coord back to plain, undamaged, unowned Rock — the
        /// common baseline every other Editor* method below starts from,
        /// so painting a tile into a wall/terrain/floor variant can never
        /// leave stale flags (an old WallResourceType, RoomId, ownership,
        /// ...) behind from whatever it used to be.
        public void EditorResetToRock(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ClearWallDecoration(coord);
            _tiles[coord.x, coord.y] = TileState.Rock;
            RefreshVisual(coord);
        }

        /// Paints coord into one of the wall variants the level designer's
        /// Map Design menu offers (see EditorWallVariant) — resets to
        /// plain Rock first, then reuses the same guarded gameplay methods
        /// (SetWallResourceType/SetBedrock) where possible so their
        /// existing HP/decoration logic doesn't need duplicating. ownerId
        /// only matters for the Reinforced case (see TileState.OwnerId/
        /// ApplyOrbOwnerColor — a Reinforced wall's orb is tinted by its
        /// owner) and is otherwise ignored; defaults to -1 ("no owner"),
        /// same fallback every other Editor* ownership param uses.
        public void EditorPaintWall(Vector2Int coord, EditorWallVariant variant, int ownerId = -1)
        {
            if (!InBounds(coord))
            {
                return;
            }

            EditorResetToRock(coord);
            switch (variant)
            {
                case EditorWallVariant.Reinforced:
                {
                    ref var tile = ref _tiles[coord.x, coord.y];
                    tile.IsReinforced = true;
                    tile.Hp = TileState.ReinforcedMaxHp;
                    tile.OwnerId = ownerId;
                    RefreshVisual(coord);
                    break;
                }
                case EditorWallVariant.GoldWall:
                    SetWallResourceType(coord, WallResourceType.GoldWall);
                    break;
                case EditorWallVariant.RegeneratingGoldWall:
                    SetWallResourceType(coord, WallResourceType.RegeneratingGoldWall);
                    break;
                case EditorWallVariant.ManaCrystalWall:
                    SetWallResourceType(coord, WallResourceType.ManaCrystalWall);
                    break;
                case EditorWallVariant.Bedrock:
                    SetBedrock(coord);
                    break;
                // Plain: EditorResetToRock above already leaves it as
                // plain, undamaged Rock.
            }
        }

        /// Paints coord into Water/Lava/Chasm — resets to plain Rock first
        /// (unlike SetTerrainFeature, which refuses anything that isn't
        /// already Rock) so the level designer can freely repaint any tile,
        /// then reuses SetTerrainFeature itself for the actual conversion
        /// so Chasm's pit-depth/spike decoration logic doesn't need
        /// duplicating.
        public void EditorPaintTerrain(Vector2Int coord, TileType terrainType)
        {
            if (!InBounds(coord))
            {
                return;
            }

            EditorResetToRock(coord);
            SetTerrainFeature(coord, terrainType);
        }

        /// Digs coord directly into Floor, Claimed or not, skipping the
        /// dig-job/impling flow entirely (see BuilderJobBoard) — the level
        /// designer authors finished level state, not a simulation of how
        /// it got that way. ownerId is only meaningful when claimed is
        /// true (see TileState.OwnerId); an Unclaimed tile always ends up
        /// with ownerId -1 regardless of what's passed in.
        public void EditorPaintFloor(Vector2Int coord, bool claimed, int ownerId)
        {
            if (!InBounds(coord))
            {
                return;
            }

            EditorResetToRock(coord);
            ref var tile = ref _tiles[coord.x, coord.y];
            tile.Type = TileType.Floor;
            tile.Ownership = claimed ? TileOwnership.Claimed : TileOwnership.Unclaimed;
            tile.OwnerId = claimed ? ownerId : -1;
            tile.IsBuildable = true;
            RefreshVisual(coord);
        }

        /// Reassigns coord's owner without touching anything else about
        /// it — deliberately narrower than EditorPaintFloor/EditorPaintWall,
        /// which each re-derive other tile state (Ownership/IsReinforced/
        /// HP/...) alongside ownerId. Used by the Level Designer's edit
        /// mode to let the author reassign who an already-placed Claimed
        /// floor tile or Reinforced wall belongs to. No-ops on anything
        /// that isn't already one of those two — an unowned tile type
        /// (plain Rock, Unclaimed floor, terrain, ...) has nothing
        /// meaningful to reassign. A room tile is Claimed Floor too, so
        /// this works for one — but see EditorReassignRoomOwner for
        /// reassigning a whole room's footprint at once, which is what
        /// the edit mode actually calls for a room tile.
        public void EditorReassignOwner(Vector2Int coord, int ownerId)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            var isReassignable = (tile.Type == TileType.Floor && tile.Ownership == TileOwnership.Claimed) || tile.IsReinforced;
            if (!isReassignable)
            {
                return;
            }

            // Reassigning a plain Claimed floor tile to the "Unclaimed"
            // pseudo-player (ownerId < 0) actually unclaims it — a claimed
            // floor tile with no owner isn't a state this game models. A
            // Reinforced wall stays a wall and just drops its orb owner
            // (see ApplyOrbOwnerColor's -1 fallback).
            if (ownerId < 0 && tile.Type == TileType.Floor)
            {
                tile.Ownership = TileOwnership.Unclaimed;
            }

            tile.OwnerId = ownerId;
            RefreshVisual(coord);
        }

        /// Reassigns every tile belonging to roomId to a new owner — a
        /// room has one owner conceptually, tracked per-tile the same as
        /// plain Claimed floor (no room manager tracks a per-room owner
        /// separately — see RestoreRoom/TryAssignRoom), so the Level
        /// Designer's edit mode reassigns a whole room at once rather than
        /// just whichever single tile was tapped. Same whole-grid-scan
        /// shape as RemoveRoomTiles. Returns how many tiles it actually
        /// reassigned.
        public int EditorReassignRoomOwner(string roomId, int ownerId)
        {
            var count = 0;
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    if (_tiles[x, y].RoomId == roomId)
                    {
                        _tiles[x, y].OwnerId = ownerId;
                        RefreshVisual(new Vector2Int(x, y));
                        count++;
                    }
                }
            }

            return count;
        }

        /// Whether the level designer's Rooms menu could stamp a room onto
        /// coord — anything except Water/Lava/Chasm/HolyGround (rooms only
        /// ever go on Floor, or plain Rock about to be dug for one — see
        /// EditorPlaceRoomTile) or a tile that already has a room.
        /// HolyGround is excluded the same way Water/Lava/Chasm are — it
        /// can never be Claimed (see TileType.HolyGround), which a room
        /// tile always implicitly is.
        public bool EditorCanPlaceRoomOn(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            var tile = GetTile(coord);
            return tile.Type != TileType.Water && tile.Type != TileType.Lava && tile.Type != TileType.Chasm
                && tile.Type != TileType.HolyGround && !tile.HasRoom;
        }

        /// "If the area hasn't been dug out, just instantly dig it out,
        /// make the tile into Unclaimed tile, and place the room" — per
        /// the level-designer brief. A tile that's already Floor keeps
        /// whatever ownership it already has (only a freshly-dug tile is
        /// forced Unclaimed); no gold cost, no manager/economy wiring —
        /// this just tags the tile with roomId the same way TryAssignRoom
        /// does, which is enough for it to render as a room (see
        /// RefreshVisual's HasRoom branch).
        public bool EditorPlaceRoomTile(Vector2Int coord, string roomId)
        {
            if (!EditorCanPlaceRoomOn(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Floor)
            {
                ClearWallDecoration(coord);
                tile = TileState.Rock;
                tile.Type = TileType.Floor;
                tile.Ownership = TileOwnership.Unclaimed;
                tile.OwnerId = -1;
                tile.IsBuildable = true;
            }

            tile.RoomId = roomId;
            tile.Hp = TileState.RoomMaxHp;
            RefreshVisual(coord);
            return true;
        }

        /// Re-runs RefreshVisual across every tile — needed whenever
        /// something that affects a tile's *color* (not its type/shape)
        /// changes after tiles have already been painted, since
        /// RefreshVisual only reruns when something explicitly asks it to.
        /// OwnerColors (see PlayerColor and
        /// LevelDesignerSession.RefreshGridOwnerColors) qualifies — changing a
        /// player's color in the Level Designer must retint every already-
        /// placed Claimed tile of theirs immediately, not just tiles
        /// painted from then on).
        public void RefreshAllVisuals()
        {
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    RefreshVisual(new Vector2Int(x, y));
                }
            }
        }

        private void BuildAllVisuals()
        {
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    var root = new GameObject($"Tile_{x}_{y}");
                    root.transform.SetParent(transform, false);
                    root.transform.localPosition = GridToWorld(coord);
                    _visuals[x, y] = root;
                    RefreshVisual(coord);
                }
            }
        }

        private void RefreshVisual(Vector2Int coord)
        {
            var tile = _tiles[coord.x, coord.y];
            var visual = _visuals[coord.x, coord.y];
            if (visual == null)
            {
                return;
            }

            Color color;
            if (tile.HasRoom)
            {
                // Same damaged->full HP lerp Rock walls use — darkens
                // toward _roomDamagedColor as a room tile takes damage (see
                // ApplyRoomDamage) and eases back as an impling repairs it
                // (see ApplyRoomRepair), so repair progress is actually
                // visible rather than silent tracked data.
                var hpFraction = Mathf.Clamp01(tile.Hp / (float)TileState.RoomMaxHp);
                color = Color.Lerp(_roomDamagedColor, _roomColor, hpFraction);
            }
            else if (tile.Type == TileType.Rock)
            {
                // Queued-for-dig/reinforce no longer get their own color
                // here — a floating icon communicates that now instead
                // (see UpdateQueuedActionIcon), so a queued wall just
                // shows its ordinary type color underneath. Unreachable
                // is kept as a color, not an icon — it's a warning about
                // the queue itself (an impling can't path to it), not the
                // queued action, and stacking a 4th icon meaning on top of
                // the other 3 wasn't worth it.
                Color baseColor;
                if ((tile.IsQueuedForDig || tile.IsQueuedForReinforce) && tile.IsUnreachable)
                {
                    baseColor = _rockUnreachableColor;
                }
                else if (tile.IsBedrock)
                {
                    baseColor = _bedrockColor;
                }
                else if (tile.IsReinforced)
                {
                    baseColor = _rockReinforcedColor;
                }
                else if (tile.WallResourceType == WallResourceType.GoldWall)
                {
                    baseColor = _goldWallColor;
                }
                else if (tile.WallResourceType == WallResourceType.RegeneratingGoldWall)
                {
                    baseColor = _regeneratingGoldWallColor;
                }
                else if (tile.WallResourceType == WallResourceType.ManaCrystalWall)
                {
                    baseColor = _manaCrystalWallColor;
                }
                else
                {
                    baseColor = _rockColor;
                }

                var hpFraction = Mathf.Clamp01(tile.Hp / (float)tile.MaxHp);
                color = Color.Lerp(_rockDamagedColor, baseColor, hpFraction);
            }
            else if (tile.Type == TileType.Water)
            {
                color = _waterColor;
            }
            else if (tile.Type == TileType.Lava)
            {
                color = _lavaColor;
            }
            else if (tile.Type == TileType.Chasm)
            {
                color = _chasmColor;
            }
            else if (tile.Type == TileType.HolyGround)
            {
                color = _holyGroundColor;
            }
            else if (tile.Ownership == TileOwnership.Claimed)
            {
                // Tinted toward the owning player's color when the Level
                // Designer has opted in (see TintFloorByOwner/OwnerColors/
                // TileState.OwnerId) — false in ordinary gameplay, where
                // this just falls back to the plain claimed color exactly
                // as before.
                color = TintFloorByOwner && tile.OwnerId >= 0 && OwnerColors != null && tile.OwnerId < OwnerColors.Length
                    ? Color.Lerp(_floorClaimedColor, OwnerColors[tile.OwnerId], 0.6f)
                    : _floorClaimedColor;
            }
            else
            {
                color = _floorUnclaimedColor;
            }

            var wallPrefab = GetWallMeshPrefab(tile);
            GameObject terrainMeshPrefab = tile.Type switch
            {
                TileType.Water => _waterMesh,
                TileType.Lava => _lavaMesh,
                _ => null
            };
            var meshPrefab = wallPrefab != null ? wallPrefab : terrainMeshPrefab;

            bool needsRebuild = _visualChildren[coord.x, coord.y] == null || _currentWallPrefab[coord.x, coord.y] != meshPrefab;
            if (needsRebuild)
            {
                if (_visualChildren[coord.x, coord.y] != null)
                {
                    Destroy(_visualChildren[coord.x, coord.y]);
                }

                GameObject child;
                if (wallPrefab != null)
                {
                    // Positioning/scaling (base flush with the floor, full
                    // cellSize on X/Z so neighbours butt together, optional
                    // half-height squash) all live in ApplyWallChildTransform.
                    child = Instantiate(wallPrefab, visual.transform, false);
                    ApplyWallChildTransform(child.transform);
                }
                else if (terrainMeshPrefab != null)
                {
                    // Already an exact 1x1 quad sitting just below its own
                    // local Y=0 (its "floor plane" per the pack's own
                    // LIQUID_TILES_README.txt) — putting the root at
                    // FloorSurfaceY lines that up with where this project's
                    // floor tiles actually sit, no scale correction needed.
                    child = Instantiate(terrainMeshPrefab, visual.transform, false);
                    child.transform.localPosition = new Vector3(0f, FloorSurfaceY, 0f);
                    child.transform.localRotation = Quaternion.identity;
                    child.transform.localScale = Vector3.one * _cellSize;
                }
                else
                {
                    child = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    child.transform.SetParent(visual.transform, false);
                }

                child.name = "Visual";
                _visualChildren[coord.x, coord.y] = child;
                _currentWallPrefab[coord.x, coord.y] = meshPrefab;
            }

            var visualChild = _visualChildren[coord.x, coord.y];
            if (terrainMeshPrefab != null)
            {
                // Water/lava textures already carry their own real color —
                // tinting with the old flat _waterColor/_lavaColor (still
                // used by the cube fallback below) would just muddy them,
                // same lesson as the wall meshes.
                ApplyTint(visualChild, Color.white);
            }
            else if (wallPrefab != null)
            {
                // The Reinforced mesh's brick/cap/orb are one combined
                // renderer (see PlayerColor's own comment) — a uniform
                // property-block tint can't leave the orb's player color
                // alone while still tinting the brick/cap, so in its
                // normal resting state (nothing to actually communicate)
                // this skips tinting entirely and lets each material's own
                // baked color show: gray brick/cap, player-colored orb.
                // Queued/damaged states still tint the whole renderer
                // uniformly (orb included) — an acceptable, rare/transient
                // exception rather than something worth losing the correct
                // steady-state look over.
                bool isPristineReinforced = wallPrefab == _wallMeshReinforced
                    && !tile.IsQueuedForDig && !tile.IsQueuedForReinforce && tile.Hp >= tile.MaxHp;
                if (isPristineReinforced)
                {
                    ClearTint(visualChild);
                }
                else
                {
                    ApplyTint(visualChild, color);
                }

                // Per-owner orb color (see ApplyOrbOwnerColor) — runs
                // every refresh, independent of isPristineReinforced,
                // since the orb's own material slot is swapped directly
                // rather than tinted through the property block either
                // branch above uses.
                if (wallPrefab == _wallMeshReinforced)
                {
                    ApplyOrbOwnerColor(visualChild, tile.OwnerId);
                }
            }
            else
            {
                visualChild.transform.localPosition = Vector3.down * (tile.Type == TileType.Rock ? 0f : (0.5f + tile.PitDepth));
                visualChild.transform.localScale = new Vector3(_cellSize * 0.95f, tile.Type == TileType.Rock ? 1f : 0.15f, _cellSize * 0.95f);

                var renderer = visualChild.GetComponent<Renderer>();
                // Plain Claimed/Unclaimed floor gets a real dungeon_pack
                // texture; every other non-mesh case (rooms, chasm/holy
                // ground, build-queued, or water/lava if their mesh
                // failed to load) keeps today's flat-colored look.
                bool isPlainFloor = tile.Type == TileType.Floor && !tile.HasRoom && !tile.IsQueuedForBuild;
                if (isPlainFloor && tile.Ownership == TileOwnership.Claimed && _floorClaimedMaterial != null)
                {
                    renderer.sharedMaterial = _floorClaimedMaterial;
                    var variantIndex = Mathf.Abs(coord.x * 92821 + coord.y * 68917) % _claimedTileTextures.Length;
                    ApplyTint(visualChild, color, _claimedTileTextures[variantIndex]);
                }
                else if (isPlainFloor && tile.Ownership == TileOwnership.Unclaimed && _floorUnclaimedMaterial != null)
                {
                    renderer.sharedMaterial = _floorUnclaimedMaterial;
                    ApplyTint(visualChild, color);
                }
                else
                {
                    renderer.sharedMaterial = _plainFloorMaterial;
                    ApplyTint(visualChild, color);
                }
            }

            UpdateFloorGrout(coord, tile);
            UpdateQueuedActionIcon(coord, tile);

            // Selection outline is a duplicate of the wall's own current
            // visual (see SetSelectedWall) — if this tile is the selected
            // one and its shape/prefab just changed (needsRebuild above),
            // the outline would otherwise still be duplicating the old,
            // now-destroyed mesh. Re-running the same selection keeps it
            // in sync; harmless/cheap on every other call since
            // SetSelectedWall no-ops when the coord isn't selected.
            if (_selectedWallCoord == coord)
            {
                SetSelectedWall(coord);
            }

            TileChanged?.Invoke(coord);
        }

        /// Ensures/clears the dark-gray grout slab under a plain Claimed
        /// floor tile — see the _floorGrout field's own header. Full cell
        /// footprint (so adjacent slabs meet and read as continuous grout
        /// lines) sitting ~1cm below the textured floor cube's top so it
        /// only shows in the gaps. Cheap to call every RefreshVisual: it
        /// only creates/destroys the slab when the claimed-ness actually
        /// changes, otherwise just re-applies the transform/tint.
        private void UpdateFloorGrout(Vector2Int coord, TileState tile)
        {
            bool wantsGrout = tile.Type == TileType.Floor
                && tile.Ownership == TileOwnership.Claimed
                && !tile.HasRoom
                && !tile.IsQueuedForBuild;

            var existing = _floorGrout[coord.x, coord.y];
            if (!wantsGrout)
            {
                if (existing != null)
                {
                    Destroy(existing);
                    _floorGrout[coord.x, coord.y] = null;
                }
                return;
            }

            if (existing == null)
            {
                existing = GameObject.CreatePrimitive(PrimitiveType.Cube);
                existing.name = "Grout";
                existing.transform.SetParent(_visuals[coord.x, coord.y].transform, false);
                Destroy(existing.GetComponent<Collider>());
                existing.GetComponent<Renderer>().sharedMaterial = _plainFloorMaterial;
                _floorGrout[coord.x, coord.y] = existing;
            }

            existing.transform.localPosition = Vector3.down * (0.5f + tile.PitDepth);
            existing.transform.localScale = new Vector3(_cellSize, 0.13f, _cellSize);
            ApplyTint(existing, _claimedGroutColor);
        }

        /// Ensures/clears the floating icon for a queued Rock/Floor tile —
        /// see QueuedIcon's own header for why this replaced flat color
        /// tinting. Only rebuilds when the icon kind actually changes
        /// (RefreshVisual fires per dig-damage hit).
        private void UpdateQueuedActionIcon(Vector2Int coord, TileState tile)
        {
            QueuedIcon icon;
            if (tile.Type == TileType.Rock && tile.IsQueuedForDig)
            {
                icon = QueuedIcon.Pickaxe;
            }
            else if (tile.Type == TileType.Rock && tile.IsQueuedForReinforce)
            {
                icon = QueuedIcon.Shield;
            }
            else if (tile.Type == TileType.Floor && tile.IsQueuedForBuild)
            {
                icon = QueuedIcon.Hammer;
            }
            else
            {
                icon = QueuedIcon.None;
            }

            if (_queuedActionIconKind[coord.x, coord.y] == icon)
            {
                return;
            }

            _queuedActionIconKind[coord.x, coord.y] = icon;

            var existing = _queuedActionIcons[coord.x, coord.y];
            if (existing != null)
            {
                Destroy(existing);
                _queuedActionIcons[coord.x, coord.y] = null;
            }

            if (icon == QueuedIcon.None)
            {
                return;
            }

            var parent = _visuals[coord.x, coord.y].transform;
            GameObject iconRoot = icon switch
            {
                QueuedIcon.Pickaxe => BuildPickaxeIcon(parent),
                QueuedIcon.Shield => BuildShieldIcon(parent),
                QueuedIcon.Hammer => BuildConstructIcon(parent),
                _ => null
            };
            _queuedActionIcons[coord.x, coord.y] = iconRoot;
        }

        /// A diagonal handle crossed by a shorter head near one end —
        /// read from roughly above (this project's fixed-ish isometric
        /// angle), same "flat, top-down-legible" convention
        /// BuildHolyGroundStar already uses rather than a billboard that'd
        /// need per-frame facing logic.
        private GameObject BuildPickaxeIcon(Transform parent)
        {
            var root = new GameObject("QueuedIcon_Mine");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(0f, QueuedIconFloatHeight, 0f);

            var handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            handle.name = "Handle";
            handle.transform.SetParent(root.transform, false);
            handle.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
            handle.transform.localScale = new Vector3(0.06f, 0.06f, 0.5f);
            handle.GetComponent<Renderer>().material.color = new Color(0.4f, 0.28f, 0.15f);
            Destroy(handle.GetComponent<Collider>());

            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            head.name = "Head";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0.1f, 0f, 0.1f);
            head.transform.localRotation = Quaternion.Euler(0f, -45f, 0f);
            head.transform.localScale = new Vector3(0.05f, 0.05f, 0.32f);
            head.GetComponent<Renderer>().material.color = new Color(0.55f, 0.55f, 0.58f);
            Destroy(head.GetComponent<Collider>());

            return root;
        }

        /// A round disc with a small raised boss in the center — same
        /// flat, top-down-legible convention as the pickaxe icon above.
        private GameObject BuildShieldIcon(Transform parent)
        {
            var root = new GameObject("QueuedIcon_Reinforce");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(0f, QueuedIconFloatHeight, 0f);

            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Disc";
            disc.transform.SetParent(root.transform, false);
            disc.transform.localScale = new Vector3(0.32f, 0.03f, 0.32f);
            disc.GetComponent<Renderer>().material.color = new Color(0.55f, 0.6f, 0.68f);
            Destroy(disc.GetComponent<Collider>());

            var boss = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            boss.name = "Boss";
            boss.transform.SetParent(root.transform, false);
            boss.transform.localPosition = Vector3.up * 0.02f;
            boss.transform.localScale = Vector3.one * 0.12f;
            boss.GetComponent<Renderer>().material.color = new Color(0.85f, 0.75f, 0.3f);
            Destroy(boss.GetComponent<Collider>());

            return root;
        }

        /// "A hammer on an empty yellow frame" — a hollow rectangle
        /// standing roughly where the future wall will rise (base at the
        /// floor surface, not floating high like the Mine/Reinforce
        /// icons), built from 4 thin bars the same way BuildHolyGroundStar/
        /// JailManager's fence rails already do, plus a small hammer
        /// shape sitting in the middle of it.
        private GameObject BuildConstructIcon(Transform parent)
        {
            var root = new GameObject("QueuedIcon_Construct");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(0f, FloorSurfaceY + ConstructFrameHeight * 0.5f, 0f);

            BuildFrameBar(root.transform, new Vector3(0f, ConstructFrameHeight * 0.5f, 0f), new Vector3(ConstructFrameWidth, ConstructFrameBarThickness, ConstructFrameBarThickness));
            BuildFrameBar(root.transform, new Vector3(0f, -ConstructFrameHeight * 0.5f, 0f), new Vector3(ConstructFrameWidth, ConstructFrameBarThickness, ConstructFrameBarThickness));
            BuildFrameBar(root.transform, new Vector3(-ConstructFrameWidth * 0.5f, 0f, 0f), new Vector3(ConstructFrameBarThickness, ConstructFrameHeight, ConstructFrameBarThickness));
            BuildFrameBar(root.transform, new Vector3(ConstructFrameWidth * 0.5f, 0f, 0f), new Vector3(ConstructFrameBarThickness, ConstructFrameHeight, ConstructFrameBarThickness));

            var handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            handle.name = "HammerHandle";
            handle.transform.SetParent(root.transform, false);
            handle.transform.localScale = new Vector3(0.05f, 0.4f, 0.05f);
            handle.GetComponent<Renderer>().material.color = new Color(0.4f, 0.28f, 0.15f);
            Destroy(handle.GetComponent<Collider>());

            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            head.name = "HammerHead";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = Vector3.up * 0.2f;
            head.transform.localScale = new Vector3(0.22f, 0.1f, 0.1f);
            head.GetComponent<Renderer>().material.color = new Color(0.5f, 0.5f, 0.52f);
            Destroy(head.GetComponent<Collider>());

            return root;
        }

        private static void BuildFrameBar(Transform parent, Vector3 localPosition, Vector3 localScale)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = "FrameBar";
            bar.transform.SetParent(parent, false);
            bar.transform.localPosition = localPosition;
            bar.transform.localScale = localScale;
            bar.GetComponent<Renderer>().material.color = Color.yellow;
            Destroy(bar.GetComponent<Collider>());
        }

        /// Selects coord's tile for the yellow outline highlight, or
        /// clears the current selection if coord is null / out of bounds
        /// / has no visual built yet. Only one tile can be selected at a
        /// time — selecting a new one replaces whatever was selected
        /// before. The outline itself is an "inverted hull": a duplicate
        /// of the tile's own current visual (whatever mesh/cube
        /// _visualChildren currently holds for it — not Rock-specific,
        /// despite the name/its original gameplay-only use case, see the
        /// Level Designer's edit mode), scaled up slightly and rendered
        /// with a front-face-culled flat yellow material (see
        /// _selectionOutlineMaterial) so only its silhouette margin shows
        /// around the real mesh.
        public void SetSelectedWall(Vector2Int? coord)
        {
            if (_selectionOutline != null)
            {
                Destroy(_selectionOutline);
                _selectionOutline = null;
            }

            _selectedWallCoord = null;

            if (coord == null || !InBounds(coord.Value))
            {
                return;
            }

            var source = _visualChildren[coord.Value.x, coord.Value.y];
            if (source == null)
            {
                return;
            }

            _selectedWallCoord = coord;

            var outline = Instantiate(source, source.transform.parent, false);
            outline.name = "SelectionOutline";
            outline.transform.localPosition = source.transform.localPosition;
            outline.transform.localRotation = source.transform.localRotation;
            outline.transform.localScale = source.transform.localScale * SelectionOutlineScale;

            foreach (var renderer in outline.GetComponentsInChildren<Renderer>())
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    materials[i] = _selectionOutlineMaterial;
                }

                renderer.sharedMaterials = materials;

                var collider = renderer.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }
            }

            _selectionOutline = outline;
        }

        /// Seats a wall mesh child in its tile: base flush with the floor,
        /// full cellSize on X/Z so adjacent walls read as one continuous
        /// surface. In "half wall" mode (see SetHalfWalls) the mesh is
        /// squashed to half height on Y about its base — the bottom half
        /// stays put and the top is pressed down to the midpoint.
        private void ApplyWallChildTransform(Transform child)
        {
            var heightScale = _halfWalls ? 0.5f : 1f;
            child.localPosition = new Vector3(0f, WallBaseLocalY, 0f);
            child.localRotation = Quaternion.identity;
            child.localScale = new Vector3(_cellSize, heightScale, _cellSize);
        }

        /// Toggles "half wall" display mode — every wall mesh is squashed to
        /// half its height about its base (bottom half kept, top pressed
        /// down), letting the player see over the dungeon without altering
        /// the walls in any gameplay sense. Purely visual; wired to
        /// BottomMenuBar's Settings menu.
        public void SetHalfWalls(bool enabled)
        {
            if (_halfWalls == enabled)
            {
                return;
            }

            _halfWalls = enabled;

            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    var child = _visualChildren[x, y];
                    if (child != null && GetWallMeshPrefab(_tiles[x, y]) != null)
                    {
                        ApplyWallChildTransform(child.transform);
                    }
                }
            }

            // The selection outline is a clone of one wall's child transform
            // (see SetSelectedWall) — rebuild it so it tracks the new height.
            if (_selectedWallCoord.HasValue)
            {
                SetSelectedWall(_selectedWallCoord);
            }
        }

        /// Which dungeon_pack wall prefab (if any) a tile should render as
        /// — null means fall back to the plain colored cube (only
        /// possible today if a mesh failed to load; every Rock variant
        /// has a dedicated mesh now). IsBedrock/IsReinforced take
        /// priority over WallResourceType since TileState keeps them
        /// mutually exclusive already (see RequestReinforce/SetBedrock).
        private GameObject GetWallMeshPrefab(TileState tile)
        {
            if (tile.Type != TileType.Rock)
            {
                return null;
            }

            if (tile.IsBedrock)
            {
                return _wallMeshBedrock;
            }

            if (tile.IsReinforced)
            {
                return _wallMeshReinforced;
            }

            switch (tile.WallResourceType)
            {
                case WallResourceType.GoldWall:
                    return _wallMeshGold;
                case WallResourceType.RegeneratingGoldWall:
                    return _wallMeshGoldRegen;
                case WallResourceType.ManaCrystalWall:
                    return _wallMeshManaCrystal;
                default:
                    return _wallMeshStone;
            }
        }

        /// Applies a MaterialPropertyBlock color tint to every renderer
        /// under visual, instead of touching .material (which would
        /// instantiate a per-object material copy) — the same shared
        /// M_StoneWall material is reused across every wall tile.
        /// baseMapOverride optionally swaps which texture a shared
        /// material's _BaseMap shows for this instance (e.g. picking one
        /// of the 4 claimed-floor texture variants) — always explicitly
        /// cleared/set from scratch each call (Clear(), not GetPropertyBlock
        /// first) so a texture override from a tile's previous state (a
        /// different WallResourceType, a different floor Ownership, ...)
        /// can never linger on a renderer that's since switched away from
        /// needing one.
        /// Removes any per-instance tint override entirely, so a renderer
        /// falls back to each of its materials' own baked-in color —
        /// used for the Reinforced wall's pristine state (see its own
        /// call site) rather than ApplyTint(..., Color.white), since a
        /// white property-block override would still multiply-blend with
        /// (and wash out) M_ReinforcedOrb's own colored/emissive look.
        private static void ClearTint(GameObject visual)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                renderer.SetPropertyBlock(null);
            }
        }

        private void ApplyTint(GameObject visual, Color color, Texture2D baseMapOverride = null)
        {
            _sharedPropertyBlock ??= new MaterialPropertyBlock();
            var renderers = visual.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                _sharedPropertyBlock.Clear();
                _sharedPropertyBlock.SetColor(BaseColorId, color);
                if (baseMapOverride != null)
                {
                    _sharedPropertyBlock.SetTexture(BaseMapId, baseMapOverride);
                }

                renderer.SetPropertyBlock(_sharedPropertyBlock);
            }
        }

        private void ClearWallDecoration(Vector2Int coord)
        {
            var existing = _wallDecorations[coord.x, coord.y];
            if (existing != null)
            {
                Destroy(existing);
                _wallDecorations[coord.x, coord.y] = null;
            }
        }

        /// Builds the "gold in a random pattern" child decoration for
        /// Gold/RegeneratingGoldWall (nothing for any other type, including
        /// ManaCrystalWall, which is just a color per the brief). The
        /// pattern is seeded from the tile's own coordinate so it's stable
        /// — this only runs once, when SetWallResourceType assigns the
        /// type, not on every RefreshVisual (which fires every hit and
        /// would otherwise re-randomize/flicker the nuggets). Skipped
        /// entirely once a dedicated dungeon_pack mesh exists for the
        /// type (see GetWallMeshPrefab) — its texture already bakes in
        /// gold/crystal clusters, so floating procedural nugget cubes on
        /// top would just look redundant.
        private void RebuildWallDecoration(Vector2Int coord, WallResourceType wallResourceType)
        {
            ClearWallDecoration(coord);

            if (wallResourceType == WallResourceType.GoldWall && _wallMeshGold != null)
            {
                return;
            }

            if (wallResourceType == WallResourceType.RegeneratingGoldWall && _wallMeshGoldRegen != null)
            {
                return;
            }

            if (wallResourceType != WallResourceType.GoldWall && wallResourceType != WallResourceType.RegeneratingGoldWall)
            {
                return;
            }

            var isRegenerating = wallResourceType == WallResourceType.RegeneratingGoldWall;
            var nuggetCount = isRegenerating ? RegeneratingGoldNuggetCount : GoldNuggetCount;
            var nuggetColor = isRegenerating ? _regeneratingGoldNuggetColor : _goldNuggetColor;

            var container = new GameObject("GoldNuggets");
            container.transform.SetParent(_visuals[coord.x, coord.y].transform, false);
            _wallDecorations[coord.x, coord.y] = container;

            var rng = new System.Random(coord.x * 92821 + coord.y * 68917 + 17);
            for (int i = 0; i < nuggetCount; i++)
            {
                var nugget = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nugget.name = $"Nugget_{i}";
                nugget.transform.SetParent(container.transform, false);
                nugget.transform.localPosition = new Vector3(
                    ((float)rng.NextDouble() - 0.5f) * 0.8f,
                    ((float)rng.NextDouble() - 0.5f) * 0.8f,
                    ((float)rng.NextDouble() - 0.5f) * 0.8f);
                var scale = 0.07f + (float)rng.NextDouble() * 0.05f;
                nugget.transform.localScale = Vector3.one * scale;
                nugget.GetComponent<Renderer>().material.color = nuggetColor;
                Destroy(nugget.GetComponent<Collider>());
            }
        }

        /// "Spikes sticking up from the bottom" — a handful of thin,
        /// randomly-placed pointy cubes standing on the sunk Chasm floor
        /// (see SetTerrainFeature/ChasmPitDepth). World-positioned and
        /// parented to this component's own transform, same convention
        /// JailManager's pit structures use (its own dirt floor/prisoner
        /// visuals), rather than nested under the tile's own thin (0.15-tall)
        /// floor cube — nesting there would squash a spike's local offsets
        /// down by that same thin scale. Reuses the seeded-per-coord RNG
        /// pattern RebuildWallDecoration uses for gold nuggets and the same
        /// _wallDecorations slot, built once when the tile becomes a Chasm.
        private void BuildChasmSpikes(Vector2Int coord)
        {
            ClearWallDecoration(coord);

            var container = new GameObject("ChasmSpikes");
            container.transform.SetParent(transform, false);
            _wallDecorations[coord.x, coord.y] = container;

            var floorTopY = FloorSurfaceY - ChasmPitDepth;
            var worldPos = GridToWorld(coord);

            var rng = new System.Random(coord.x * 51239 + coord.y * 30097 + 7);
            for (int i = 0; i < ChasmSpikeCount; i++)
            {
                var spike = GameObject.CreatePrimitive(PrimitiveType.Cube);
                spike.name = $"Spike_{i}";
                spike.transform.SetParent(container.transform, false);
                spike.transform.position = new Vector3(
                    worldPos.x + ((float)rng.NextDouble() - 0.5f) * 0.6f,
                    floorTopY + ChasmSpikeHeight * 0.5f,
                    worldPos.z + ((float)rng.NextDouble() - 0.5f) * 0.6f);
                spike.transform.rotation = Quaternion.Euler(
                    ((float)rng.NextDouble() - 0.5f) * 20f,
                    (float)rng.NextDouble() * 360f,
                    ((float)rng.NextDouble() - 0.5f) * 20f);
                spike.transform.localScale = new Vector3(ChasmSpikeRadius, ChasmSpikeHeight, ChasmSpikeRadius);
                spike.GetComponent<Renderer>().material.color = _chasmSpikeColor;
                Destroy(spike.GetComponent<Collider>());
            }
        }

        /// A golden 8-pointed star centered on coord's white HolyGround
        /// tile — 4 double-ended bars (see HolyGroundStarAngles), world-
        /// positioned and parented to this component's own transform, same
        /// "don't nest under the tile's own thin floor cube" convention
        /// BuildChasmSpikes uses (nesting there would squash local offsets
        /// down by that cube's thin 0.15 Y-scale). Reuses the
        /// _wallDecorations slot, built once when the tile becomes
        /// HolyGround.
        private void BuildHolyGroundStar(Vector2Int coord)
        {
            ClearWallDecoration(coord);

            var container = new GameObject("HolyGroundStar");
            container.transform.SetParent(transform, false);
            _wallDecorations[coord.x, coord.y] = container;

            var worldPos = GridToWorld(coord);
            var starY = FloorSurfaceY + HolyGroundStarReliefOffset;

            foreach (var angleDegrees in HolyGroundStarAngles)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bar.name = $"StarBar_{angleDegrees}";
                bar.transform.SetParent(container.transform, false);
                bar.transform.position = new Vector3(worldPos.x, starY, worldPos.z);
                bar.transform.rotation = Quaternion.Euler(0f, angleDegrees, 0f);
                bar.transform.localScale = new Vector3(_cellSize * HolyGroundStarLength, HolyGroundStarThickness, HolyGroundStarThickness);
                bar.GetComponent<Renderer>().material.color = _holyGroundStarColor;
                Destroy(bar.GetComponent<Collider>());
            }
        }
    }
}

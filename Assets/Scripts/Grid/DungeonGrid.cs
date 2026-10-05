using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Grid
{
    /// Owns tile data and their placeholder visuals. Isometric look comes entirely
    /// from the camera angle (see CameraControl/IsoCameraController) — the grid
    /// itself is a plain XZ plane, which keeps dig/territory/room logic 2D and simple.
    public partial class DungeonGrid : MonoBehaviour
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

        /// Tints the Reinforced wall mesh's glowing orb sub-part (see
        /// ApplyTint) — same placeholder-blue value as ThroneRoom's own
        /// _playerColor field, kept in sync manually since there's no
        /// real per-player color selection system yet for either to read
        /// from instead (both are marked as stand-ins for one).
        [SerializeField] private Color _playerColor = new Color(0.25f, 0.55f, 0.95f);
        [SerializeField] private Color _holyGroundStarColor = new Color(0.85f, 0.7f, 0.15f);
        [SerializeField] private Color _unholyGroundStarColor = new Color(0.6f, 0.08f, 0.12f);

        // "Make it as deep as the Jail" — same one-full-grid-level sink
        // JailManager's own PitDepth constant uses (DungeonGrid.SetPitDepth
        // is a render-only offset either way, see its own header).
        private const float ChasmPitDepth = 1f;

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
        private Material _selectionOutlineMaterial;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        // Parallel to _visuals — small decorative child objects (currently
        // just gold nuggets) parented to a tile's cube, built once when a
        // wall becomes a resource type (not on every RefreshVisual, which
        // fires every hit and would otherwise re-randomize/flicker them)
        // and cleared once the tile stops being Rock at all.
        private GameObject[,] _wallDecorations;

        // Wall torches share the _wallDecorations slot (mutually exclusive
        // in practice — a torch only ever considers a plain, undecorated
        // Rock wall, see ConsiderWallTorch) so they get fog-hiding and
        // reinforce/dig cleanup for free from the existing wallDecoration
        // plumbing. This set just guards against re-scanning the same
        // Floor tile's neighbors on every one of its own RefreshVisual
        // calls (claim tint, fog dim/visible, ownership flips, ...) —
        // torch placement itself only needs to happen once, the first
        // time a given tile is seen as Floor.
        private readonly HashSet<Vector2Int> _torchScannedFloors = new();

        public int Width => _width;
        public int Height => _height;
        public float CellSize => _cellSize;

        /// Local-keeper fog of war, or null when there is none (the Level
        /// Designer). Set by FogOfWar.Initialize; when
        /// non-null, RefreshVisual renders an Unseen tile as plain Rock and
        /// dims an Explored one. See FogOfWar.
        public FogOfWar Fog { get; set; }

        /// Whose queued-job icons (Mine / Reinforce / Construct) this client
        /// draws — a player only sees their own selections, not a rival's.
        /// The local keeper's OwnerId offline / on the host, the replicated
        /// keeper's on the networked client, and moved by the debug player
        /// switcher. -1 shows every keeper's icons (the Level Designer, which
        /// has none anyway). See UpdateQueuedActionIcon.
        public int LocalViewerOwnerId { get; set; } = -1;

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
            _torchScannedFloors.Clear();
            _wallMeshStone = Resources.Load<GameObject>("Dungeon/Wall_Stone");
            _wallMeshGold = Resources.Load<GameObject>("Dungeon/Wall_Gold");
            _wallMeshGoldRegen = Resources.Load<GameObject>("Dungeon/Wall_GoldRegen");
            _wallMeshManaCrystal = Resources.Load<GameObject>("Dungeon/Wall_ManaCrystal");
            _wallMeshBedrock = Resources.Load<GameObject>("Dungeon/Wall_Bedrock");
            _wallMeshReinforced = Resources.Load<GameObject>("Dungeon/Wall_Reinforced");
            _waterMesh = Resources.Load<GameObject>("Dungeon/Tile_Water");
            _lavaMesh = Resources.Load<GameObject>("Dungeon/Tile_Lava");
            _plainFloorMaterial = Prims.NewMaterial();
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

        /// Converts a bare Rock tile directly into a terrain type
        /// (Water/Lava/Chasm/Holy Ground/Unholy Ground). No-ops on anything
        /// that isn't currently Rock, same guard SetWallResourceType uses —
        /// the runtime dev tool goes through DevPaintTerrain and the Level
        /// Designer through EditorPaintTerrain, both of which reset to Rock
        /// first so any tile can be repainted. Chasm additionally sinks its
        /// floor like a Jail's pit (see ChasmPitDepth) with a few spikes;
        /// Holy/Unholy Ground grow a star decoration.
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
                BuildGroundStar(coord, "HolyGroundStar", _holyGroundStarColor);
            }
            else if (terrainType == TileType.UnholyGround)
            {
                BuildGroundStar(coord, "UnholyGroundStar", _unholyGroundStarColor);
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

        /// Client-only: overwrite coord's tile with the host's replicated
        /// state and rebuild its visual. The host never calls this — it
        /// mutates tiles through the ordinary gameplay/editor methods, and a
        /// networking layer (see GridNetSync) forwards each TileChanged to
        /// clients, which land here. RebuildWallDecoration / the terrain
        /// decorations below are all deterministic (coord-seeded), so client
        /// and host match.
        public void ApplyReplicatedTile(Vector2Int coord, TileState state)
        {
            if (_tiles == null || !InBounds(coord))
            {
                return;
            }

            _tiles[coord.x, coord.y] = state;
            RefreshVisual(coord);
            RebuildWallDecoration(coord, state.WallResourceType);

            // RefreshVisual only recolours the tile — the terrain-feature
            // decorations (Chasm spikes, Holy / Unholy Ground stars) are
            // otherwise only built on the host inside SetTerrainFeature, so
            // rebuild them here too or a replicated terrain tile renders bare
            // on the client.
            if (state.Type == TileType.Chasm)
            {
                BuildChasmSpikes(coord);
            }
            else if (state.Type == TileType.HolyGround)
            {
                BuildGroundStar(coord, "HolyGroundStar", _holyGroundStarColor);
            }
            else if (state.Type == TileType.UnholyGround)
            {
                BuildGroundStar(coord, "UnholyGroundStar", _unholyGroundStarColor);
            }
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
                case TileType.UnholyGround:
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

        /// Whether a straight line between two tile centers is unobstructed
        /// by a Rock wall — used by combat target acquisition (see
        /// design-doc.md's Combat section / Combatant). Only Rock blocks
        /// (Bedrock/Reinforced/resource walls are all TileType.Rock);
        /// Floor/Water/Lava/Chasm/HolyGround and other creatures do not.
        /// A supercover trace so a diagonal sightline can't slip through the
        /// corner between two orthogonally-touching walls. Both endpoints
        /// are excluded — a creature standing next to (or on) a wall still
        /// sees out.
        public bool HasLineOfSight(Vector2Int from, Vector2Int to)
        {
            if (from == to)
            {
                return true;
            }

            int x = from.x;
            int y = from.y;
            int nx = Mathf.Abs(to.x - from.x);
            int ny = Mathf.Abs(to.y - from.y);
            int signX = to.x > from.x ? 1 : -1;
            int signY = to.y > from.y ? 1 : -1;

            // Supercover walk from `from` to `to` — steps through every cell
            // the segment passes through, corners included, so a diagonal
            // sightline can't slip between two touching walls.
            for (int ix = 0, iy = 0; ix < nx || iy < ny;)
            {
                int decision = (1 + 2 * ix) * ny - (1 + 2 * iy) * nx;
                if (decision == 0)
                {
                    x += signX;
                    y += signY;
                    ix++;
                    iy++;
                }
                else if (decision < 0)
                {
                    x += signX;
                    ix++;
                }
                else
                {
                    y += signY;
                    iy++;
                }

                var cell = new Vector2Int(x, y);
                if (cell != to && GetTile(cell).Type == TileType.Rock)
                {
                    return false;
                }
            }

            return true;
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
}
}

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
        [SerializeField] private int _width = 24;
        [SerializeField] private int _height = 24;
        [SerializeField] private float _cellSize = 1f;

        [SerializeField] private Color _rockColor = new Color(0.35f, 0.32f, 0.3f);
        [SerializeField] private Color _rockQueuedColor = new Color(0.55f, 0.5f, 0.2f);
        [SerializeField] private Color _rockQueuedReinforceColor = new Color(0.2f, 0.35f, 0.55f);
        [SerializeField] private Color _rockReinforcedColor = new Color(0.16f, 0.14f, 0.13f);

        /// Bedrock — darker than a reinforced wall, per the brief.
        [SerializeField] private Color _bedrockColor = new Color(0.07f, 0.06f, 0.055f);

        [SerializeField] private Color _rockDamagedColor = new Color(0.25f, 0.12f, 0.1f);
        [SerializeField] private Color _rockUnreachableColor = new Color(0.2f, 0.28f, 0.38f);
        [SerializeField] private Color _floorUnclaimedColor = new Color(0.2f, 0.18f, 0.15f);
        [SerializeField] private Color _floorClaimedColor = new Color(0.25f, 0.4f, 0.25f);
        [SerializeField] private Color _floorQueuedBuildColor = new Color(0.45f, 0.32f, 0.18f);
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
        // under this. The tile's actual geometry (flat colored cube for
        // Floor/Water/..., or an autotiled KayKit wall prefab for Rock —
        // see WallAutotiler/WallMeshCatalog) lives in _visualChildren as a
        // single swappable child, so geometry can change (dug out, wall
        // shape changes as neighbors change) without disturbing decorations
        // parented alongside it.
        private GameObject[,] _visuals;
        private GameObject[,] _visualChildren;

        // Parallel to _visualChildren — null means that tile's current
        // child is the plain flat-colored cube; a value means it's an
        // instantiated wall prefab of that shape. Lets RefreshVisual tell
        // whether it needs to destroy/recreate the child (shape actually
        // changed) or just re-tint the existing one (e.g. a damage tick),
        // since RefreshVisual fires on every single dig-damage hit.
        private WallShape?[,] _cachedWallShape;

        // Loaded once from Resources (DungeonGrid is built entirely
        // procedurally by GameBootstrap — there's no scene object to
        // hand-wire this reference onto). Null is a valid, supported state:
        // Rock tiles just fall back to the plain colored cube every other
        // tile type already uses, rather than throwing.
        private WallMeshCatalog _wallCatalog;

        private static MaterialPropertyBlock _sharedPropertyBlock;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        // Parallel to _visuals — small decorative child objects (currently
        // just gold nuggets) parented to a tile's cube, built once when a
        // wall becomes a resource type (not on every RefreshVisual, which
        // fires every hit and would otherwise re-randomize/flicker them)
        // and cleared once the tile stops being Rock at all.
        private GameObject[,] _wallDecorations;

        public int Width => _width;
        public int Height => _height;
        public float CellSize => _cellSize;

        /// Per-player colors used to tint a Claimed tile's visual by its
        /// owner (see RefreshVisual/TileState.OwnerId) — set once by the
        /// level designer's session (LevelDesignerSession.
        /// RefreshGridOwnerColors) and left null during ordinary gameplay,
        /// so BuildWorld's tiles render exactly as before.
        public Color[] EditorOwnerColors { get; set; }

        /// World-space Y of a dug (Floor) tile's top surface. Floor tiles sit
        /// with their center at y=-0.5 and a height of 0.15 (see RefreshVisual)
        /// so they read as "excavated" against Rock's raised full-height
        /// block — the visible ground is not y=0. Anything meant to sit flush
        /// on the floor (e.g. Portal's staircase) should be grounded here
        /// rather than assuming y=0.
        public float FloorSurfaceY => -0.5f + 0.15f * 0.5f;

        public event Action<Vector2Int> DigRequested;
        public event Action<Vector2Int> DigCanceled;
        public event Action<Vector2Int> ReinforceRequested;
        public event Action<Vector2Int> ReinforceCanceled;
        public event Action<Vector2Int> BuildRequested;
        public event Action<Vector2Int> BuildCanceled;
        public event Action<Vector2Int> TileChanged;

        /// Fired when a Rock tile finishes digging out as Unclaimed floor —
        /// i.e. always, since digging no longer auto-claims by proximity to
        /// the portal. BuilderJobBoard listens for this to queue a claim job.
        public event Action<Vector2Int> FloorNeedsClaim;

        /// Fired whenever a room tile takes damage and survives (see
        /// ApplyRoomDamage) — not fired on the hit that destroys it, since
        /// at that point the whole room is about to be torn down rather
        /// than needing a repair job. BuilderJobBoard listens for this to
        /// queue a repair job, the same way it listens to FloorNeedsClaim.
        public event Action<Vector2Int> RoomDamaged;

        public void Initialize(int width, int height, float cellSize)
        {
            _width = width;
            _height = height;
            _cellSize = cellSize;

            _tiles = new TileState[_width, _height];
            _visuals = new GameObject[_width, _height];
            _visualChildren = new GameObject[_width, _height];
            _cachedWallShape = new WallShape?[_width, _height];
            _wallDecorations = new GameObject[_width, _height];
            _wallCatalog = Resources.Load<WallMeshCatalog>("Dungeon/WallMeshCatalog");

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
        /// the fixed starting rooms (Chaos Core, Portal room, the corridor
        /// between them) built directly by GameBootstrap, as opposed to
        /// tiles dug out during play. isBuildable defaults to true for
        /// ordinary rooms; GameBootstrap passes false for rooms that already
        /// have their own fixed structure, so a Lair can't be placed on them.
        public void CarveRoom(Vector2Int center, int halfSize, bool isBuildable = true)
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
                    _tiles[coord.x, coord.y].IsBuildable = isBuildable;
                    RefreshVisualAndWallNeighbors(coord);
                }
            }
        }

        /// Same idea as CarveRoom (Floor+Claimed), but width x height from
        /// origin (its min-corner, not a center) rather than a symmetric
        /// halfSize — CarveRoom's (2*halfSize+1) span can only ever produce
        /// odd dimensions, so this is what an even-sized room (e.g. a 4x4)
        /// needs instead.
        public void CarveRect(Vector2Int origin, int width, int height, bool isBuildable = true)
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
                    _tiles[coord.x, coord.y].IsBuildable = isBuildable;
                    RefreshVisualAndWallNeighbors(coord);
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

            RefreshVisualAndWallNeighbors(coord);

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
        /// its type/ownership — used by ChaosCore to keep its center tile
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
        public void RequestDig(Vector2Int coord)
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
            DigRequested?.Invoke(coord);
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
        public void RequestReinforce(Vector2Int coord)
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
            ReinforceRequested?.Invoke(coord);
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
        public void CompleteReinforce(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            tile.IsQueuedForReinforce = false;
            tile.IsReinforced = true;
            tile.Hp = TileState.ReinforcedMaxHp;
            RefreshVisual(coord);
        }

        /// Queues a Claimed, room-free Floor tile to become a wall (see
        /// CompleteBuild) — the reverse of digging, for walling off part of
        /// an already-dug-out domain. Requiring Claimed ownership means a
        /// tile can never be both a pending claim job and a pending build
        /// job at once, with no extra exclusion check needed.
        public void RequestBuild(Vector2Int coord)
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
            BuildRequested?.Invoke(coord);
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
        public bool ApplyDigDamage(Vector2Int coord, int amount, out ResourceType resourceType, out int resourceAmount)
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
                CompleteDig(coord);
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
            RoomDamaged?.Invoke(coord);
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

        public void CompleteDig(Vector2Int coord)
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

            RefreshVisualAndWallNeighbors(coord);
            FloorNeedsClaim?.Invoke(coord);
        }

        public void ClaimTile(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type == TileType.Floor && tile.Ownership != TileOwnership.Claimed)
            {
                tile.Ownership = TileOwnership.Claimed;
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
        public bool TryAssignBridgeRoom(Vector2Int coord, string roomId)
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
            RefreshVisualAndWallNeighbors(coord);
        }

        /// Paints coord into one of the wall variants the level designer's
        /// Map Design menu offers (see EditorWallVariant) — resets to
        /// plain Rock first, then reuses the same guarded gameplay methods
        /// (SetWallResourceType/SetBedrock) where possible so their
        /// existing HP/decoration logic doesn't need duplicating.
        public void EditorPaintWall(Vector2Int coord, EditorWallVariant variant)
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
            RefreshVisualAndWallNeighbors(coord);
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
            RefreshVisualAndWallNeighbors(coord);
            return true;
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
                Color baseColor;
                if ((tile.IsQueuedForDig || tile.IsQueuedForReinforce) && tile.IsUnreachable)
                {
                    baseColor = _rockUnreachableColor;
                }
                else if (tile.IsQueuedForDig)
                {
                    baseColor = _rockQueuedColor;
                }
                else if (tile.IsQueuedForReinforce)
                {
                    baseColor = _rockQueuedReinforceColor;
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
            else if (tile.IsQueuedForBuild)
            {
                color = _floorQueuedBuildColor;
            }
            else if (tile.Ownership == TileOwnership.Claimed)
            {
                // Tinted toward the owning player's color when one's set
                // (see EditorOwnerColors/TileState.OwnerId) — null/-1 in
                // ordinary gameplay, where this just falls back to the
                // plain claimed color exactly as before.
                color = tile.OwnerId >= 0 && EditorOwnerColors != null && tile.OwnerId < EditorOwnerColors.Length
                    ? Color.Lerp(_floorClaimedColor, EditorOwnerColors[tile.OwnerId], 0.6f)
                    : _floorClaimedColor;
            }
            else
            {
                color = _floorUnclaimedColor;
            }

            bool needsWallMesh = tile.Type == TileType.Rock && _wallCatalog != null;
            WallShape? shape = null;
            float rotation = 0f;
            if (needsWallMesh)
            {
                (shape, rotation) = WallAutotiler.Compute(
                    IsWallNeighbor(coord + Vector2Int.up),
                    IsWallNeighbor(coord + Vector2Int.right),
                    IsWallNeighbor(coord + Vector2Int.down),
                    IsWallNeighbor(coord + Vector2Int.left));
            }

            bool needsRebuild = _visualChildren[coord.x, coord.y] == null || _cachedWallShape[coord.x, coord.y] != shape;
            if (needsRebuild)
            {
                if (_visualChildren[coord.x, coord.y] != null)
                {
                    Destroy(_visualChildren[coord.x, coord.y]);
                }

                GameObject child;
                if (needsWallMesh)
                {
                    child = Instantiate(_wallCatalog.GetPrefab(shape.Value), visual.transform, false);
                    child.transform.localScale = Vector3.one * _wallCatalog.PrefabScale;
                }
                else
                {
                    child = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    child.transform.SetParent(visual.transform, false);
                }

                child.name = "Visual";
                _visualChildren[coord.x, coord.y] = child;
                _cachedWallShape[coord.x, coord.y] = shape;
            }

            var visualChild = _visualChildren[coord.x, coord.y];
            if (needsWallMesh)
            {
                // Compose on top of the prefab's own authored rotation
                // rather than replacing it outright — these source meshes
                // carry a real orientation of their own (e.g. an axis
                // compensation baked in at import), and overwriting
                // localRotation wholesale silently discarded it, leaving
                // every wall lying on its side instead of standing up.
                var baseRotation = _wallCatalog.GetPrefab(shape.Value).transform.localRotation;
                visualChild.transform.localPosition = Vector3.zero;
                visualChild.transform.localRotation = Quaternion.Euler(0f, rotation + _wallCatalog.RotationOffsetDegrees, 0f) * baseRotation;
                ApplyTint(visualChild, color);
            }
            else
            {
                visualChild.transform.localPosition = Vector3.down * (tile.Type == TileType.Rock ? 0f : (0.5f + tile.PitDepth));
                visualChild.transform.localScale = new Vector3(_cellSize * 0.95f, tile.Type == TileType.Rock ? 1f : 0.15f, _cellSize * 0.95f);
                visualChild.GetComponent<Renderer>().material.color = color;
            }

            TileChanged?.Invoke(coord);
        }

        /// True if coord is out of bounds (map edges read as sealed rather
        /// than sprouting spurious end-caps) or holds a Rock tile — the
        /// input WallAutotiler needs for each of a wall tile's 4 cardinal
        /// neighbors.
        private bool IsWallNeighbor(Vector2Int coord)
        {
            return !InBounds(coord) || GetTile(coord).Type == TileType.Rock;
        }

        /// Applies a MaterialPropertyBlock color tint to every renderer
        /// under visual, instead of touching .material (which would
        /// instantiate a per-object material copy) — the same shared
        /// M_DungeonWalls material is reused across every wall tile.
        private static void ApplyTint(GameObject visual, Color color)
        {
            _sharedPropertyBlock ??= new MaterialPropertyBlock();
            var renderers = visual.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                renderer.GetPropertyBlock(_sharedPropertyBlock);
                _sharedPropertyBlock.SetColor(BaseColorId, color);
                renderer.SetPropertyBlock(_sharedPropertyBlock);
            }
        }

        /// Same as RefreshVisual(coord), but also refreshes its 4 cardinal
        /// neighbors — needed at every call site where a tile's Type
        /// actually flips to/from Rock (dig completing, room carving,
        /// level-designer repainting, ...), since a Rock neighbor's wall
        /// shape depends on whether coord itself currently reads as a wall.
        /// Every other call site (damage, queued flags, reinforced,
        /// bedrock, resource type — none of which change Type) can keep
        /// calling plain RefreshVisual, since those never change any
        /// neighbor's wall shape.
        private void RefreshVisualAndWallNeighbors(Vector2Int coord)
        {
            RefreshVisual(coord);
            RefreshVisualIfInBounds(coord + Vector2Int.up);
            RefreshVisualIfInBounds(coord + Vector2Int.right);
            RefreshVisualIfInBounds(coord + Vector2Int.down);
            RefreshVisualIfInBounds(coord + Vector2Int.left);
        }

        private void RefreshVisualIfInBounds(Vector2Int coord)
        {
            if (InBounds(coord))
            {
                RefreshVisual(coord);
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
        /// would otherwise re-randomize/flicker the nuggets).
        private void RebuildWallDecoration(Vector2Int coord, WallResourceType wallResourceType)
        {
            ClearWallDecoration(coord);

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

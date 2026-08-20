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
        [SerializeField] private Color _rockDamagedColor = new Color(0.25f, 0.12f, 0.1f);
        [SerializeField] private Color _rockUnreachableColor = new Color(0.2f, 0.28f, 0.38f);
        [SerializeField] private Color _floorUnclaimedColor = new Color(0.2f, 0.18f, 0.15f);
        [SerializeField] private Color _floorClaimedColor = new Color(0.25f, 0.4f, 0.25f);
        [SerializeField] private Color _floorQueuedBuildColor = new Color(0.45f, 0.32f, 0.18f);
        [SerializeField] private Color _roomColor = new Color(0.55f, 0.15f, 0.5f);
        [SerializeField] private Color _goldWallColor = new Color(0.5f, 0.42f, 0.2f);
        [SerializeField] private Color _regeneratingGoldWallColor = new Color(0.55f, 0.45f, 0.15f);
        [SerializeField] private Color _manaCrystalWallColor = new Color(0.15f, 0.5f, 0.55f);
        [SerializeField] private Color _goldNuggetColor = new Color(0.85f, 0.7f, 0.15f);
        [SerializeField] private Color _regeneratingGoldNuggetColor = new Color(0.95f, 0.8f, 0.2f);

        // "add some gold in a random pattern" — RegeneratingGoldWall gets
        // more nuggets than plain GoldWall so it visually reads as the
        // richer, gold-heavier vein ("switch the amount of gold & rock").
        private const int GoldNuggetCount = 5;
        private const int RegeneratingGoldNuggetCount = 10;

        private TileState[,] _tiles;
        private GameObject[,] _visuals;

        // Parallel to _visuals — small decorative child objects (currently
        // just gold nuggets) parented to a tile's cube, built once when a
        // wall becomes a resource type (not on every RefreshVisual, which
        // fires every hit and would otherwise re-randomize/flicker them)
        // and cleared once the tile stops being Rock at all.
        private GameObject[,] _wallDecorations;

        public int Width => _width;
        public int Height => _height;
        public float CellSize => _cellSize;

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

        public void Initialize(int width, int height, float cellSize)
        {
            _width = width;
            _height = height;
            _cellSize = cellSize;

            _tiles = new TileState[_width, _height];
            _visuals = new GameObject[_width, _height];
            _wallDecorations = new GameObject[_width, _height];

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
                    RefreshVisual(coord);
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

        public bool InBounds(Vector2Int coord)
        {
            return coord.x >= 0 && coord.x < _width && coord.y >= 0 && coord.y < _height;
        }

        public TileState GetTile(Vector2Int coord)
        {
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

        public bool IsWalkable(Vector2Int coord)
        {
            return InBounds(coord) && GetTile(coord) is { Type: TileType.Floor, IsBlocked: false };
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
        /// Claimed floor. Gates claim jobs so territory only ever grows
        /// outward from what's already claimed, one ring at a time, instead
        /// of an impling being able to claim any reachable dug-out tile
        /// regardless of whether it actually borders the claimed frontier.
        public bool BordersClaimedTile(Vector2Int coord)
        {
            foreach (var offset in GridDirections.Cardinal)
            {
                var neighbor = coord + offset;
                if (InBounds(neighbor) && GetTile(neighbor) is { Type: TileType.Floor, Ownership: TileOwnership.Claimed })
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
        public Dictionary<Vector2Int, int> GetReachableFloorDistances(Vector2Int fromCoord)
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
                    if (distances.ContainsKey(neighbor) || !IsWalkable(neighbor))
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
        /// pattern as the rest of these request methods.
        public void RequestDig(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || tile.IsQueuedForDig || tile.IsQueuedForReinforce)
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
        /// a gold seam isn't a thing implings do.
        public void RequestReinforce(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || tile.IsQueuedForDig || tile.IsQueuedForReinforce || tile.IsReinforced || tile.WallResourceType != WallResourceType.None)
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
            if (tile.Type != TileType.Rock)
            {
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
            return false;
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

            RefreshVisual(coord);
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

        private void BuildAllVisuals()
        {
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = $"Tile_{x}_{y}";
                    cube.transform.SetParent(transform, false);
                    cube.transform.localPosition = GridToWorld(coord) + Vector3.down * 0.5f;
                    cube.transform.localScale = new Vector3(_cellSize * 0.95f, 1f, _cellSize * 0.95f);
                    _visuals[x, y] = cube;
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
                color = _roomColor;
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
            else if (tile.IsQueuedForBuild)
            {
                color = _floorQueuedBuildColor;
            }
            else
            {
                color = tile.Ownership == TileOwnership.Claimed ? _floorClaimedColor : _floorUnclaimedColor;
            }

            visual.transform.localPosition = GridToWorld(coord) + Vector3.down * (tile.Type == TileType.Rock ? 0f : (0.5f + tile.PitDepth));
            visual.transform.localScale = new Vector3(_cellSize * 0.95f, tile.Type == TileType.Rock ? 1f : 0.15f, _cellSize * 0.95f);
            visual.GetComponent<Renderer>().material.color = color;

            TileChanged?.Invoke(coord);
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
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Creatures;
using KeepersDomain.Implings;
using KeepersDomain.Monsters;

namespace KeepersDomain.Grid
{
    public enum FogView
    {
        /// Never revealed — renders as plain unmined Rock, hiding whatever's
        /// really on the tile.
        Unseen,

        /// Revealed before, no live vision now — real geometry, dimmed, with
        /// creatures / health rings hidden (the "static map" state).
        Explored,

        /// A friendly minion has vision of this tile right now.
        Visible
    }

    /// Per-tile fog of war for the local keeper. Created by
    /// GameBootstrap.BuildWorld (offline play, skirmish, Continue and the
    /// multiplayer host, all for owner 0) and by BuildClientWorld for the
    /// networked client (owner 1). The Level Designer never creates one, so
    /// DungeonGrid.Fog stays null there and every tile reads as Visible.
    ///
    /// Vision comes from the keeper's own minions (a vision spell is a later
    /// feature) and its claimed territory. On the host/offline the minions
    /// are the live species agents; the client has none of those (the sim
    /// runs host-side), so it passes a gatherer that reads its replicated
    /// CreatureNetView ghosts instead. The client's fog is visual only —
    /// the host still replicates the whole map, so a modified client could
    /// see through it. The local Throne Room is seeded Explored when its
    /// coord is known up front (host/offline), but it never self-grants
    /// Visible — it "stays static" until a minion walks up to it.
    ///
    /// All grid-tile rendering is done inside DungeonGrid.RefreshVisual,
    /// which calls ViewAt; objects outside the grid (creatures, the Throne /
    /// Portal props) are hidden through FogObscurable and the
    /// CreatureHealthRing.IsWorldPointVisibleForRing hook, both driven from
    /// RefreshObscurables here.
    public class FogOfWar : MonoBehaviour
    {
        // Tiles. Matches Creature.DefaultAggroRadius — a minion sees about as
        // far as it can pick a fight.
        private const int VisionRadius = 5;

        // 5 Hz. The recompute early-outs entirely on the common frame where
        // no minion changed tiles, so this is a ceiling, not a per-frame cost.
        private const float RecomputeInterval = 0.2f;

        /// Multiplier applied to an Explored tile's colour in
        /// DungeonGrid.RefreshVisual so scouted-but-unwatched ground reads as
        /// dimmer than live territory.
        public const float ExploredDim = 0.6f;

        private DungeonGrid _grid;
        private int _localOwnerId;
        private int _width;
        private int _height;

        private FogView[,] _view;
        private bool[,] _visibleScratch;

        private Vector2Int? _throneCoord;
        private int _throneHalfSize;

        // Fills the given list with the world positions of ownerId's
        // minions. Null = read the live species agent rosters (host/offline).
        private Action<int, List<Vector3>> _gatherVision;
        private readonly List<Vector3> _visionPositions = new List<Vector3>();

        // Off on the client: its queued-job flags are the host's replicated
        // state, so clearing one locally would only desync the display (the
        // host already validates every dig/build request against real tiles).
        private bool _clearStaleQueuedJobs = true;

        private float _timer;
        private bool _enabled = true;

        // Set from DungeonGrid.TileChanged (a real tile mutation — a tile
        // dug out, claimed, walled, a room placed). Forces the next recompute
        // even if no minion moved, so newly claimed territory / freshly dug
        // floor reveals right away. Fog's own refreshes use suppressNotify,
        // so they never set this.
        private bool _worldDirty = true;

        private readonly List<Vector2Int> _minionTiles = new List<Vector2Int>();
        private readonly HashSet<Vector2Int> _lastMinionTiles = new HashSet<Vector2Int>();

        /// Toggled from BottomMenuBar's Settings menu. Off = the whole map
        /// renders live again (ViewAt returns Visible for everything) and the
        /// obscurable / ring hooks are released.
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                if (_enabled)
                {
                    SeedThroneRoom();
                    _lastMinionTiles.Clear();
                    PushRingHook();
                    _grid.RefreshAllVisuals(suppressNotify: true);
                    Recompute(force: true);
                }
                else
                {
                    CreatureHealthRing.IsWorldPointVisibleForRing = null;
                    _grid.RefreshAllVisuals(suppressNotify: true);
                    RefreshObscurables();
                }
            }
        }

        /// throneCoord: the local Throne Room to seed as Explored, or null if
        /// it isn't known yet (the client — claimed-territory vision covers
        /// it once the grid replicates). gatherVision / clearStaleQueuedJobs:
        /// see their fields; defaults are the host/offline behavior.
        public void Initialize(DungeonGrid grid, int localOwnerId, Vector2Int? throneCoord, int throneRoomHalfSize,
            Action<int, List<Vector3>> gatherVision = null, bool clearStaleQueuedJobs = true)
        {
            _grid = grid;
            _localOwnerId = localOwnerId;
            _throneCoord = throneCoord;
            _gatherVision = gatherVision;
            _clearStaleQueuedJobs = clearStaleQueuedJobs;
            _throneHalfSize = Mathf.Max(0, throneRoomHalfSize);
            _width = grid.Width;
            _height = grid.Height;
            _view = new FogView[_width, _height];
            _visibleScratch = new bool[_width, _height];

            SeedThroneRoom();

            _grid.Fog = this;
            _grid.TileChanged += OnTileChanged;
            PushRingHook();
            _grid.RefreshAllVisuals(suppressNotify: true);
            Recompute(force: true);
        }

        private void OnTileChanged(Vector2Int coord)
        {
            _worldDirty = true;
        }

        /// The visibility state a tile should render as. Out-of-bounds is
        /// Unseen; a disabled fog reports everything Visible.
        public FogView ViewAt(Vector2Int coord)
        {
            if (!_enabled)
            {
                return FogView.Visible;
            }

            return InBounds(coord) ? _view[coord.x, coord.y] : FogView.Unseen;
        }

        private void Update()
        {
            if (!_enabled)
            {
                return;
            }

            _timer += Time.deltaTime;
            if (_timer < RecomputeInterval)
            {
                return;
            }

            _timer = 0f;
            Recompute(force: false);
        }

        private void SeedThroneRoom()
        {
            if (_throneCoord is not Vector2Int throneCoord)
            {
                return;
            }

            for (int dx = -_throneHalfSize; dx <= _throneHalfSize; dx++)
            {
                for (int dy = -_throneHalfSize; dy <= _throneHalfSize; dy++)
                {
                    var coord = throneCoord + new Vector2Int(dx, dy);
                    if (InBounds(coord) && _view[coord.x, coord.y] == FogView.Unseen)
                    {
                        _view[coord.x, coord.y] = FogView.Explored;
                    }
                }
            }
        }

        private void PushRingHook()
        {
            CreatureHealthRing.IsWorldPointVisibleForRing =
                world => ViewAt(_grid.WorldToGrid(world)) == FogView.Visible;
        }

        private void Recompute(bool force)
        {
            GatherMinionTiles();

            if (!force && !_worldDirty && MinionTilesUnchanged())
            {
                // Nothing that affects visibility moved — but a freshly
                // spawned FogObscurable (a new creature) still needs its
                // first toggle.
                RefreshObscurables();
                return;
            }

            _worldDirty = false;
            _lastMinionTiles.Clear();
            for (int i = 0; i < _minionTiles.Count; i++)
            {
                _lastMinionTiles.Add(_minionTiles[i]);
            }

            Array.Clear(_visibleScratch, 0, _visibleScratch.Length);
            for (int i = 0; i < _minionTiles.Count; i++)
            {
                MarkVisibleFrom(_minionTiles[i]);
            }

            MarkClaimedTerritoryVisible();

            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    var current = _view[x, y];
                    var next = _visibleScratch[x, y]
                        ? FogView.Visible
                        : (current == FogView.Unseen ? FogView.Unseen : FogView.Explored);

                    if (next == current)
                    {
                        continue;
                    }

                    _view[x, y] = next;
                    var coord = new Vector2Int(x, y);

                    // A tile just came into live view — if the player queued
                    // a dig / reinforce / build on it while it was fogged and
                    // it turns out not to be the right tile type (already dug,
                    // now a room, ...), drop that stale job now.
                    if (next == FogView.Visible && _clearStaleQueuedJobs)
                    {
                        _grid.ClearStaleQueuedJobs(coord);
                    }

                    _grid.NotifyFogChanged(coord);
                }
            }

            RefreshObscurables();
        }

        private void MarkVisibleFrom(Vector2Int origin)
        {
            int minX = Mathf.Max(0, origin.x - VisionRadius);
            int maxX = Mathf.Min(_width - 1, origin.x + VisionRadius);
            int minY = Mathf.Max(0, origin.y - VisionRadius);
            int maxY = Mathf.Min(_height - 1, origin.y + VisionRadius);
            const int radiusSq = VisionRadius * VisionRadius;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (_visibleScratch[x, y])
                    {
                        continue;
                    }

                    int dx = x - origin.x;
                    int dy = y - origin.y;
                    if (dx * dx + dy * dy > radiusSq)
                    {
                        continue;
                    }

                    var coord = new Vector2Int(x, y);
                    if (_grid.HasLineOfSight(origin, coord))
                    {
                        _visibleScratch[x, y] = true;
                    }
                }
            }
        }

        /// The local keeper's own claimed territory is always visible — plus
        /// a one-tile ring around it, so the rock walls on the frontier (and
        /// the dig markers queued on them) show rather than reading as
        /// undiscovered. A whole-grid scan, but claimed tiles change rarely
        /// and this only runs on a recompute that already touches every tile.
        private void MarkClaimedTerritoryVisible()
        {
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    var tile = _grid.GetTile(new Vector2Int(x, y));
                    if (tile.Ownership != TileOwnership.Claimed || tile.OwnerId != _localOwnerId)
                    {
                        continue;
                    }

                    int minX = Mathf.Max(0, x - 1);
                    int maxX = Mathf.Min(_width - 1, x + 1);
                    int minY = Mathf.Max(0, y - 1);
                    int maxY = Mathf.Min(_height - 1, y + 1);
                    for (int nx = minX; nx <= maxX; nx++)
                    {
                        for (int ny = minY; ny <= maxY; ny++)
                        {
                            _visibleScratch[nx, ny] = true;
                        }
                    }
                }
            }
        }

        private void GatherMinionTiles()
        {
            _minionTiles.Clear();

            if (_gatherVision != null)
            {
                _visionPositions.Clear();
                _gatherVision(_localOwnerId, _visionPositions);
                for (int i = 0; i < _visionPositions.Count; i++)
                {
                    AddMinionTile(_grid.WorldToGrid(_visionPositions[i]));
                }
                return;
            }

            AddAgents(ImplingAgent.All);
            AddAgents(GremlinAgent.All);
            AddAgents(WarlockAgent.All);
            AddAgents(MazeRattlerAgent.All);
            AddAgents(BeanCounterAgent.All);
            AddAgents(ElfAgent.All);
        }

        private void AddAgents<T>(IReadOnlyList<T> agents) where T : class, ICombatant
        {
            for (int i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent?.Creature == null || agent.Creature.OwnerId != _localOwnerId)
                {
                    continue;
                }

                AddMinionTile(_grid.WorldToGrid(agent.transform.position));
            }
        }

        private void AddMinionTile(Vector2Int coord)
        {
            if (InBounds(coord) && !_minionTiles.Contains(coord))
            {
                _minionTiles.Add(coord);
            }
        }

        private bool MinionTilesUnchanged()
        {
            if (_minionTiles.Count != _lastMinionTiles.Count)
            {
                return false;
            }

            for (int i = 0; i < _minionTiles.Count; i++)
            {
                if (!_lastMinionTiles.Contains(_minionTiles[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private void RefreshObscurables()
        {
            var list = FogObscurable.All;
            for (int i = 0; i < list.Count; i++)
            {
                var obscurable = list[i];
                if (obscurable == null)
                {
                    continue;
                }

                var view = ViewAt(_grid.WorldToGrid(obscurable.transform.position));
                bool show = obscurable.Kind == FogObscurableKind.Creature
                    ? view == FogView.Visible
                    : view != FogView.Unseen;
                obscurable.SetShown(show);
            }
        }

        private bool InBounds(Vector2Int coord)
        {
            return coord.x >= 0 && coord.x < _width && coord.y >= 0 && coord.y < _height;
        }

        private void OnDestroy()
        {
            if (_grid != null)
            {
                _grid.TileChanged -= OnTileChanged;
                if (_grid.Fog == this)
                {
                    _grid.Fog = null;
                }
            }

            CreatureHealthRing.IsWorldPointVisibleForRing = null;
        }
    }
}

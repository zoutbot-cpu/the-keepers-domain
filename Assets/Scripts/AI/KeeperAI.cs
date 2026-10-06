using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Core;
using KeepersDomain.Grid;
using KeepersDomain.Implings;
using KeepersDomain.Input;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Monsters;
using KeepersDomain.Net;
using KeepersDomain.Rooms;
using KeepersDomain.DebugUI;

namespace KeepersDomain.AI
{
    /// Drives one AI keeper (KeeperContext.IsAI). It plays through the same
    /// IKeeperActions a human's clicks go through (LocalKeeperActions), so it
    /// can't do anything a player couldn't: queue digs, place rooms, summon
    /// Imps, recruit, reinforce. The only extra is MonsterAgent.OrderAssault,
    /// a march order a player attack-command can reuse later.
    ///
    /// Every ThinkInterval it runs, in order:
    ///   1. Imps     — summon up to a target count while mana allows.
    ///   2. Recruit  — any creature whose join requirements are met.
    ///   3. Rooms    — work down BuildOrder: find a site near the Throne,
    ///                 dig it out, place the room once it's claimed floor
    ///                 and the gold is there.
    ///   4. Expand   — keep a few frontier digs queued, resource walls first.
    ///   5. Fortify  — once the core rooms exist, reinforce the outer shell
    ///                 (which is also what grows wall torches).
    ///   6. Attack   — at AttackArmySize creatures, tunnel to the nearest
    ///                 hostile Throne and march the army there; fall back
    ///                 when it's whittled down to RetreatArmySize.
    ///
    /// Created by WorldBuilder.BuildWorld for each AI keeper (offline and
    /// on the host). Never on the networked client, which has no
    /// KeeperContexts — and not for the client's own keeper on a host.
    public class KeeperAI : MonoBehaviour
    {
        private const float ThinkInterval = 1.5f;

        private const int MinImps = 4;
        private const int MaxImps = 10;
        private const int DigQueueTarget = 8;
        private const int ExpansionRadius = 11;
        private const int RoomSearchRadius = 16;
        private const float RoomPlanTimeoutSeconds = 150f;

        // Fortify only after this many build-order steps, a few walls a tick.
        private const int FortifyAfterBuildSteps = 4;
        private const int ReinforcePerThink = 3;

        private const int AttackArmySize = 6;
        private const int RetreatArmySize = 2;
        // Don't attack before the core economy exists.
        private const int AttackAfterBuildSteps = 3;
        private const float TunnelReplanSeconds = 30f;
        private const int MaxTunnelDigs = 80;

        private struct RoomStep
        {
            public RoomDesignTool Tool;
            public int Width;
            public int Height;
        }

        // Hatchery first (it caps the creature population), then food,
        // training (Gremlins need 9 Training Room tiles to join), beds,
        // research — then more of each and the Jail / Conversion Class
        // pair. Sizes are the smallest each room accepts or close to it.
        private static readonly RoomStep[] BuildOrder =
        {
            new RoomStep { Tool = RoomDesignTool.SlimeHatchery, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.Tavern, Width = 4, Height = 4 },
            new RoomStep { Tool = RoomDesignTool.TrainingRoom, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.Lair, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.Library, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.SlimeHatchery, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.Lair, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.Jail, Width = 5, Height = 5 },
            new RoomStep { Tool = RoomDesignTool.SlimeHatchery, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.ConversionClass, Width = 4, Height = 5 },
            new RoomStep { Tool = RoomDesignTool.Treasury, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.TrainingRoom, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.Lair, Width = 3, Height = 3 },
            new RoomStep { Tool = RoomDesignTool.SlimeHatchery, Width = 3, Height = 3 },
        };

        private KeeperContext _ctx;
        private DungeonGrid _grid;
        private IKeeperActions _actions;
        private int _owner;
        private float _thinkTimer;

        private int _buildStep;
        private RectInt? _roomSite;
        private float _roomSiteAge;

        private bool _attacking;
        private KeeperContext _enemy;
        private readonly List<Vector2Int> _tunnel = new List<Vector2Int>();
        private float _tunnelAge = float.MaxValue;

        private readonly List<MonsterAgent> _army = new List<MonsterAgent>();
        private readonly List<(Vector2Int coord, float score)> _digCandidates = new List<(Vector2Int, float)>();

        public void Initialize(KeeperContext ctx, DungeonGrid grid)
        {
            _ctx = ctx;
            _grid = grid;
            _owner = ctx.OwnerId;
            _actions = new LocalKeeperActions(ctx, grid);

            // Stagger keepers so several AIs don't all think on one frame.
            _thinkTimer = -0.37f * _owner;
            GameplayLog.Write(_owner, "AI keeper online");
        }

        private void Update()
        {
            if (_ctx == null || _ctx.Throne == null || !_ctx.Throne.IsAlive || StanceRegistry.Current == null)
            {
                return;
            }

            _thinkTimer += Time.deltaTime;
            _roomSiteAge += Time.deltaTime;
            _tunnelAge += Time.deltaTime;
            if (_thinkTimer < ThinkInterval)
            {
                return;
            }

            _thinkTimer = 0f;
            SummonImps();
            Recruit();
            ManageRooms();
            Expand();
            Fortify();
            ManageAttack();
        }

        // ---------------------------------------------------------------- Imps

        private void SummonImps()
        {
            var imps = 0;
            foreach (var imp in ImplingAgent.All)
            {
                if (imp != null && imp.Creature.OwnerId == _owner)
                {
                    imps++;
                }
            }

            var target = Mathf.Clamp(MinImps + _buildStep, MinImps, MaxImps);
            if (imps >= target || _ctx.Throne.CurrentMana < ImplingSpawner.ImplingManaUpkeep)
            {
                return;
            }

            if (TryFindWalkableNear(_ctx.ThroneCoord, 3, isImp: true, out var spawnCoord))
            {
                _actions.SpawnImpling(spawnCoord);
            }
        }

        // ------------------------------------------------------------- Recruit

        private void Recruit()
        {
            if (_ctx.GremlinSpawner != null && _ctx.GremlinSpawner.CanRecruit) _actions.Recruit(EditorCreatureKind.Gremlin);
            if (_ctx.WarlockSpawner != null && _ctx.WarlockSpawner.CanRecruit) _actions.Recruit(EditorCreatureKind.Warlock);
            if (_ctx.MazeRattlerSpawner != null && _ctx.MazeRattlerSpawner.CanRecruit) _actions.Recruit(EditorCreatureKind.MazeRattler);
            if (_ctx.BeanCounterSpawner != null && _ctx.BeanCounterSpawner.CanRecruit) _actions.Recruit(EditorCreatureKind.BeanCounter);
        }

        // --------------------------------------------------------------- Rooms

        private RoomStep CurrentStep => BuildOrder[_buildStep % BuildOrder.Length];

        private void ManageRooms()
        {
            var step = CurrentStep;

            if (_roomSite is not RectInt site)
            {
                if (TryFindRoomSite(step.Width, step.Height, out var found))
                {
                    _roomSite = found;
                    _roomSiteAge = 0f;
                    GameplayLog.Write(_owner, $"AI plans a {step.Tool} at ({found.x},{found.y}) {found.width}x{found.height}");
                }
                else
                {
                    // Nowhere to put it right now — move on rather than stall.
                    _buildStep++;
                }
                return;
            }

            if (_roomSiteAge > RoomPlanTimeoutSeconds)
            {
                // Taking too long (no gold, Imps busy elsewhere, a rule the
                // site keeps failing) — skip this step rather than stall.
                GameplayLog.Write(_owner, $"AI gives up on its {step.Tool} for now");
                _roomSite = null;
                _buildStep++;
                return;
            }

            if (!IsSiteStillUsable(site))
            {
                _roomSite = null;
                return;
            }

            var ready = true;
            foreach (var coord in site.allPositionsWithin)
            {
                if (_grid.CanBuildRoomOn(coord, _owner))
                {
                    continue;
                }

                ready = false;
                if (_grid.GetTile(coord).Type == TileType.Rock)
                {
                    _actions.RequestDig(coord);
                }
            }

            if (!ready || TreasuryGold() < site.width * site.height * CostPerTile(step.Tool))
            {
                return;
            }

            _actions.PlaceRoom(step.Tool, site.min, site.max - Vector2Int.one);
            if (_grid.GetTile(site.min).HasRoom)
            {
                GameplayLog.Write(_owner, $"AI built a {step.Tool}");
                _buildStep++;
                _roomSite = null;
            }
        }

        /// The best w x h rectangle near the Throne that is (or can be dug
        /// into) this keeper's buildable floor and touches its territory.
        /// Scored by how much digging it needs plus distance from the Throne,
        /// so the dungeon grows compactly outward from the Throne Room.
        private bool TryFindRoomSite(int w, int h, out RectInt site)
        {
            site = default;
            var best = float.MaxValue;
            var throne = _ctx.ThroneCoord;

            for (var ox = throne.x - RoomSearchRadius; ox <= throne.x + RoomSearchRadius; ox++)
            {
                for (var oy = throne.y - RoomSearchRadius; oy <= throne.y + RoomSearchRadius; oy++)
                {
                    // Try both orientations of a non-square room.
                    for (var turn = 0; turn < (w == h ? 1 : 2); turn++)
                    {
                        var rect = turn == 0 ? new RectInt(ox, oy, w, h) : new RectInt(ox, oy, h, w);
                        if (!TryScoreSite(rect, out var digs))
                        {
                            continue;
                        }

                        var center = rect.center;
                        var score = digs * 2f + Vector2.Distance(center, throne);
                        if (score < best)
                        {
                            best = score;
                            site = rect;
                        }
                    }
                }
            }

            return best < float.MaxValue;
        }

        private bool TryScoreSite(RectInt rect, out int digs)
        {
            digs = 0;
            var touchesTerritory = false;

            foreach (var coord in rect.allPositionsWithin)
            {
                if (!_grid.InBounds(coord) || !IsSiteTile(coord, out var needsDig))
                {
                    return false;
                }

                if (needsDig)
                {
                    digs++;
                }

                if (!touchesTerritory && (IsOwnClaimedFloor(coord) || BordersOwnClaimedFloor(coord)))
                {
                    touchesTerritory = true;
                }
            }

            return touchesTerritory;
        }

        /// A tile a room could end up on: already buildable for this keeper,
        /// plain floor it will claim, or rock its Imps can dig out. Never
        /// another keeper's ground, an existing room, water/lava/chasm/holy
        /// ground, or bedrock.
        private bool IsSiteTile(Vector2Int coord, out bool needsDig)
        {
            needsDig = false;
            if (_grid.CanBuildRoomOn(coord, _owner))
            {
                return true;
            }

            var tile = _grid.GetTile(coord);
            if (tile.HasRoom || tile.IsBlocked)
            {
                return false;
            }

            var foreign = tile.Ownership == TileOwnership.Claimed && tile.OwnerId != _owner;
            if (tile.Type == TileType.Rock)
            {
                if (tile.IsBedrock || (tile.IsReinforced && tile.OwnerId != _owner) || foreign
                    || (tile.IsQueuedForDig && tile.QueuedByOwnerId != _owner))
                {
                    return false;
                }

                needsDig = true;
                return true;
            }

            return tile.Type == TileType.Floor && !foreign;
        }

        private bool IsSiteStillUsable(RectInt site)
        {
            foreach (var coord in site.allPositionsWithin)
            {
                if (!IsSiteTile(coord, out _))
                {
                    return false;
                }
            }

            return true;
        }

        private static int CostPerTile(RoomDesignTool tool)
        {
            switch (tool)
            {
                case RoomDesignTool.Lair: return LairManager.CostPerTile;
                case RoomDesignTool.Treasury: return TreasuryManager.CostPerTile;
                case RoomDesignTool.SlimeHatchery: return SlimeHatcheryManager.CostPerTile;
                case RoomDesignTool.Tavern: return TavernManager.CostPerTile;
                case RoomDesignTool.TrainingRoom: return TrainingRoomManager.CostPerTile;
                case RoomDesignTool.Library: return LibraryManager.CostPerTile;
                case RoomDesignTool.Jail: return JailManager.CostPerTile;
                case RoomDesignTool.ConversionClass: return ConversionClassManager.CostPerTile;
                default: return 20;
            }
        }

        private int TreasuryGold() => _ctx.Treasury != null ? _ctx.Treasury.TotalGold : 0;

        // -------------------------------------------------------------- Expand

        /// Keeps DigQueueTarget frontier digs queued: rock bordering this
        /// keeper's claimed floor, resource veins (gold / mana) first, then
        /// whatever keeps the dungeon compact around the Throne. Never digs
        /// into a rival's walls here — that's the attack tunnel's job.
        private void Expand()
        {
            var queued = 0;
            _digCandidates.Clear();
            var throne = _ctx.ThroneCoord;

            for (var x = 0; x < _grid.Width; x++)
            {
                for (var y = 0; y < _grid.Height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    var tile = _grid.GetTile(coord);
                    if (tile.Type != TileType.Rock)
                    {
                        continue;
                    }

                    if (tile.IsQueuedForDig && tile.QueuedByOwnerId == _owner)
                    {
                        queued++;
                        continue;
                    }

                    if (tile.IsQueuedForDig || tile.IsQueuedForReinforce || tile.IsBedrock || tile.IsReinforced
                        || !BordersOwnClaimedFloor(coord) || BordersForeignTerritory(coord))
                    {
                        continue;
                    }

                    var distance = Vector2Int.Distance(coord, throne);
                    var isVein = tile.WallResourceType != WallResourceType.None;
                    if (!isVein && distance > ExpansionRadius)
                    {
                        continue;
                    }

                    _digCandidates.Add((coord, isVein ? distance - 100f : distance));
                }
            }

            var wanted = DigQueueTarget - queued;
            if (wanted <= 0 || _digCandidates.Count == 0)
            {
                return;
            }

            _digCandidates.Sort((a, b) => a.score.CompareTo(b.score));
            for (var i = 0; i < wanted && i < _digCandidates.Count; i++)
            {
                _actions.RequestDig(_digCandidates[i].coord);
            }
        }

        // ------------------------------------------------------------- Fortify

        /// Reinforces the outer shell — frontier rock beyond the expansion
        /// radius (so it won't be dug later), skipping veins, the planned
        /// room site and the attack tunnel. Not BuilderJobBoard's
        /// auto-reinforce: that queues every frontier wall, and a wall queued
        /// for reinforcing can't be dug, which would freeze expansion.
        private void Fortify()
        {
            if (_buildStep < FortifyAfterBuildSteps)
            {
                return;
            }

            var throne = _ctx.ThroneCoord;
            var budget = ReinforcePerThink;
            for (var x = 0; x < _grid.Width && budget > 0; x++)
            {
                for (var y = 0; y < _grid.Height && budget > 0; y++)
                {
                    var coord = new Vector2Int(x, y);
                    var tile = _grid.GetTile(coord);
                    if (tile.Type != TileType.Rock || tile.IsReinforced || tile.IsBedrock || tile.IsQueuedForDig
                        || tile.IsQueuedForReinforce || tile.WallResourceType != WallResourceType.None
                        || Vector2Int.Distance(coord, throne) <= ExpansionRadius
                        || !BordersOwnClaimedFloor(coord)
                        || (_roomSite is RectInt site && site.Contains(coord))
                        || _tunnel.Contains(coord))
                    {
                        continue;
                    }

                    _actions.RequestReinforce(coord);
                    budget--;
                }
            }
        }

        // -------------------------------------------------------------- Attack

        private void ManageAttack()
        {
            _enemy = PickEnemy();
            GatherArmy();

            if (_enemy == null)
            {
                Recall();
                return;
            }

            if (!_attacking && _army.Count >= AttackArmySize && _buildStep >= AttackAfterBuildSteps)
            {
                _attacking = true;
                _tunnelAge = float.MaxValue;
                GameplayLog.Write(_owner, $"AI goes on the attack against P{_enemy.OwnerId + 1} with {_army.Count} creatures");
            }
            else if (_attacking && _army.Count <= RetreatArmySize)
            {
                GameplayLog.Write(_owner, "AI calls off the attack — army too small");
                Recall();
                return;
            }

            if (!_attacking)
            {
                return;
            }

            if (TryFindRallyPoint(out var rally))
            {
                foreach (var creature in _army)
                {
                    creature.OrderAssault(rally);
                }
                return;
            }

            // No route yet — dig one. Re-planned periodically, since the
            // board changes (walls dug, rooms placed) while Imps work on it.
            if (_tunnelAge >= TunnelReplanSeconds)
            {
                _tunnelAge = 0f;
                PlanTunnel();
            }

            foreach (var coord in _tunnel)
            {
                _actions.RequestDig(coord);
            }
        }

        private void Recall()
        {
            _attacking = false;
            _tunnel.Clear();
            foreach (var creature in _army)
            {
                creature.OrderAssault(null);
            }
        }

        /// The nearest rival this keeper is Aggressive toward whose Throne
        /// still stands.
        private KeeperContext PickEnemy()
        {
            KeeperContext best = null;
            var bestDistance = float.MaxValue;
            if (KeeperContext.All == null)
            {
                return null;
            }

            foreach (var other in KeeperContext.All)
            {
                if (other == null || other.OwnerId == _owner || other.Throne == null || !other.Throne.IsAlive
                    || StanceRegistry.Current.Get(_owner, other.OwnerId) != Stance.Aggressive)
                {
                    continue;
                }

                var distance = Vector2Int.Distance(other.ThroneCoord, _ctx.ThroneCoord);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = other;
                }
            }

            return best;
        }

        private void GatherArmy()
        {
            _army.Clear();
            AddArmy(GremlinAgent.All);
            AddArmy(WarlockAgent.All);
            AddArmy(MazeRattlerAgent.All);
            AddArmy(BeanCounterAgent.All);
            AddArmy(ElfAgent.All);
        }

        private void AddArmy<T>(IReadOnlyList<T> agents) where T : MonsterAgent
        {
            for (var i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent != null && agent.isActiveAndEnabled && agent.Creature.OwnerId == _owner
                    && !agent.Combat.IsDowned)
                {
                    _army.Add(agent);
                }
            }
        }

        /// A walkable tile within 3 of the enemy Throne that the army can
        /// actually reach from home — close enough that Combatant sieges the
        /// Throne on its own (aggro radius 5 + line of sight).
        private bool TryFindRallyPoint(out Vector2Int rally)
        {
            rally = default;
            if (!TryFindWalkableNear(_ctx.ThroneCoord, 3, isImp: false, out var home))
            {
                return false;
            }

            // One flood from home instead of a pathfind per candidate tile.
            var reachable = _grid.GetReachableFloorDistances(home);
            var throne = _enemy.ThroneCoord;
            for (var r = 1; r <= 3; r++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    for (var dy = -r; dy <= r; dy++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r)
                        {
                            continue;
                        }

                        var coord = throne + new Vector2Int(dx, dy);
                        if (reachable.ContainsKey(coord))
                        {
                            rally = coord;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// Cheapest dig route from home to the enemy Throne Room: walkable
        /// tiles cost 1, diggable rock 4 (an enemy's reinforced wall 12, so
        /// the tunnel goes around their fortifications when it can). Water,
        /// lava, chasms and bedrock are impassable — Imps can't cross them.
        /// Fills _tunnel with the rock tiles on that route, nearest first.
        private void PlanTunnel()
        {
            _tunnel.Clear();
            if (!TryFindWalkableNear(_ctx.ThroneCoord, 3, isImp: true, out var start))
            {
                return;
            }

            var goal = _enemy.ThroneCoord;
            var width = _grid.Width;
            var size = width * _grid.Height;
            var cost = new int[size];
            var from = new int[size];
            for (var i = 0; i < size; i++)
            {
                cost[i] = int.MaxValue;
                from[i] = -1;
            }

            var open = new SortedSet<(int f, int index)>();
            var startIndex = start.y * width + start.x;
            cost[startIndex] = 0;
            open.Add((Heuristic(start, goal), startIndex));
            var reached = -1;

            while (open.Count > 0)
            {
                var (_, current) = open.Min;
                open.Remove(open.Min);
                var coord = new Vector2Int(current % width, current / width);
                if (Mathf.Max(Mathf.Abs(coord.x - goal.x), Mathf.Abs(coord.y - goal.y)) <= 2)
                {
                    reached = current;
                    break;
                }

                foreach (var dir in GridDirections.Cardinal)
                {
                    var next = coord + dir;
                    if (!_grid.InBounds(next))
                    {
                        continue;
                    }

                    var step = StepCost(next);
                    if (step < 0)
                    {
                        continue;
                    }

                    var nextIndex = next.y * width + next.x;
                    var g = cost[current] + step;
                    if (g >= cost[nextIndex])
                    {
                        continue;
                    }

                    if (cost[nextIndex] != int.MaxValue)
                    {
                        open.Remove((cost[nextIndex] + Heuristic(next, goal), nextIndex));
                    }

                    cost[nextIndex] = g;
                    from[nextIndex] = current;
                    open.Add((g + Heuristic(next, goal), nextIndex));
                }
            }

            if (reached < 0)
            {
                GameplayLog.Write(_owner, "AI can't find any route to dig toward its enemy");
                return;
            }

            for (var i = reached; i >= 0 && _tunnel.Count < MaxTunnelDigs; i = from[i])
            {
                var coord = new Vector2Int(i % width, i / width);
                if (_grid.GetTile(coord).Type == TileType.Rock)
                {
                    _tunnel.Add(coord);
                }
            }

            _tunnel.Reverse();
            GameplayLog.Write(_owner, $"AI tunnels toward P{_enemy.OwnerId + 1}: {_tunnel.Count} walls to dig");
        }

        private int StepCost(Vector2Int coord)
        {
            if (_grid.IsWalkable(coord, isImp: true))
            {
                return 1;
            }

            var tile = _grid.GetTile(coord);
            if (tile.Type != TileType.Rock || tile.IsBedrock || tile.HasRoom)
            {
                return -1;
            }

            return tile.IsReinforced && tile.OwnerId != _owner ? 12 : 4;
        }

        private static int Heuristic(Vector2Int a, Vector2Int b) => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

        // ------------------------------------------------------------- Helpers

        private bool IsOwnClaimedFloor(Vector2Int coord)
        {
            var tile = _grid.GetTile(coord);
            return tile.Type != TileType.Rock && tile.Ownership == TileOwnership.Claimed && tile.OwnerId == _owner;
        }

        private bool BordersOwnClaimedFloor(Vector2Int coord)
        {
            foreach (var dir in GridDirections.Cardinal)
            {
                var n = coord + dir;
                if (_grid.InBounds(n) && IsOwnClaimedFloor(n))
                {
                    return true;
                }
            }

            return false;
        }

        private bool BordersForeignTerritory(Vector2Int coord)
        {
            foreach (var dir in GridDirections.Cardinal)
            {
                var n = coord + dir;
                if (!_grid.InBounds(n))
                {
                    continue;
                }

                var tile = _grid.GetTile(n);
                if (tile.Ownership == TileOwnership.Claimed && tile.OwnerId != _owner)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryFindWalkableNear(Vector2Int center, int radius, bool isImp, out Vector2Int coord)
        {
            for (var r = 0; r <= radius; r++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    for (var dy = -r; dy <= r; dy++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r)
                        {
                            continue;
                        }

                        var c = center + new Vector2Int(dx, dy);
                        if (_grid.IsWalkable(c, isImp))
                        {
                            coord = c;
                            return true;
                        }
                    }
                }
            }

            coord = default;
            return false;
        }

        /// Wires up an AI for every AI keeper in contexts. Skipped on a
        /// networked host for the client's keeper (owner 1) — a human plays
        /// that one even if the loaded level marked the slot AI.
        public static void CreateFor(KeeperContext[] contexts, DungeonGrid grid, int localOwnerId, Transform parent = null)
        {
            foreach (var ctx in contexts)
            {
                if (ctx == null || !ctx.IsAI || ctx.OwnerId == localOwnerId)
                {
                    continue;
                }

                if (CreatureNetView.HostActive && ctx.OwnerId == 1)
                {
                    continue;
                }

                var go = new GameObject($"KeeperAI P{ctx.OwnerId + 1}");
                if (parent != null)
                {
                    go.transform.SetParent(parent, false);
                }

                go.AddComponent<KeeperAI>().Initialize(ctx, grid);
            }
        }
    }
}

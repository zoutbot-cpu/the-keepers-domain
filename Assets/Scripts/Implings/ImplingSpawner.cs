using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;
using KeepersDomain.LevelDesigner;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Implings
{
    /// The one real (non-debug) way for an impling to come into being:
    /// summoning one directly with mana via the Impling menu's Spawn button
    /// (SpawnImplingAt). Implings are mana-conjured creatures, not people
    /// who need a bed — a Lair doesn't spawn one on placement (see
    /// LairManager), it's just a resting spot a monster claims later.
    public class ImplingSpawner : MonoBehaviour
    {
        /// Mana every impling reserves out of the Throne Room's free pool
        /// for as long as it's alive (see ImplingAgent.OnDestroy, which
        /// releases it back). Read by BottomMenuBar too, so the Spawn
        /// Impling button's label/enabled state stays in sync with what
        /// actually gets reserved here.
        public const int ImplingManaUpkeep = 20;


        private BuilderJobBoard _jobBoard;
        private DungeonGrid _grid;
        private TreasuryManager _treasuryManager;
        private ThroneRoom _throneRoom;
        private SlimeHatcheryManager _slimeHatchery;
        private TavernManager _tavern;
        private int _ownerId;

        public void Initialize(BuilderJobBoard jobBoard, DungeonGrid grid, TreasuryManager treasuryManager, ThroneRoom throneRoom, SlimeHatcheryManager slimeHatchery, TavernManager tavern, int ownerId = 0)
        {
            _jobBoard = jobBoard;
            _grid = grid;
            _treasuryManager = treasuryManager;
            _throneRoom = throneRoom;
            _slimeHatchery = slimeHatchery;
            _tavern = tavern;
            _ownerId = ownerId;
        }

        /// Summons an impling directly out of mana, no Lair required — the
        /// real mechanic behind the bottom menu's "Spawn Impling" tool, not
        /// a debug shortcut. The spawned impling's "home" becomes wherever
        /// it was summoned (ImplingAgent just needs some Vector3 to idle
        /// near/return to; it doesn't require that point to actually be a
        /// placed Lair). No-ops on a non-walkable tile so it can't spawn
        /// inside solid Rock — the mana check itself happens in
        /// SpawnImpling.
        /// Owner is always this spawner's keeper (_ownerId) — implings are
        /// summoned by the player who owns this ImplingSpawner (its
        /// KeeperContext), or, on the load path, by routing the restore to
        /// the matching context's spawner (see GameBootstrap.
        /// RestoreWorldCreatures).
        public void SpawnImplingAt(Vector2Int coord)
        {
            if (_grid.IsWalkable(coord, isImp: true))
            {
                SpawnImpling(_grid.GridToWorld(coord), _ownerId);
            }
        }

        /// Reserves this impling's upkeep mana before creating anything —
        /// if the Throne Room doesn't have enough free mana, nothing spawns
        /// at all. Mana upkeep is what actually pays for an impling
        /// existing; a Lair is not a requirement.
        private void SpawnImpling(Vector3 homeWorldPos, int ownerId)
        {
            if (_throneRoom != null && !_throneRoom.TryReserveMana(ImplingManaUpkeep))
            {
                return;
            }

            var visual = CreatureFactory.CreateOfflineBody(EditorCreatureKind.Imp, homeWorldPos);

            var agent = visual.AddComponent<ImplingAgent>();
            agent.Initialize(_jobBoard, _grid, homeWorldPos, _treasuryManager, _throneRoom, _slimeHatchery, _tavern, ImplingManaUpkeep, ownerId);
            GameplayLog.Write(ownerId, $"{agent.Name} spawned at {_grid.WorldToGrid(homeWorldPos)}");
        }
    }
}

using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
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
        /// Mana every impling reserves out of the Chaos Core's free pool
        /// for as long as it's alive (see ImplingAgent.OnDestroy, which
        /// releases it back). Read by BottomMenuBar too, so the Spawn
        /// Impling button's label/enabled state stays in sync with what
        /// actually gets reserved here.
        public const int ImplingManaUpkeep = 20;

        [SerializeField] private Color _implingColor = new Color(0.8f, 0.2f, 0.2f);
        [SerializeField] private float _implingScale = 0.33f;

        private BuilderJobBoard _jobBoard;
        private DungeonGrid _grid;
        private TreasuryManager _treasuryManager;
        private ChaosCore _chaosCore;
        private SlimeHatcheryManager _slimeHatchery;
        private BaconBeaconManager _baconBeacon;

        public void Initialize(BuilderJobBoard jobBoard, DungeonGrid grid, TreasuryManager treasuryManager, ChaosCore chaosCore, SlimeHatcheryManager slimeHatchery, BaconBeaconManager baconBeacon)
        {
            _jobBoard = jobBoard;
            _grid = grid;
            _treasuryManager = treasuryManager;
            _chaosCore = chaosCore;
            _slimeHatchery = slimeHatchery;
            _baconBeacon = baconBeacon;
        }

        /// Summons an impling directly out of mana, no Lair required — the
        /// real mechanic behind the bottom menu's "Spawn Impling" tool, not
        /// a debug shortcut. The spawned impling's "home" becomes wherever
        /// it was summoned (ImplingAgent just needs some Vector3 to idle
        /// near/return to; it doesn't require that point to actually be a
        /// placed Lair). No-ops on a non-walkable tile so it can't spawn
        /// inside solid Rock — the mana check itself happens in
        /// SpawnImpling.
        public void SpawnImplingAt(Vector2Int coord)
        {
            if (_grid.IsWalkable(coord, isImp: true))
            {
                SpawnImpling(_grid.GridToWorld(coord));
            }
        }

        /// Reserves this impling's upkeep mana before creating anything —
        /// if the Chaos Core doesn't have enough free mana, nothing spawns
        /// at all. Mana upkeep is what actually pays for an impling
        /// existing; a Lair is not a requirement.
        private void SpawnImpling(Vector3 homeWorldPos)
        {
            if (_chaosCore != null && !_chaosCore.TryReserveMana(ImplingManaUpkeep))
            {
                return;
            }

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Impling";
            visual.transform.localScale = Vector3.one * _implingScale;
            // Default capsule is 2 units tall at scale 1, so half-height == scale.
            visual.transform.position = homeWorldPos + Vector3.up * _implingScale;
            visual.GetComponent<Renderer>().material.color = _implingColor;
            Destroy(visual.GetComponent<Collider>());

            var agent = visual.AddComponent<ImplingAgent>();
            agent.Initialize(_jobBoard, _grid, homeWorldPos, _treasuryManager, _chaosCore, _slimeHatchery, _baconBeacon, ImplingManaUpkeep);
            GameplayLog.Write($"{agent.Name} spawned at {_grid.WorldToGrid(homeWorldPos)}");
        }
    }
}

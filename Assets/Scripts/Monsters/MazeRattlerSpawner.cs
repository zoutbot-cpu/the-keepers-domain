using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// Recruits Maze Rattlers out of the Portal's pool — per the design rule
    /// that every non-Imp creature has to "join" the domain by coming down
    /// the portal stairway, same as GremlinSpawner/WarlockSpawner. On top of
    /// pool availability, a Maze Rattler's own join requirements (see
    /// MeetsJoinRequirements) all have to hold too.
    public class MazeRattlerSpawner : MonoBehaviour
    {
        /// How many Maze Rattlers a single placed Jail supports — "1 Jail
        /// for 5 Maze Rattlers" per the brief. Counted per Jail *room*
        /// (JailManager.RoomCount), not per tile — see that property's own
        /// comment for why a tile-based ratio doesn't make sense for Jail's
        /// much bigger minimum footprint.
        private const int MazeRattlersPerJail = 5;

        // Brown per the brief ("make the visual Brown for now") — a
        // placeholder capsule until a real model exists, same shape
        // GremlinAgent/WarlockAgent's own placeholders use.
        [SerializeField] private Color _mazeRattlerColor = new Color(0.45f, 0.3f, 0.15f);
        [SerializeField] private float _mazeRattlerRadiusScale = 0.22f;
        [SerializeField] private float _mazeRattlerHeightScale = 0.4f;

        private DungeonGrid _grid;
        private Portal _portal;
        private LairManager _lairManager;
        private JailManager _jailManager;
        private BaconBeaconManager _baconBeaconManager;
        private TrainingRoomManager _trainingRoomManager;
        private TreasuryManager _treasuryManager;

        public void Initialize(DungeonGrid grid, Portal portal, LairManager lairManager, JailManager jailManager, BaconBeaconManager baconBeaconManager, TrainingRoomManager trainingRoomManager, TreasuryManager treasuryManager)
        {
            _grid = grid;
            _portal = portal;
            _lairManager = lairManager;
            _jailManager = jailManager;
            _baconBeaconManager = baconBeaconManager;
            _trainingRoomManager = trainingRoomManager;
            _treasuryManager = treasuryManager;
        }

        /// How many Maze Rattlers are still available to recruit from the
        /// Portal's pool — read by BottomMenuBar to show the Recruit
        /// button's count (separately from whether it's currently enabled —
        /// see CanRecruit, which also checks MeetsJoinRequirements).
        public int AvailableToRecruit => _portal != null ? _portal.GetPoolCount(MazeRattlerAgent.CreatureKind) : 0;

        /// Whether a recruit attempt would actually succeed right now —
        /// pool availability plus every join requirement. Read by
        /// BottomMenuBar to enable/disable the Recruit button.
        public bool CanRecruit => AvailableToRecruit > 0 && MeetsJoinRequirements();

        /// A Maze Rattler's join requirements, on top of pool availability:
        /// - At least one Lair with no monster currently resting in it
        ///   (same universal "needs somewhere to rest" requirement every
        ///   recruitable creature has).
        /// - Fewer Maze Rattlers already in the domain than
        ///   MazeRattlersPerJail times the number of placed Jail rooms.
        public bool MeetsJoinRequirements()
        {
            if (_lairManager == null || !_lairManager.HasUnclaimedLair())
            {
                return false;
            }

            if (_jailManager == null || MazeRattlerAgent.All.Count >= _jailManager.RoomCount * MazeRattlersPerJail)
            {
                return false;
            }

            return true;
        }

        /// Takes one Maze Rattler out of the Portal's pool and spawns it at
        /// the Portal's own coord ("coming down the stairway"). Returns
        /// whether it actually happened — fails if the pool has none left,
        /// or if MeetsJoinRequirements doesn't hold (checked before
        /// touching the pool, so a failed requirement never costs a pool
        /// slot).
        public bool TryRecruitMazeRattler()
        {
            if (_portal == null || !MeetsJoinRequirements() || !_portal.TryTakeFromPool(MazeRattlerAgent.CreatureKind))
            {
                return false;
            }

            SpawnMazeRattler(_portal.Coord);
            return true;
        }

        /// Public so ConversionClassManager can reuse this exact "capsule +
        /// Initialize" spawn code when a tormented Maze Rattler prisoner
        /// wins its conversion roll and rejoins the domain, instead of
        /// duplicating it.
        public void SpawnMazeRattler(Vector2Int coord)
        {
            var worldPos = _grid.GridToWorld(coord);

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "MazeRattler";
            visual.transform.localScale = new Vector3(_mazeRattlerRadiusScale, _mazeRattlerHeightScale, _mazeRattlerRadiusScale);
            // Default capsule is 2 units tall at scale 1, so half its actual
            // height is _mazeRattlerHeightScale — grounds it on worldPos
            // instead of burying half of it in the floor.
            visual.transform.position = worldPos + Vector3.up * _mazeRattlerHeightScale;
            visual.GetComponent<Renderer>().material.color = _mazeRattlerColor;
            Destroy(visual.GetComponent<Collider>());

            var agent = visual.AddComponent<MazeRattlerAgent>();
            agent.Initialize(_grid, _lairManager, _baconBeaconManager, _trainingRoomManager, _jailManager, _treasuryManager, _portal);
            GameplayLog.Write($"{agent.Name} joined via the Portal at ({coord.x},{coord.y})");
        }
    }
}

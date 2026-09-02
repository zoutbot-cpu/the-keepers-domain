using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// Recruits Bean Counters out of the Portal's pool — per the design
    /// rule that every non-Imp creature has to "join" the domain by coming
    /// down the portal stairway, same as GremlinSpawner/WarlockSpawner/
    /// MazeRattlerSpawner. On top of pool availability, a Bean Counter's
    /// own join requirements (see MeetsJoinRequirements) all have to hold
    /// too.
    public class BeanCounterSpawner : MonoBehaviour
    {
        /// How many Bean Counters a single placed Conversion Class
        /// supports — same "N per room" shape MazeRattlerSpawner's own
        /// MazeRattlersPerJail uses, counted per Conversion Class *room*
        /// (ConversionClassManager.RoomCount), not per tile.
        private const int BeanCountersPerConversionClass = 3;

        // Sickly yellow-green per the brief's zealot flavor — a placeholder
        // capsule until a real model exists, same shape every other
        // creature's own placeholder uses.
        [SerializeField] private Color _beanCounterColor = new Color(0.68f, 0.72f, 0.3f);
        [SerializeField] private float _beanCounterRadiusScale = 0.22f;
        [SerializeField] private float _beanCounterHeightScale = 0.4f;

        private DungeonGrid _grid;
        private Portal _portal;
        private LairManager _lairManager;
        private ConversionClassManager _conversionClassManager;
        private JailManager _jailManager;
        private TavernManager _tavernManager;
        private TreasuryManager _treasuryManager;
        private int _ownerId;

        public void Initialize(DungeonGrid grid, Portal portal, LairManager lairManager, ConversionClassManager conversionClassManager, JailManager jailManager, TavernManager tavernManager, TreasuryManager treasuryManager, int ownerId = 0)
        {
            _grid = grid;
            _portal = portal;
            _lairManager = lairManager;
            _conversionClassManager = conversionClassManager;
            _jailManager = jailManager;
            _tavernManager = tavernManager;
            _treasuryManager = treasuryManager;
            _ownerId = ownerId;
        }

        /// How many Bean Counters are still available to recruit from the
        /// Portal's pool — read by BottomMenuBar to show the Recruit
        /// button's count.
        public int AvailableToRecruit => _portal != null ? _portal.GetPoolCount(BeanCounterAgent.CreatureKind) : 0;

        /// Whether a recruit attempt would actually succeed right now.
        public bool CanRecruit => AvailableToRecruit > 0 && MeetsJoinRequirements();

        /// A Bean Counter's join requirements, on top of pool availability:
        /// - At least one free (unclaimed) Lair spot (same universal
        ///   requirement every recruitable creature has).
        /// - Fewer Bean Counters already in the domain than
        ///   BeanCountersPerConversionClass times the number of placed
        ///   Conversion Class rooms.
        public bool MeetsJoinRequirements()
        {
            if (_lairManager == null || !_lairManager.HasUnclaimedLair())
            {
                return false;
            }

            if (_conversionClassManager == null || BeanCounterAgent.CountForOwner(_ownerId) >= _conversionClassManager.RoomCount * BeanCountersPerConversionClass)
            {
                return false;
            }

            return true;
        }

        /// Takes one Bean Counter out of the Portal's pool and spawns it at
        /// the Portal's own coord ("coming down the stairway").
        public bool TryRecruitBeanCounter()
        {
            if (_portal == null || !MeetsJoinRequirements() || !_portal.TryTakeFromPool(BeanCounterAgent.CreatureKind))
            {
                return false;
            }

            SpawnBeanCounter(_portal.Coord, _ownerId);
            return true;
        }

        /// Spawns one Bean Counter at coord — public so ConversionClassManager
        /// isn't needed for this (Bean Counter is never a conversion
        /// outcome, only a recruit), kept the same access shape as
        /// GremlinSpawner.SpawnGremlin/WarlockSpawner.SpawnWarlock for
        /// consistency.
        public void SpawnBeanCounter(Vector2Int coord, int ownerId = 0)
        {
            var worldPos = _grid.GridToWorld(coord);

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "BeanCounter";
            visual.transform.localScale = new Vector3(_beanCounterRadiusScale, _beanCounterHeightScale, _beanCounterRadiusScale);
            visual.transform.position = worldPos + Vector3.up * _beanCounterHeightScale;
            visual.GetComponent<Renderer>().material.color = _beanCounterColor;
            Destroy(visual.GetComponent<Collider>());

            var agent = visual.AddComponent<BeanCounterAgent>();
            agent.Initialize(_grid, _lairManager, _tavernManager, _conversionClassManager, _jailManager, _treasuryManager, _portal, ownerId);
            GameplayLog.Write($"{agent.Name} joined via the Portal at ({coord.x},{coord.y})");
        }
    }
}

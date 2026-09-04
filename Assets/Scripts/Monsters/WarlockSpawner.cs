using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Net;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// Recruits Warlocks out of the Portal's pool — same "join via the
    /// Portal stairway" rule GremlinSpawner follows: a Warlock can only
    /// come into being by successfully taking one out of
    /// Portal.TryTakeFromPool, spawned at the Portal's own coord. On top of
    /// pool availability, a Warlock's own join requirements (see
    /// MeetsJoinRequirements) all have to hold too.
    public class WarlockSpawner : MonoBehaviour
    {
        /// A placed Lair tile required before a Warlock can join — unlike
        /// Gremlin's requirement, this only checks that a Lair exists
        /// somewhere (claimed or not), not that one is free.
        private const int RequiredLairTiles = 1;

        /// Minimum width/height a single placed Library needs to satisfy
        /// the "at least a 3x3 library" requirement (see
        /// LibraryManager.HasLibraryAtLeast).
        private const int RequiredLibrarySize = 3;

        private DungeonGrid _grid;
        private Portal _portal;
        private LairManager _lairManager;
        private LibraryManager _libraryManager;
        private SlimeHatcheryManager _slimeHatcheryManager;
        private TavernManager _tavernManager;
        private TrainingRoomManager _trainingRoomManager;
        private TreasuryManager _treasuryManager;
        private int _ownerId;

        public void Initialize(DungeonGrid grid, Portal portal, LairManager lairManager, LibraryManager libraryManager, SlimeHatcheryManager slimeHatcheryManager, TavernManager tavernManager, TrainingRoomManager trainingRoomManager, TreasuryManager treasuryManager, int ownerId = 0)
        {
            _grid = grid;
            _portal = portal;
            _lairManager = lairManager;
            _libraryManager = libraryManager;
            _slimeHatcheryManager = slimeHatcheryManager;
            _tavernManager = tavernManager;
            _trainingRoomManager = trainingRoomManager;
            _treasuryManager = treasuryManager;
            _ownerId = ownerId;
        }

        /// How many Warlocks are still available to recruit from the
        /// Portal's pool — read by BottomMenuBar to show the Recruit
        /// button's count (separately from whether it's currently enabled —
        /// see CanRecruit, which also checks MeetsJoinRequirements).
        public int AvailableToRecruit => _portal != null ? _portal.GetPoolCount(WarlockAgent.CreatureKind) : 0;

        /// Whether a recruit attempt would actually succeed right now —
        /// pool availability plus every join requirement.
        public bool CanRecruit => AvailableToRecruit > 0 && MeetsJoinRequirements();

        /// A Warlock's join requirements, on top of pool availability:
        /// - At least one Lair tile placed anywhere (any Lair, claimed or
        ///   not — unlike Gremlin's "at least one free/unclaimed Lair").
        /// - At least one placed Library that's at least 3x3.
        /// - Fewer non-Imp creatures already in the domain (Gremlin +
        ///   Warlock + Maze Rattler + Bean Counter combined) than there are
        ///   Slime Hatchery tiles (across every placed Hatchery) — same
        ///   shape as GremlinSpawner's own Hatchery requirement.
        /// - Fewer intelligent creatures already in the domain (only
        ///   Warlock counts as intelligent so far) than there are
        ///   Tavern tiles (across every placed Tavern).
        public bool MeetsJoinRequirements()
        {
            if (_lairManager == null || _lairManager.TotalTileCount < RequiredLairTiles)
            {
                return false;
            }

            if (_libraryManager == null || !_libraryManager.HasLibraryAtLeast(RequiredLibrarySize, RequiredLibrarySize))
            {
                return false;
            }

            var nonImpCount = GremlinAgent.CountForOwner(_ownerId) + WarlockAgent.CountForOwner(_ownerId) + MazeRattlerAgent.CountForOwner(_ownerId) + BeanCounterAgent.CountForOwner(_ownerId);
            if (_slimeHatcheryManager == null || nonImpCount >= _slimeHatcheryManager.TotalTileCount)
            {
                return false;
            }

            var intelligentCount = WarlockAgent.CountForOwner(_ownerId);
            if (_tavernManager == null || intelligentCount >= _tavernManager.TotalTileCount)
            {
                return false;
            }

            return true;
        }

        /// Takes one Warlock out of the Portal's pool and spawns it at the
        /// Portal's own coord ("coming down the stairway"). Returns whether
        /// it actually happened — fails if the pool has none left, or if
        /// MeetsJoinRequirements doesn't hold (checked before touching the
        /// pool, so a failed requirement never costs a pool slot).
        public bool TryRecruitWarlock()
        {
            if (_portal == null || !MeetsJoinRequirements() || !_portal.TryTakeFromPool(WarlockAgent.CreatureKind))
            {
                return false;
            }

            SpawnWarlock(_portal.Coord, _ownerId);
            return true;
        }

        /// Public so ConversionClassManager can reuse this exact "capsule +
        /// Initialize" spawn code when a tormented Warlock prisoner wins
        /// its conversion roll and rejoins the domain, instead of
        /// duplicating it.
        public void SpawnWarlock(Vector2Int coord, int ownerId = 0)
        {
            var worldPos = _grid.GridToWorld(coord);

            var visual = CreatureNetView.HostActive
                ? CreatureNetView.CreateHostBody(EditorCreatureKind.Warlock, worldPos)
                : CreatureFactory.CreateOfflineBody(EditorCreatureKind.Warlock, worldPos);

            var agent = visual.AddComponent<WarlockAgent>();
            agent.Initialize(_grid, _lairManager, _tavernManager, _libraryManager, _trainingRoomManager, _treasuryManager, _portal, ownerId);
            CreatureNetView.HostFinalize(visual, EditorCreatureKind.Warlock, agent.Creature);
            GameplayLog.Write(agent.Creature.OwnerId, $"{agent.Name} joined via the Portal at ({coord.x},{coord.y})");
        }
    }
}

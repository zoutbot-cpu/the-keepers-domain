using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Creatures;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Net;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// Recruits Gremlins out of the Portal's pool — per the design rule that
    /// every non-Imp creature has to "join" the domain by coming down the
    /// portal stairway, a Gremlin can only come into being by successfully
    /// taking one out of Portal.TryTakeFromPool, spawned at the Portal's own
    /// coord. On top of pool availability, a Gremlin's own join
    /// requirements (see MeetsJoinRequirements) all have to hold too.
    public class GremlinSpawner : MonoBehaviour
    {
        /// Training Room tiles (summed across every placed Training Room)
        /// required before a Gremlin can join, per the design brief.
        private const int RequiredTrainingRoomTiles = 9;

        private DungeonGrid _grid;
        private Portal _portal;
        private LairManager _lairManager;
        private SlimeHatcheryManager _slimeHatcheryManager;
        private TrainingRoomManager _trainingRoomManager;
        private TavernManager _tavernManager;
        private TreasuryManager _treasuryManager;
        private int _ownerId;

        public void Initialize(DungeonGrid grid, Portal portal, LairManager lairManager, SlimeHatcheryManager slimeHatcheryManager, TrainingRoomManager trainingRoomManager, TavernManager tavernManager, TreasuryManager treasuryManager, int ownerId = 0)
        {
            _grid = grid;
            _portal = portal;
            _lairManager = lairManager;
            _slimeHatcheryManager = slimeHatcheryManager;
            _trainingRoomManager = trainingRoomManager;
            _tavernManager = tavernManager;
            _treasuryManager = treasuryManager;
            _ownerId = ownerId;
        }

        /// How many Gremlins are still available to recruit from the
        /// Portal's pool — read by BottomMenuBar to show the Recruit
        /// button's count (separately from whether it's currently enabled —
        /// see CanRecruit, which also checks MeetsJoinRequirements).
        public int AvailableToRecruit => _portal != null ? _portal.GetPoolCount(GremlinAgent.CreatureKind) : 0;

        /// Whether a recruit attempt would actually succeed right now —
        /// pool availability plus every join requirement. Read by
        /// BottomMenuBar to enable/disable the Recruit button.
        public bool CanRecruit => AvailableToRecruit > 0 && MeetsJoinRequirements();

        /// A Gremlin's join requirements, on top of pool availability:
        /// - At least one Lair with no monster currently resting in it.
        /// - Fewer non-Imp creatures already in the domain than there are
        ///   Slime Hatchery tiles (across every placed Hatchery).
        /// - At least RequiredTrainingRoomTiles Training Room tiles placed
        ///   (across every placed Training Room).
        /// Gremlin, Warlock, Maze Rattler, and Bean Counter (see
        /// WarlockSpawner/MazeRattlerSpawner/BeanCounterSpawner) are all
        /// counted as "non-Imp creatures" for the Hatchery requirement —
        /// any further non-Imp creature type needs to be added to this
        /// population count too. Elf deliberately isn't counted here —
        /// it's never recruited through this gate at all (see
        /// ElfSpawner.SpawnElf), only ever created as a conversion outcome.
        public bool MeetsJoinRequirements()
        {
            if (_lairManager == null || !_lairManager.HasUnclaimedLair())
            {
                return false;
            }

            var nonImpCount = GremlinAgent.CountForOwner(_ownerId) + WarlockAgent.CountForOwner(_ownerId) + MazeRattlerAgent.CountForOwner(_ownerId) + BeanCounterAgent.CountForOwner(_ownerId);
            if (_slimeHatcheryManager == null || nonImpCount >= _slimeHatcheryManager.TotalTileCount)
            {
                return false;
            }

            if (_trainingRoomManager == null || _trainingRoomManager.TotalTileCount < RequiredTrainingRoomTiles)
            {
                return false;
            }

            return true;
        }

        /// Takes one Gremlin out of the Portal's pool and spawns it at the
        /// Portal's own coord ("coming down the stairway"). Returns whether
        /// it actually happened — fails if the pool has none left, or if
        /// MeetsJoinRequirements doesn't hold (checked before touching the
        /// pool, so a failed requirement never costs a pool slot).
        public bool TryRecruitGremlin()
        {
            if (_portal == null || !MeetsJoinRequirements() || !_portal.TryTakeFromPool(GremlinAgent.CreatureKind))
            {
                return false;
            }

            SpawnGremlin(_portal.Coord, _ownerId);
            return true;
        }

        /// Public so ConversionClassManager can reuse this exact "capsule +
        /// Initialize" spawn code when a tormented Gremlin prisoner wins
        /// its conversion roll and rejoins the domain (see
        /// ConversionClassManager.TryTormentRandomPrisoner), instead of
        /// duplicating it.
        public void SpawnGremlin(Vector2Int coord, int ownerId = 0)
        {
            var worldPos = _grid.GridToWorld(coord);

            var visual = CreatureNetView.HostActive
                ? CreatureNetView.CreateHostBody(EditorCreatureKind.Gremlin, worldPos)
                : CreatureFactory.CreateOfflineBody(EditorCreatureKind.Gremlin, worldPos);

            var agent = visual.AddComponent<GremlinAgent>();
            agent.Initialize(_grid, _lairManager, _tavernManager, _trainingRoomManager, _treasuryManager, _portal, ownerId);
            CreatureNetView.HostFinalize(visual, EditorCreatureKind.Gremlin, agent.Creature);
            GameplayLog.Write(agent.Creature.OwnerId, $"{agent.Name} joined via the Portal at ({coord.x},{coord.y})");
        }
    }
}

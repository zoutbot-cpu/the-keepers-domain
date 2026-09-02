using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.DebugUI;

namespace KeepersDomain.Monsters
{
    /// Spawns Elves directly — unlike every other creature's own Spawner,
    /// there's no Portal pool/MeetsJoinRequirements gate here: an Elf only
    /// ever comes into being as Conversion Class's torment-failure outcome
    /// (see ConversionClassManager.TryTormentRandomPrisoner), never
    /// recruited by the player. SpawnElf is the only entry point.
    public class ElfSpawner : MonoBehaviour
    {
        // Pale, sickly green per "weak and worthless" — noticeably smaller
        // than every other creature's own capsule.
        [SerializeField] private Color _elfColor = new Color(0.75f, 0.85f, 0.68f);
        [SerializeField] private float _elfRadiusScale = 0.18f;
        [SerializeField] private float _elfHeightScale = 0.32f;

        private DungeonGrid _grid;
        private Portal _portal;
        private LairManager _lairManager;
        private TavernManager _tavernManager;
        private TreasuryManager _treasuryManager;

        public void Initialize(DungeonGrid grid, Portal portal, LairManager lairManager, TavernManager tavernManager, TreasuryManager treasuryManager)
        {
            _grid = grid;
            _portal = portal;
            _lairManager = lairManager;
            _tavernManager = tavernManager;
            _treasuryManager = treasuryManager;
        }

        /// Spawns one Elf at coord — called by ConversionClassManager when
        /// a tormented prisoner fails its conversion roll, and by
        /// MinionGrabController if a live Elf is ever grabbed and re-jailed
        /// (same generic capture path every jailable creature uses).
        public void SpawnElf(Vector2Int coord, int ownerId = 0)
        {
            var worldPos = _grid.GridToWorld(coord);

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Elf";
            visual.transform.localScale = new Vector3(_elfRadiusScale, _elfHeightScale, _elfRadiusScale);
            visual.transform.position = worldPos + Vector3.up * _elfHeightScale;
            visual.GetComponent<Renderer>().material.color = _elfColor;
            Destroy(visual.GetComponent<Collider>());

            var agent = visual.AddComponent<ElfAgent>();
            agent.Initialize(_grid, _lairManager, _tavernManager, _treasuryManager, _portal, ownerId);
            GameplayLog.Write($"{agent.Name} shuffled into existence, weak and worthless, at ({coord.x},{coord.y})");
        }
    }
}

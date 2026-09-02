using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Rooms;
using KeepersDomain.Monsters;
using KeepersDomain.Implings;

namespace KeepersDomain.Core
{
    /// One keeper's entire gameplay stack — job board (task lists), Portal
    /// (recruit pool), Throne Room (mana), the nine room managers (Treasury
    /// also holds this keeper's gold), and the six creature spawners.
    /// GameBootstrap.BuildWorld builds one per player in the loaded roster
    /// (exactly one for a freshly generated map) via BuildKeeperContext, and
    /// hands the local player's context to the input controller / HUD /
    /// camera. The DungeonGrid itself stays shared — it owns the tiles,
    /// visuals and pathfinding; only the systems layered on top are per
    /// player.
    ///
    /// Plain reference bundle, not a MonoBehaviour: every field points at a
    /// component GameBootstrap created and Initialize()d. Nothing here ticks.
    public sealed class KeeperContext
    {
        public int OwnerId;
        public bool IsAI;
        public Color Color;
        public Vector2Int ThroneCoord;
        public Vector2Int PortalCoord;

        public ThroneRoom Throne;
        public Portal Portal;
        public BuilderJobBoard JobBoard;

        public LairManager Lair;
        public TreasuryManager Treasury;
        public SlimeHatcheryManager SlimeHatchery;
        public TavernManager Tavern;
        public TrainingRoomManager TrainingRoom;
        public LibraryManager Library;
        public JailManager Jail;
        public ConversionClassManager ConversionClass;
        public BridgeManager Bridge;

        public ImplingSpawner ImplingSpawner;
        public GremlinSpawner GremlinSpawner;
        public WarlockSpawner WarlockSpawner;
        public MazeRattlerSpawner MazeRattlerSpawner;
        public BeanCounterSpawner BeanCounterSpawner;
        public ElfSpawner ElfSpawner;

        /// Every context this session, indexed by OwnerId. Set by
        /// GameBootstrap.BuildWorld once all contexts are built, and reset
        /// to null on BuildWorld entry and in ReturnToMainMenu so a
        /// "Main Menu -> Start Game" bounce never sees stale references.
        public static KeeperContext[] All;

        public static KeeperContext ForOwner(int ownerId) =>
            All != null && ownerId >= 0 && ownerId < All.Length ? All[ownerId] : null;

        /// Sell/destroy whatever room sits on coord through its OWNER's
        /// LairManager (the single generic sell path — LairManager
        /// .TrySellRoom, which now rejects tiles that aren't its keeper's).
        /// Used for cross-owner teardown — a hostile creature wrecking
        /// someone else's room — where the acting agent's own context is
        /// the wrong one to route through. Returns false if the tile has
        /// no room or no resolvable owner.
        public static bool TrySellRoomAt(DungeonGrid grid, Vector2Int coord)
        {
            var tile = grid.GetTile(coord);
            if (!tile.HasRoom)
            {
                return false;
            }

            var owner = ForOwner(tile.OwnerId);
            return owner != null && owner.Lair.TrySellRoom(coord);
        }
    }
}

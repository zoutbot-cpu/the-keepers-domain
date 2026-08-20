using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Rooms
{
    /// The dungeon's central landmark — this project's original-IP stand-in
    /// for the genre's usual "heart" room (no EA names/assets, per the
    /// brief). Phase 1: a raised 3x3 platform with a color orb on top, plus
    /// a Max Mana stat — mana crystals mined from ManaCrystalWall tiles get
    /// deposited here (see ImplingAgent's Depositing state /
    /// DepositManaCrystals) to raise it. The stat itself is read by
    /// BottomMenuBar's top-bar counter rather than shown in-world.
    public class ChaosCore : MonoBehaviour
    {
        private const int PlatformTileSpan = 3;

        /// Distance from the platform's center tile to its corners —
        /// GameBootstrap uses this to place the starting implings there
        /// without duplicating PlatformTileSpan as its own magic number.
        public const int PlatformHalfSize = PlatformTileSpan / 2;

        // Matches Portal's SECOND staircase step height (Portal's per-step
        // formula is cellSize * 0.15f * (i + 1), so step index 1 = 0.15f * 2)
        // — keeps both "special room" landmarks reading as the same visual
        // language rather than an arbitrary independent height. This is the
        // center tile's pedestal height; the surrounding ring sits lower,
        // at RingHeightFactor (Portal's FIRST step height).
        private const float PlatformHeightFactor = 0.15f * 2f;
        private const float RingHeightFactor = 0.15f;

        // "1 mana crystal weighs 1" was the only ratio the brief pinned
        // down; this is the placeholder conversion for what a deposited
        // crystal is actually worth until a real mana-economy pass exists.
        private const int MaxManaPerCrystal = 1;

        // MaxMana's starting value, before any crystals are deposited —
        // the Chaos Core itself provides this much capacity on its own.
        private const int StartingMaxMana = 100;

        [SerializeField] private Color _platformColor = new Color(0.3f, 0.28f, 0.32f);

        // Placeholder until a real player-color selection system exists —
        // this is where that color should be plugged in once it does.
        [SerializeField] private Color _playerColor = new Color(0.25f, 0.55f, 0.95f);

        public Vector2Int Coord { get; private set; }

        /// Total capacity — starts at StartingMaxMana and is further raised
        /// by depositing mined mana crystals (see DepositManaCrystals).
        public int MaxMana { get; private set; }

        /// Held out of the free pool for as long as some living impling
        /// needs it — see ImplingAgent, which reserves on spawn and
        /// releases on death (OnDestroy).
        public int ReservedMana { get; private set; }

        /// What's actually free to spend right now.
        public int CurrentMana => MaxMana - ReservedMana;

        public void Initialize(Vector2Int center, DungeonGrid grid)
        {
            Coord = center;
            transform.position = grid.GridToWorld(center);
            var platformHeight = grid.CellSize * PlatformHeightFactor;
            var ringHeight = grid.CellSize * RingHeightFactor;

            MaxMana = StartingMaxMana;

            grid.SetBlocked(center, true);

            BuildPlatform(grid.CellSize, grid.FloorSurfaceY, platformHeight, ringHeight);
            BuildOrb(grid.CellSize, grid.FloorSurfaceY, platformHeight);
        }

        /// Reserves amount out of CurrentMana if there's enough free,
        /// otherwise leaves everything untouched. Returns whether the
        /// reservation happened, so callers (ImplingSpawner) can bail out
        /// of whatever they were about to do on failure. Pair with
        /// ReleaseMana once whatever held the reservation goes away.
        public bool TryReserveMana(int amount)
        {
            if (amount <= 0 || CurrentMana < amount)
            {
                return false;
            }

            ReservedMana += amount;
            return true;
        }

        /// Frees a reservation previously taken by TryReserveMana — called
        /// by ImplingAgent.OnDestroy so a dead impling's upkeep mana goes
        /// back to the free pool.
        public void ReleaseMana(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            ReservedMana = Mathf.Max(0, ReservedMana - amount);
        }

        /// Deposits mana crystals to raise MaxMana — used by ImplingAgent
        /// once it's carried mana crystals here. Returns amount unchanged
        /// (there's no capacity limit on the Chaos Core, unlike Treasury
        /// gold tiles) so the caller can always clear its inventory.
        public int DepositManaCrystals(int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }

            MaxMana += amount * MaxManaPerCrystal;
            return amount;
        }

        /// Builds the platform as two stacked pieces rather than one flat
        /// 3x3 slab: a ring base spanning all 9 tiles at ringHeight, and a
        /// single-tile pedestal on top of the center that rises the rest of
        /// the way to platformHeight — so only the center tile (where the
        /// orb sits, and which Initialize marks non-walkable) reads as the
        /// "high" part of the Core.
        private void BuildPlatform(float cellSize, float floorSurfaceY, float platformHeight, float ringHeight)
        {
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ring.name = "ChaosCoreRing";
            ring.transform.SetParent(transform, false);
            ring.transform.localPosition = new Vector3(0f, floorSurfaceY + ringHeight * 0.5f, 0f);
            ring.transform.localScale = new Vector3(
                cellSize * PlatformTileSpan * 0.95f,
                ringHeight,
                cellSize * PlatformTileSpan * 0.95f);
            ring.GetComponent<Renderer>().material.color = _platformColor;
            Destroy(ring.GetComponent<Collider>());

            var pedestalHeight = platformHeight - ringHeight;
            var pedestal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pedestal.name = "ChaosCorePedestal";
            pedestal.transform.SetParent(transform, false);
            pedestal.transform.localPosition = new Vector3(0f, floorSurfaceY + ringHeight + pedestalHeight * 0.5f, 0f);
            pedestal.transform.localScale = new Vector3(cellSize * 0.95f, pedestalHeight, cellSize * 0.95f);
            pedestal.GetComponent<Renderer>().material.color = _platformColor;
            Destroy(pedestal.GetComponent<Collider>());
        }

        private void BuildOrb(float cellSize, float floorSurfaceY, float platformHeight)
        {
            var orbDiameter = cellSize * 0.6f;

            var orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            orb.name = "ChaosCoreOrb";
            orb.transform.SetParent(transform, false);
            orb.transform.localPosition = new Vector3(0f, floorSurfaceY + platformHeight + orbDiameter * 0.5f, 0f);
            orb.transform.localScale = Vector3.one * orbDiameter;
            orb.GetComponent<Renderer>().material.color = _playerColor;
            Destroy(orb.GetComponent<Collider>());
        }
    }
}

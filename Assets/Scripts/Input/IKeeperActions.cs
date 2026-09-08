using UnityEngine;
using KeepersDomain.Core;
using KeepersDomain.Grid;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Input
{
    /// A creature spawner that can recruit from the Portal pool -- just the
    /// two bits BottomMenuBar's Recruit buttons show/gate on. The four
    /// recruitable-species spawners implement it (Gremlin/Warlock/
    /// MazeRattler/BeanCounter); Elf and Imp have no recruit path.
    public interface IRecruitSource
    {
        int AvailableToRecruit { get; }
        bool CanRecruit { get; }
    }

    /// Every mutating thing the gameplay UI (TileInteractionController,
    /// BottomMenuBar) does on behalf of the keeper it's driving. Offline
    /// and on the host, LocalKeeperActions runs them straight against that
    /// keeper's own managers -- exactly what the UI used to do inline. On
    /// a networked client there is no real keeper to run them against, so
    /// NetworkedKeeperActions (KeepersDomain.Net) sends a server RPC
    /// instead and the outcome comes back through replication. Non-mutating
    /// UI (tool selection, previews, the inspection readout) never goes
    /// through here.
    public interface IKeeperActions
    {
        void RequestDig(Vector2Int coord);
        void RequestReinforce(Vector2Int coord);
        void RequestBuild(Vector2Int coord);

        void CancelDig(Vector2Int coord);
        void CancelReinforce(Vector2Int coord);
        void CancelBuild(Vector2Int coord);

        void SellRoom(Vector2Int coord);
        void PlaceBridgeTile(Vector2Int coord);
        void PlaceRoom(RoomDesignTool room, Vector2Int start, Vector2Int end);

        void SpawnImpling(Vector2Int coord);
        void ToggleLairClaim(Vector2Int coord);
        void Recruit(EditorCreatureKind kind);

        void SetTerrain(Vector2Int coord, TileType type);
        void SetBedrock(Vector2Int coord);

        void SetDigJobsPaused(bool paused);
        void SetAutoReinforce(bool enabled);
    }

    /// The direct path -- offline single-player and the host. Behaviourally
    /// identical to the inline manager calls the UI held before the seam
    /// existed; the null guards match the `?.` the UI used to spread
    /// around.
    public sealed class LocalKeeperActions : IKeeperActions
    {
        private readonly KeeperContext _ctx;
        private readonly DungeonGrid _grid;

        public LocalKeeperActions(KeeperContext ctx, DungeonGrid grid)
        {
            _ctx = ctx;
            _grid = grid;
        }

        public void RequestDig(Vector2Int coord) => _grid.RequestDig(coord, _ctx.OwnerId);
        public void RequestReinforce(Vector2Int coord) => _grid.RequestReinforce(coord, _ctx.OwnerId);
        public void RequestBuild(Vector2Int coord) => _grid.RequestBuild(coord, _ctx.OwnerId);

        public void CancelDig(Vector2Int coord)
        {
            if (_ctx.JobBoard != null && _ctx.JobBoard.CancelJob(coord))
            {
                _grid.CancelDig(coord);
            }
        }

        public void CancelReinforce(Vector2Int coord)
        {
            if (_ctx.JobBoard != null && _ctx.JobBoard.CancelReinforceJob(coord))
            {
                _grid.CancelReinforce(coord);
            }
        }

        public void CancelBuild(Vector2Int coord)
        {
            if (_ctx.JobBoard != null && _ctx.JobBoard.CancelBuildJob(coord))
            {
                _grid.CancelBuild(coord);
            }
        }

        public void SellRoom(Vector2Int coord)
        {
            if (_ctx.Lair != null) _ctx.Lair.TrySellRoom(coord);
        }

        public void PlaceBridgeTile(Vector2Int coord)
        {
            if (_ctx.Bridge != null) _ctx.Bridge.TryPlaceBridgeTile(coord);
        }

        public void PlaceRoom(RoomDesignTool room, Vector2Int start, Vector2Int end)
        {
            switch (room)
            {
                case RoomDesignTool.Lair: _ctx.Lair?.TryPlaceLair(start, end); break;
                case RoomDesignTool.Treasury: _ctx.Treasury?.TryPlaceTreasury(start, end); break;
                case RoomDesignTool.SlimeHatchery: _ctx.SlimeHatchery?.TryPlaceHatchery(start, end); break;
                case RoomDesignTool.Tavern: _ctx.Tavern?.TryPlaceTavern(start, end); break;
                case RoomDesignTool.TrainingRoom: _ctx.TrainingRoom?.TryPlaceTrainingRoom(start, end); break;
                case RoomDesignTool.Library: _ctx.Library?.TryPlaceLibrary(start, end); break;
                case RoomDesignTool.Jail: _ctx.Jail?.TryPlaceJail(start, end); break;
                case RoomDesignTool.ConversionClass: _ctx.ConversionClass?.TryPlaceConversionClass(start, end); break;
            }
        }

        public void SpawnImpling(Vector2Int coord)
        {
            if (_ctx.ImplingSpawner != null) _ctx.ImplingSpawner.SpawnImplingAt(coord);
        }

        public void ToggleLairClaim(Vector2Int coord)
        {
            if (_ctx.Lair != null) _ctx.Lair.ToggleLairClaim(coord);
        }

        public void Recruit(EditorCreatureKind kind)
        {
            switch (kind)
            {
                case EditorCreatureKind.Gremlin: _ctx.GremlinSpawner?.TryRecruitGremlin(); break;
                case EditorCreatureKind.Warlock: _ctx.WarlockSpawner?.TryRecruitWarlock(); break;
                case EditorCreatureKind.MazeRattler: _ctx.MazeRattlerSpawner?.TryRecruitMazeRattler(); break;
                case EditorCreatureKind.BeanCounter: _ctx.BeanCounterSpawner?.TryRecruitBeanCounter(); break;
            }
        }

        public void SetTerrain(Vector2Int coord, TileType type) => _grid.DevPaintTerrain(coord, type);
        public void SetBedrock(Vector2Int coord) => _grid.SetBedrock(coord);

        public void SetDigJobsPaused(bool paused) => _ctx.JobBoard?.SetDigJobsPaused(paused);
        public void SetAutoReinforce(bool enabled) => _ctx.JobBoard?.SetAutoReinforceEnabled(enabled);
    }
}

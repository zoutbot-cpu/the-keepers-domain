using UnityEngine;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Net
{
    /// The client's IKeeperActions -- every mutating UI action becomes a
    /// server RPC on NetGame, and the result comes back through the normal
    /// tile-delta / creature-ghost / room-visual-state replication. No
    /// local effect at all: if the host rejects the action (not your
    /// territory, not enough gold, ...) nothing happens, exactly as the
    /// underlying gameplay method would reject it locally. A no-op until
    /// NetGame.Instance exists (the client world is built before it spawns
    /// only very briefly).
    public sealed class NetworkedKeeperActions : IKeeperActions
    {
        private static NetGame Net => NetGame.Instance;

        public void RequestDig(Vector2Int coord) => Net?.RequestDigRpc(NetCoord.From(coord));
        public void RequestReinforce(Vector2Int coord) => Net?.RequestReinforceRpc(NetCoord.From(coord));
        public void RequestBuild(Vector2Int coord) => Net?.RequestBuildRpc(NetCoord.From(coord));

        public void CancelDig(Vector2Int coord) => Net?.RequestCancelDigRpc(NetCoord.From(coord));
        public void CancelReinforce(Vector2Int coord) => Net?.RequestCancelReinforceRpc(NetCoord.From(coord));
        public void CancelBuild(Vector2Int coord) => Net?.RequestCancelBuildRpc(NetCoord.From(coord));

        public void SellRoom(Vector2Int coord) => Net?.RequestSellRoomRpc(NetCoord.From(coord));
        public void PlaceBridgeTile(Vector2Int coord) => Net?.RequestBridgeTileRpc(NetCoord.From(coord));

        public void PlaceRoom(RoomDesignTool room, Vector2Int start, Vector2Int end) =>
            Net?.RequestPlaceRoomRpc(room, NetCoord.From(start), NetCoord.From(end));

        public void SpawnImpling(Vector2Int coord) => Net?.RequestSummonImplingRpc(NetCoord.From(coord));
        public void ToggleLairClaim(Vector2Int coord) => Net?.RequestToggleLairClaimRpc(NetCoord.From(coord));
        public void Recruit(EditorCreatureKind kind) => Net?.RequestRecruitRpc(kind);

        public void SetTerrain(Vector2Int coord, TileType type) => Net?.RequestSetTerrainRpc(NetCoord.From(coord), (byte)type);
        public void SetBedrock(Vector2Int coord) => Net?.RequestSetBedrockRpc(NetCoord.From(coord));

        public void SetDigJobsPaused(bool paused) => Net?.RequestSetDigJobsPausedRpc(paused);
        public void SetAutoReinforce(bool enabled) => Net?.RequestSetAutoReinforceRpc(enabled);
    }
}

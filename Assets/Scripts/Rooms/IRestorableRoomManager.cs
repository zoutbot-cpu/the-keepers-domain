using UnityEngine;

namespace KeepersDomain.Rooms
{
    /// Lets a saved level rebuild a room through the exact same real
    /// placement path gameplay uses (decoration visuals, gold-free — see
    /// each manager's own RestoreRoom) instead of DungeonGrid.
    /// EditorPlaceRoomTile's bare tile-tagging, which is what used to leave
    /// every reconstructed room as an undecorated placeholder-colored cube.
    /// Implemented by all nine player-buildable room managers.
    ///
    /// start/end is the rectangle corners exactly like every manager's own
    /// TryPlaceX/PlaceStartingX pair already takes — every rectangular room
    /// in this game, merged or not, is always a filled rectangle (see any
    /// manager's TryFindMergeableRoom, which only ever merges when doing so
    /// exactly fills a rectangle), so a saved room's footprint can always
    /// be recovered as a single bounding rectangle. BridgeManager is the
    /// odd one out — a bridge isn't a rectangle, so each of its tiles is
    /// its own 1x1 "room" and RoomReconstruction calls it once per tile
    /// with start == end.
    ///
    /// ownerId: the eight rectangular managers ignore it (their tiles must
    /// already be Claimed Floor — see DungeonGrid.TryAssignRoom — painted
    /// with the right owner by the caller before this runs); BridgeManager
    /// uses it, since a saved bridge tile is restored as plain unclaimed
    /// Water/Lava and TryAssignBridgeRoom is what claims it.
    public interface IRestorableRoomManager
    {
        bool RestoreRoom(Vector2Int start, Vector2Int end, int ownerId);
    }
}

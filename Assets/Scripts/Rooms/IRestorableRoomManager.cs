using UnityEngine;

namespace KeepersDomain.Rooms
{
    /// Lets the Level Designer place/rebuild a room through the exact same
    /// real placement path gameplay uses (decoration visuals, gold-free —
    /// see each manager's own RestoreRoom) instead of DungeonGrid.
    /// EditorPlaceRoomTile's bare tile-tagging, which is what used to leave
    /// every Level Designer room as an undecorated placeholder-colored cube.
    /// Implemented by every player-buildable room manager except
    /// BridgeManager (a painted line over Water/Lava, not a rectangular
    /// room — out of scope for the Level Designer's Rooms menu, see
    /// RoomDesignTool).
    ///
    /// start/end is the rectangle corners exactly like every manager's own
    /// TryPlaceX/PlaceStartingX pair already takes — every room in this
    /// game, merged or not, is always a filled rectangle (see any manager's
    /// TryFindMergeableRoom, which only ever merges when doing so exactly
    /// fills a rectangle), so a saved room's footprint can always be
    /// recovered as a single bounding rectangle. ownerId isn't used by the
    /// managers themselves (a room's tiles must already be Claimed Floor —
    /// see DungeonGrid.TryAssignRoom — which the caller is expected to have
    /// painted, with the right owner, before calling this), it's kept on
    /// the interface purely for the callers' own bookkeeping/consistency.
    public interface IRestorableRoomManager
    {
        bool RestoreRoom(Vector2Int start, Vector2Int end, int ownerId);
    }
}

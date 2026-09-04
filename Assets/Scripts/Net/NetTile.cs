using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Net
{
    /// One tile's replicable state on the wire — coord plus the subset of
    /// TileState the client needs to render it. Everything blittable except
    /// RoomId, sent as a FixedString32 (roomIds are short, e.g.
    /// "Lair_3000001"). The host packs these off DungeonGrid.TileChanged;
    /// the client unpacks them into DungeonGrid.ApplyReplicatedTile. Purely
    /// visual fields (PitDepth is render-only, IsQueuedForDig drives an
    /// icon, ...) are included so the client dungeon looks identical.
    public struct NetTile : INetworkSerializable
    {
        public ushort X;
        public ushort Y;

        public byte Type;              // TileType
        public byte Ownership;         // TileOwnership
        public int OwnerId;
        public bool IsReinforced;
        public bool IsBedrock;
        public bool IsQueuedForDig;
        public bool IsQueuedForReinforce;
        public bool IsQueuedForBuild;
        public bool IsBlocked;
        public byte WallResourceType;  // WallResourceType
        public int Hp;
        public float PitDepth;
        public FixedString32Bytes RoomId;

        public static NetTile From(Vector2Int coord, TileState t)
        {
            return new NetTile
            {
                X = (ushort)coord.x,
                Y = (ushort)coord.y,
                Type = (byte)t.Type,
                Ownership = (byte)t.Ownership,
                OwnerId = t.OwnerId,
                IsReinforced = t.IsReinforced,
                IsBedrock = t.IsBedrock,
                IsQueuedForDig = t.IsQueuedForDig,
                IsQueuedForReinforce = t.IsQueuedForReinforce,
                IsQueuedForBuild = t.IsQueuedForBuild,
                IsBlocked = t.IsBlocked,
                WallResourceType = (byte)t.WallResourceType,
                Hp = t.Hp,
                PitDepth = t.PitDepth,
                RoomId = string.IsNullOrEmpty(t.RoomId) ? default : new FixedString32Bytes(t.RoomId),
            };
        }

        public Vector2Int Coord => new Vector2Int(X, Y);

        public TileState ToTileState()
        {
            var s = TileState.Rock;
            s.Type = (TileType)Type;
            s.Ownership = (TileOwnership)Ownership;
            s.OwnerId = OwnerId;
            s.IsReinforced = IsReinforced;
            s.IsBedrock = IsBedrock;
            s.IsQueuedForDig = IsQueuedForDig;
            s.IsQueuedForReinforce = IsQueuedForReinforce;
            s.IsQueuedForBuild = IsQueuedForBuild;
            s.IsBlocked = IsBlocked;
            s.WallResourceType = (WallResourceType)WallResourceType;
            s.Hp = Hp;
            s.PitDepth = PitDepth;
            // RoomId deliberately NOT applied — a room tile lands on the
            // client as plain Claimed Floor so RoomReconstruction's
            // TryAssignRoom can tag + decorate it (see NetGame). The RoomId
            // is still carried on the wire (above) for NetGame's footprint
            // bookkeeping.
            s.IsBuildable = (TileType)Type != TileType.Rock;
            return s;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref X);
            s.SerializeValue(ref Y);
            s.SerializeValue(ref Type);
            s.SerializeValue(ref Ownership);
            s.SerializeValue(ref OwnerId);
            s.SerializeValue(ref IsReinforced);
            s.SerializeValue(ref IsBedrock);
            s.SerializeValue(ref IsQueuedForDig);
            s.SerializeValue(ref IsQueuedForReinforce);
            s.SerializeValue(ref IsQueuedForBuild);
            s.SerializeValue(ref IsBlocked);
            s.SerializeValue(ref WallResourceType);
            s.SerializeValue(ref Hp);
            s.SerializeValue(ref PitDepth);
            s.SerializeValue(ref RoomId);
        }
    }
}

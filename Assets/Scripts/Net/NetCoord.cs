using Unity.Netcode;
using UnityEngine;

namespace KeepersDomain.Net
{
    /// A bare grid coordinate on the wire — used wherever a message needs
    /// only a tile's (x, y), not a full NetTile (room-manager visual-state
    /// sync: lair claims, treasury gold — see NetGame).
    public struct NetCoord : INetworkSerializable
    {
        public ushort X;
        public ushort Y;

        public static NetCoord From(Vector2Int coord) => new NetCoord { X = (ushort)coord.x, Y = (ushort)coord.y };

        public Vector2Int ToVector2Int() => new Vector2Int(X, Y);

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref X);
            s.SerializeValue(ref Y);
        }
    }
}

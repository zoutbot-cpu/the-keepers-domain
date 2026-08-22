namespace KeepersDomain.Grid
{
    /// Which modular KayKit wall piece a Rock tile should use, purely a
    /// function of which of its 4 cardinal neighbors are also walls — see
    /// WallMeshCatalog for the prefab each shape maps to.
    public enum WallShape
    {
        Isolated,
        EndCap,
        Straight,
        Corner,
        TJunction,
        Cross
    }

    /// Picks a WallShape + Y rotation from a tile's 4 cardinal wall
    /// neighbors. Pure/stateless so DungeonGrid.RefreshVisual can call it
    /// per-tile without needing a scene object.
    ///
    /// Source-mesh facing convention (verify in the Unity Scene view once
    /// imported, adjust WallMeshCatalog.RotationOffsetDegrees by 90 at a
    /// time if every piece is uniformly rotated wrong): each prefab is
    /// assumed authored with its "front" facing local +Z (north) at 0
    /// rotation — EndCap's open connection point faces north, Corner
    /// connects north+east, TJunction connects north+east+west (missing
    /// south), Straight/Cross are rotationally symmetric enough that only
    /// one of their two/four equivalent orientations is ever needed.
    public static class WallAutotiler
    {
        private const int North = 1;
        private const int East = 2;
        private const int South = 4;
        private const int West = 8;

        public static (WallShape Shape, float YRotation) Compute(bool north, bool east, bool south, bool west)
        {
            int mask = (north ? North : 0) | (east ? East : 0) | (south ? South : 0) | (west ? West : 0);
            int count = (north ? 1 : 0) + (east ? 1 : 0) + (south ? 1 : 0) + (west ? 1 : 0);

            switch (count)
            {
                case 0:
                    return (WallShape.Isolated, 0f);

                case 1:
                    // Canonical EndCap connects North; rotate until the
                    // actual single connected side lines up with North.
                    return (WallShape.EndCap, RotationToMatch(mask, North));

                case 2:
                    if (mask == (North | South) || mask == (East | West))
                    {
                        // Straight is symmetric under a 180 flip, so only
                        // two distinct orientations exist (N-S vs E-W).
                        return (WallShape.Straight, mask == (North | South) ? 0f : 90f);
                    }
                    // Corner: canonical piece connects North+East.
                    return (WallShape.Corner, RotationToMatchPair(mask, North | East));

                case 3:
                    // TJunction: canonical piece connects every side except
                    // South (i.e. the "missing" side is South) — rotate
                    // until the actual missing side lines up with South.
                    int missing = ~mask & (North | East | South | West);
                    return (WallShape.TJunction, RotationToMatch(missing, South));

                default:
                    return (WallShape.Cross, 0f);
            }
        }

        /// Degrees of clockwise Y rotation needed to turn canonicalSingleBit
        /// into actualSingleBit, rotating N->E->S->W->N.
        private static float RotationToMatch(int actualSingleBit, int canonicalSingleBit)
        {
            int[] order = { North, East, South, West };
            int from = System.Array.IndexOf(order, canonicalSingleBit);
            int to = System.Array.IndexOf(order, actualSingleBit);
            int steps = (to - from + 4) % 4;
            return steps * 90f;
        }

        /// Same idea as RotationToMatch, but for a 2-bit adjacent pair
        /// (corner): tries each of the 4 rotations of canonicalPair until
        /// it equals actualPair.
        private static float RotationToMatchPair(int actualPair, int canonicalPair)
        {
            int[] order = { North, East, South, West };
            int startIndex = System.Array.IndexOf(order, canonicalPair & -canonicalPair);
            for (int steps = 0; steps < 4; steps++)
            {
                int bitA = order[(startIndex + steps) % 4];
                int bitB = order[(startIndex + steps + 1) % 4];
                if ((bitA | bitB) == actualPair)
                {
                    return steps * 90f;
                }
            }

            return 0f;
        }
    }
}

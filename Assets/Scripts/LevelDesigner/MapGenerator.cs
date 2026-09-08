using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.LevelDesigner
{
    /// Everything the seed-based map generator needs — a plain struct so a
    /// caller (skirmish, the multiplayer lobby's Procedural map, the Level
    /// Designer's Generate button) can spell out exactly one map.
    public struct MapGenSettings
    {
        /// Any int — the same seed + player count + size always produces the
        /// exact same LevelData, so a good layout can be re-rolled or shared.
        public int Seed;

        /// 1-4. 1 is a lone dungeon at the map centre (mostly for the Level
        /// Designer); 2 mirrors east/west; 3-4 use the four map corners.
        public int PlayerCount;

        public int MapWidth;
        public int MapHeight;

        /// Only affects the authored player roster (every keeper human vs.
        /// "human + AI fill") and the LevelData.Multiplayer flag — the
        /// geometry is identical either way.
        public bool Multiplayer;
    }

    /// Builds a fair, seed-reproducible <see cref="LevelData"/> — one Throne
    /// Room with an attached Portal Room per player, a small starting domain
    /// (Treasury + Lair) each, and a resource-wall scatter that is *identical*
    /// in every player's own local frame, so gold and mana crystal yields are
    /// equal between keepers by construction.
    ///
    /// Emitting a LevelData (rather than carving a grid directly) means the
    /// result rides every existing load path unchanged: GameBootstrap.
    /// BuildWorld's data != null branch, per-owner room reconstruction
    /// (RoomReconstruction), the Level Designer's own loader, and JSON
    /// save/load. New generator features (fauna, terrain features, treasure
    /// props, ...) just add more to the same LevelData.
    public static class MapGenerator
    {
        public const int StartingGoldPerPlayer = 1500;
        public const int StartingManaPerPlayer = 200;

        // Resource walls placed per player, stamped identically into each
        // keeper's own rotated local frame (see ScatterResources) so every
        // keeper gets the same count and geometry. Yields are tuned so total
        // minable gold ~= total minable mana per player: a GoldWall drops
        // ~200 gold and a ManaCrystalWall ~100 mana (TileState.*MaxHp *
        // ResourceDropPerHp), so 12 gold walls (~2400) balances 24 crystal
        // walls (~2400). RegeneratingGoldWall is a small renewable bonus on
        // top of that, not part of the balance. Tune here.
        public const int GoldWallsPerPlayer = 12;
        public const int RegeneratingGoldWallsPerPlayer = 3;
        public const int ManaCrystalWallsPerPlayer = 24;

        // A contested core with no resource veins, radius (Chebyshev) in
        // tiles from the map centre. Skipped for a 1-player map.
        private const int NeutralCoreRadius = 5;

        // Keep resource veins at least this far inside the bedrock border.
        private const int BorderKeepout = 3;

        private const int ThroneHalfSize = 2; // 5x5 room, 3x3 platform
        private const int PortalHalfSize = 1; // 3x3 room

        // Portal Room centre, local +X from the throne centre: throne edge
        // (ThroneHalfSize) + a one-tile corridor + the portal's own half.
        // Matches GameBootstrap's fixed starting layout.
        private const int PortalCentreLocalX = ThroneHalfSize + 1 + PortalHalfSize + 1;

        public static LevelData Generate(MapGenSettings settings)
        {
            var playerCount = Mathf.Clamp(settings.PlayerCount, 1, 4);
            var width = Mathf.Max(32, settings.MapWidth);
            var height = Mathf.Max(32, settings.MapHeight);
            var rng = new System.Random(settings.Seed);

            var data = new LevelData
            {
                MapWidth = width,
                MapHeight = height,
                Multiplayer = settings.Multiplayer
            };

            for (int i = 0; i < playerCount; i++)
            {
                data.Players.Add(new LevelPlayerData
                {
                    // First keeper is the human; on a singleplayer map the
                    // rest are AI fill (matches the Level Designer roster
                    // default). Multiplayer keeps every slot human.
                    IsAI = i > 0 && !settings.Multiplayer,
                    ColorIndex = i % LevelDesignerColors.Palette.Length,
                    StartingGold = StartingGoldPerPlayer,
                    StartingMana = StartingManaPerPlayer,
                    StartingBacon = 0
                });
            }

            // One authoritative tile per coord, last writer wins — lets the
            // resource scatter cheaply test "is this cell still plain rock".
            var tiles = new Dictionary<Vector2Int, LevelTileData>();

            void Set(Vector2Int c, LevelTileData t)
            {
                if (c.x < 0 || c.y < 0 || c.x >= width || c.y >= height)
                {
                    return;
                }

                t.X = c.x;
                t.Y = c.y;
                tiles[c] = t;
            }

            // --- bedrock border (matches the Level Designer's blank map) ---
            for (int x = 0; x < width; x++)
            {
                Set(new Vector2Int(x, 0), Bedrock());
                Set(new Vector2Int(x, height - 1), Bedrock());
            }
            for (int y = 0; y < height; y++)
            {
                Set(new Vector2Int(0, y), Bedrock());
                Set(new Vector2Int(width - 1, y), Bedrock());
            }

            // --- per-player anchor + grid-aligned orientation ---
            var center = new Vector2Int(width / 2, height / 2);
            var spawnOffset = Mathf.Max(8, Mathf.Min(width, height) / 2 - 12);
            var anchors = new Vector2Int[playerCount];
            var turns = new int[playerCount];
            ResolveSpawns(playerCount, center, spawnOffset, anchors, turns);

            var kit = BuildLocalKit();

            for (int i = 0; i < playerCount; i++)
            {
                var throneCentre = anchors[i];
                data.Structures.Add(new LevelStructureData
                {
                    Kind = StructureKind.ThroneRoom,
                    X = throneCentre.x,
                    Y = throneCentre.y,
                    OwnerId = i
                });

                var portalCentre = anchors[i] + RotateQuarters(
                    new Vector2Int(PortalCentreLocalX, 0), turns[i]);
                data.Structures.Add(new LevelStructureData
                {
                    Kind = StructureKind.PortalRoom,
                    X = portalCentre.x,
                    Y = portalCentre.y,
                    OwnerId = i
                });

                foreach (var entry in kit)
                {
                    var world = anchors[i] + RotateQuarters(entry.Local, turns[i]);
                    var roomId = entry.RoomPrefix == null ? null : entry.RoomPrefix + "_" + i;
                    Set(world, Floor(i, roomId));
                }

                // Four starting Imps on the Throne platform ring (the centre
                // tile is blocked — see ThroneRoom.Initialize).
                foreach (var off in PlatformRing)
                {
                    var w = anchors[i] + RotateQuarters(off, turns[i]);
                    data.Creatures.Add(new LevelCreatureData
                    {
                        Kind = EditorCreatureKind.Imp,
                        X = w.x,
                        Y = w.y,
                        OwnerId = i
                    });
                }
            }

            ScatterResources(rng, playerCount, width, height, center, anchors, turns, tiles);

            var ordered = new List<LevelTileData>(tiles.Values);
            ordered.Sort((a, b) => a.X != b.X ? a.X - b.X : a.Y - b.Y);
            data.Tiles.AddRange(ordered);
            return data;
        }

        private static readonly Vector2Int[] PlatformRing =
        {
            new Vector2Int(-1, -1), new Vector2Int(1, -1),
            new Vector2Int(-1, 1), new Vector2Int(1, 1)
        };

        private static LevelTileData Bedrock()
        {
            return new LevelTileData { Type = TileType.Rock, IsBedrock = true };
        }

        private static LevelTileData Floor(int ownerId, string roomId)
        {
            return new LevelTileData
            {
                Type = TileType.Floor,
                Ownership = TileOwnership.Claimed,
                OwnerId = ownerId,
                WallResourceType = WallResourceType.None,
                RoomId = roomId
            };
        }

        private struct KitTile
        {
            public Vector2Int Local;

            /// null for plain structure/corridor floor; a RoomDesignTool name
            /// ("Treasury", "Lair") for a tile RoomReconstruction should hand
            /// to that manager. The numeric suffix is added per player.
            public string RoomPrefix;
        }

        /// The starting domain in a keeper's own local frame, throne centre at
        /// the origin, +X pointing toward the attached Portal Room. Deliberately
        /// minimal — Throne + Portal + Treasury + Lair; every other economy
        /// room is player-built. Extend this list to hand out more.
        private static List<KitTile> BuildLocalKit()
        {
            var kit = new List<KitTile>();

            // Throne Room 5x5.
            for (int x = -ThroneHalfSize; x <= ThroneHalfSize; x++)
            {
                for (int y = -ThroneHalfSize; y <= ThroneHalfSize; y++)
                {
                    kit.Add(new KitTile { Local = new Vector2Int(x, y) });
                }
            }

            // Portal Room 3x3 to the east, joined by a one-tile corridor.
            const int portalCx = PortalCentreLocalX;
            kit.Add(new KitTile { Local = new Vector2Int(ThroneHalfSize + 1, 0) });
            for (int x = portalCx - PortalHalfSize; x <= portalCx + PortalHalfSize; x++)
            {
                for (int y = -PortalHalfSize; y <= PortalHalfSize; y++)
                {
                    kit.Add(new KitTile { Local = new Vector2Int(x, y) });
                }
            }

            // Treasury 3x3 to the south, one-tile corridor.
            kit.Add(new KitTile { Local = new Vector2Int(0, -(ThroneHalfSize + 1)) });
            kit.Add(new KitTile { Local = new Vector2Int(0, -(ThroneHalfSize + 2)) });
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -7; y <= -5; y++)
                {
                    kit.Add(new KitTile { Local = new Vector2Int(x, y), RoomPrefix = "Treasury" });
                }
            }

            // Lair 4x4 to the north, one-tile corridor.
            kit.Add(new KitTile { Local = new Vector2Int(0, ThroneHalfSize + 1) });
            kit.Add(new KitTile { Local = new Vector2Int(0, ThroneHalfSize + 2) });
            for (int x = -2; x <= 1; x++)
            {
                for (int y = 5; y <= 8; y++)
                {
                    kit.Add(new KitTile { Local = new Vector2Int(x, y), RoomPrefix = "Lair" });
                }
            }

            return kit;
        }

        /// Fills anchors/turns with each player's throne centre and a grid-
        /// aligned quarter-turn count (0-3) applied to every local offset, so
        /// the whole per-player layout — kit and resources alike — is a clean
        /// rotation of one template. 2 players mirror east/west; 3-4 take the
        /// four corners.
        private static void ResolveSpawns(int playerCount, Vector2Int center, int offset,
            Vector2Int[] anchors, int[] turns)
        {
            if (playerCount <= 1)
            {
                anchors[0] = center;
                turns[0] = 0;
                return;
            }

            if (playerCount == 2)
            {
                anchors[0] = center + new Vector2Int(-offset, 0);
                turns[0] = 0;
                anchors[1] = center + new Vector2Int(offset, 0);
                turns[1] = 2;
                return;
            }

            var corners = new[]
            {
                new Vector2Int(-offset, -offset),
                new Vector2Int(offset, -offset),
                new Vector2Int(offset, offset),
                new Vector2Int(-offset, offset)
            };
            for (int i = 0; i < playerCount; i++)
            {
                anchors[i] = center + corners[i];
                turns[i] = i;
            }
        }

        /// Rotates a local offset by q * 90 degrees CCW about the origin,
        /// staying on the integer grid. q is taken mod 4.
        private static Vector2Int RotateQuarters(Vector2Int v, int q)
        {
            q = ((q % 4) + 4) % 4;
            for (int k = 0; k < q; k++)
            {
                v = new Vector2Int(-v.y, v.x);
            }

            return v;
        }

        /// Places the per-player resource bag. Each candidate cell is rolled
        /// once in the local frame and only committed if it is valid (in
        /// bounds, clear of the bedrock keepout and the neutral core, still
        /// plain rock, no collision) for *every* player's transform — so all
        /// keepers always receive the identical set, exactly fair.
        private static void ScatterResources(System.Random rng, int playerCount, int width, int height,
            Vector2Int center, Vector2Int[] anchors, int[] turns, Dictionary<Vector2Int, LevelTileData> tiles)
        {
            var bag = new List<WallResourceType>();
            for (int i = 0; i < GoldWallsPerPlayer; i++) bag.Add(WallResourceType.GoldWall);
            for (int i = 0; i < RegeneratingGoldWallsPerPlayer; i++) bag.Add(WallResourceType.RegeneratingGoldWall);
            for (int i = 0; i < ManaCrystalWallsPerPlayer; i++) bag.Add(WallResourceType.ManaCrystalWall);

            // Fisher-Yates so the three types intermix spatially.
            for (int i = bag.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (bag[i], bag[j]) = (bag[j], bag[i]);
            }

            var reach = Mathf.Min(width, height) / 2;
            var maxAttempts = bag.Count * 500;
            var placed = 0;
            var attempts = 0;
            var worlds = new Vector2Int[playerCount];

            while (placed < bag.Count && attempts < maxAttempts)
            {
                attempts++;
                var local = new Vector2Int(
                    rng.Next(-reach, reach + 1),
                    rng.Next(-reach, reach + 1));

                var ok = true;
                for (int i = 0; i < playerCount && ok; i++)
                {
                    var wc = anchors[i] + RotateQuarters(local, turns[i]);
                    worlds[i] = wc;

                    if (wc.x <= BorderKeepout || wc.y <= BorderKeepout ||
                        wc.x >= width - 1 - BorderKeepout || wc.y >= height - 1 - BorderKeepout)
                    {
                        ok = false;
                    }
                    else if (playerCount > 1 && Chebyshev(wc, center) <= NeutralCoreRadius)
                    {
                        ok = false;
                    }
                    else if (tiles.ContainsKey(wc))
                    {
                        ok = false;
                    }
                    else
                    {
                        for (int j = 0; j < i && ok; j++)
                        {
                            if (worlds[j] == wc)
                            {
                                ok = false;
                            }
                        }
                    }
                }

                if (!ok)
                {
                    continue;
                }

                var type = bag[placed];
                for (int i = 0; i < playerCount; i++)
                {
                    var wc = worlds[i];
                    tiles[wc] = new LevelTileData
                    {
                        X = wc.x,
                        Y = wc.y,
                        Type = TileType.Rock,
                        WallResourceType = type
                    };
                }

                placed++;
            }

            if (placed < bag.Count)
            {
                Debug.LogWarning($"MapGenerator: only placed {placed}/{bag.Count} resource veins per player " +
                                 "— map may be too small or too crowded for the configured counts.");
            }
        }

        private static int Chebyshev(Vector2Int a, Vector2Int b)
        {
            return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Grid
{
    /// Tile mutations driven by gameplay: dig / reinforce / build job
    /// requests and cancels, dig and room damage, claiming, and room-tile
    /// assignment.
    public partial class DungeonGrid
    {
        /// Queuing a tile for digging and queuing it for reinforcement are
        /// mutually exclusive — rejects if the other is already queued (or
        /// the tile is already reinforced/not Rock), same no-op-on-invalid
        /// pattern as the rest of these request methods. Also rejects
        /// Bedrock outright — "unminable" — see SetBedrock.
        public void RequestDig(Vector2Int coord, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || tile.IsQueuedForDig || tile.IsQueuedForReinforce || tile.IsBedrock)
            {
                return;
            }

            tile.IsQueuedForDig = true;
            tile.QueuedByOwnerId = ownerId;
            RefreshVisual(coord);
            DigRequested?.Invoke(coord, ownerId);
        }

        /// Un-queues a Rock tile. Only meaningful while BuilderJobBoard still
        /// considers the job cancelable (see BuilderJobBoard.CancelJob) — the
        /// caller is expected to check that before calling this.
        public bool CancelDig(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || !tile.IsQueuedForDig)
            {
                return false;
            }

            tile.IsQueuedForDig = false;
            RefreshVisual(coord);
            DigCanceled?.Invoke(coord);
            return true;
        }

        /// Queues a Rock tile to be reinforced (see CompleteReinforce).
        /// Mutually exclusive with digging — same reasoning as RequestDig —
        /// and a no-op on a tile that's already reinforced, since there's no
        /// repair mechanic yet to make re-reinforcing meaningful. Also
        /// rejects resource walls (WallResourceType != None) — reinforcing
        /// a gold seam isn't a thing implings do — and Bedrock, which is
        /// already permanently unminable and has nothing to gain from it.
        public void RequestReinforce(Vector2Int coord, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || tile.IsQueuedForDig || tile.IsQueuedForReinforce || tile.IsReinforced || tile.WallResourceType != WallResourceType.None || tile.IsBedrock)
            {
                return;
            }

            tile.IsQueuedForReinforce = true;
            tile.QueuedByOwnerId = ownerId;
            RefreshVisual(coord);
            ReinforceRequested?.Invoke(coord, ownerId);
        }

        /// Un-queues a Rock tile's reinforce job. Only meaningful while
        /// BuilderJobBoard still considers it cancelable (see
        /// BuilderJobBoard.CancelReinforceJob) — the caller is expected to
        /// check that before calling this.
        public bool CancelReinforce(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || !tile.IsQueuedForReinforce)
            {
                return false;
            }

            tile.IsQueuedForReinforce = false;
            RefreshVisual(coord);
            ReinforceCanceled?.Invoke(coord);
            return true;
        }

        /// Finishes a reinforce job: the wall becomes IsReinforced (darker,
        /// see RefreshVisual) and its HP is topped up to ReinforcedMaxHp —
        /// meaningful even on a tile that's already taken some dig damage,
        /// since reinforcing represents thickening the wall back up.
        /// ownerId stamps the wall's owner so its glowing orb renders in that
        /// player's color (see ApplyOrbOwnerColor / ResolveOwnerColor). -1
        /// leaves whatever owner the tile already had (the plain
        /// single-player / unowned-fallback case).
        public void CompleteReinforce(Vector2Int coord, int ownerId = -1)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            tile.IsQueuedForReinforce = false;
            tile.IsReinforced = true;
            tile.Hp = TileState.ReinforcedMaxHp;
            if (ownerId >= 0)
            {
                tile.OwnerId = ownerId;
            }
            RefreshVisual(coord);
        }

        /// Queues a Claimed, room-free Floor tile to become a wall (see
        /// CompleteBuild) — the reverse of digging, for walling off part of
        /// an already-dug-out domain. Requiring Claimed ownership means a
        /// tile can never be both a pending claim job and a pending build
        /// job at once, with no extra exclusion check needed.
        public void RequestBuild(Vector2Int coord, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Floor || tile.Ownership != TileOwnership.Claimed || tile.HasRoom || tile.IsQueuedForBuild)
            {
                return;
            }

            tile.IsQueuedForBuild = true;
            tile.QueuedByOwnerId = ownerId;
            RefreshVisual(coord);
            BuildRequested?.Invoke(coord, ownerId);
        }

        /// Un-queues a Floor tile's build job. Only meaningful while
        /// BuilderJobBoard still considers it cancelable (see
        /// BuilderJobBoard.CancelBuildJob) — the caller is expected to check
        /// that before calling this.
        public bool CancelBuild(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Floor || !tile.IsQueuedForBuild)
            {
                return false;
            }

            tile.IsQueuedForBuild = false;
            RefreshVisual(coord);
            BuildCanceled?.Invoke(coord);
            return true;
        }

        /// Drops any queued job whose tile turned out not to match it — a
        /// Mine or Reinforce job on a tile that isn't Rock, or a Construct
        /// job on a tile that isn't room-free Floor. Called by FogOfWar the
        /// moment a fogged tile a player queued becomes visible: if the
        /// wall they "selected" out in the dark was actually already dug
        /// (a rival got there first, say), the selection and its board job
        /// just disappear rather than lingering forever. The ordinary
        /// Cancel* methods deliberately refuse a type-mismatched tile, so
        /// this is its own path. Fires the same Canceled events the board
        /// listens to.
        public void ClearStaleQueuedJobs(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            var changed = false;

            if (tile.IsQueuedForDig && tile.Type != TileType.Rock)
            {
                tile.IsQueuedForDig = false;
                DigCanceled?.Invoke(coord);
                changed = true;
            }

            if (tile.IsQueuedForReinforce && tile.Type != TileType.Rock)
            {
                tile.IsQueuedForReinforce = false;
                ReinforceCanceled?.Invoke(coord);
                changed = true;
            }

            if (tile.IsQueuedForBuild && (tile.Type != TileType.Floor || tile.HasRoom))
            {
                tile.IsQueuedForBuild = false;
                BuildCanceled?.Invoke(coord);
                changed = true;
            }

            if (changed)
            {
                RefreshVisual(coord);
            }
        }

        /// Finishes a build job: the tile becomes ordinary Rock again at
        /// full HP. Ownership is left untouched (still Claimed, since
        /// RequestBuild only ever allowed queuing an already-Claimed tile)
        /// so digging it back out later doesn't need a fresh claim job.
        public void CompleteBuild(Vector2Int coord)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            tile.Type = TileType.Rock;
            tile.Hp = TileState.RockMaxHp;
            tile.IsQueuedForBuild = false;
            tile.IsBuildable = false;
            tile.IsReinforced = false;
            tile.IsQueuedForReinforce = false;
            tile.IsQueuedForDig = false;

            RefreshVisual(coord);
        }

        /// Every tile currently tagged with roomId — the level editor's
        /// Remove tool grabs this before selling a room so it can then
        /// reset that exact footprint back to plain Rock (TrySellRoom
        /// leaves the tiles as bare Claimed Floor). Same whole-grid scan
        /// RemoveRoomTiles uses, and must be called before it since
        /// TrySellRoom clears every RoomId as part of the sale.
        public List<Vector2Int> GetRoomFootprint(string roomId)
        {
            var tiles = new List<Vector2Int>();
            if (string.IsNullOrEmpty(roomId))
            {
                return tiles;
            }

            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    if (_tiles[x, y].RoomId == roomId)
                    {
                        tiles.Add(new Vector2Int(x, y));
                    }
                }
            }

            return tiles;
        }

        /// Clears RoomId off every tile belonging to roomId — used to sell a
        /// placed room. Scans the whole grid rather than tracking footprints
        /// separately, which is fine at prototype scale and stays correct
        /// even if a future room type's footprint is bigger than the
        /// current 1x1 Lair. Returns how many tiles it actually cleared, so
        /// the caller (LairManager.TrySellRoom) can refund gold per tile
        /// without needing its own separate footprint count.
        public int RemoveRoomTiles(string roomId)
        {
            var clearedCount = 0;
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    if (_tiles[x, y].RoomId == roomId)
                    {
                        _tiles[x, y].RoomId = null;
                        RefreshVisual(new Vector2Int(x, y));
                        clearedCount++;
                    }
                }
            }

            return clearedCount;
        }

        /// Deals dig damage to a Rock tile. Returns true once the job is done —
        /// either this hit broke through, or the tile was already dug out from
        /// under the caller by another worker sharing the same job.
        /// resourceType/resourceAmount report what a resource-wall tile
        /// (see WallResourceType) yielded from this specific hit — None/0
        /// for a plain wall. The yield is based on however much HP this hit
        /// actually removed (capped at what was left), not the raw damage
        /// number, so an overkill final hit doesn't over-report.
        public bool ApplyDigDamage(Vector2Int coord, int amount, out ResourceType resourceType, out int resourceAmount, int diggerOwnerId = 0)
        {
            resourceType = ResourceType.None;
            resourceAmount = 0;

            if (!InBounds(coord))
            {
                return true;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Rock || tile.IsBedrock)
            {
                // Bedrock is never actually queueable (see RequestDig), so
                // this should never fire in practice — guarded defensively
                // anyway, same "treat as already done" no-op every other
                // invalid-target case here follows, rather than silently
                // applying damage to something meant to be permanent.
                return true;
            }

            if (tile.WallResourceType != WallResourceType.None)
            {
                var hpRemoved = Mathf.Min(amount, tile.Hp);
                resourceType = ToResourceType(tile.WallResourceType);
                resourceAmount = hpRemoved * TileState.ResourceDropPerHp;
            }

            tile.Hp -= amount;
            if (tile.Hp <= 0)
            {
                CompleteDig(coord, diggerOwnerId);
                return true;
            }

            // "regenerate 15hp per hit" — only while the wall survives the
            // hit; a killing blow completes the dig instead (see above), it
            // doesn't get regenerated back to life.
            if (tile.WallResourceType == WallResourceType.RegeneratingGoldWall)
            {
                tile.Hp = Mathf.Min(tile.MaxHp, tile.Hp + TileState.RegeneratingGoldWallRegenPerHit);
            }

            RefreshVisual(coord);
            return false;
        }

        private static ResourceType ToResourceType(WallResourceType wallResourceType)
        {
            switch (wallResourceType)
            {
                case WallResourceType.GoldWall:
                case WallResourceType.RegeneratingGoldWall:
                    return ResourceType.Gold;
                case WallResourceType.ManaCrystalWall:
                    return ResourceType.ManaCrystal;
                default:
                    return ResourceType.None;
            }
        }

        /// Deals damage to a room tile's HP (see TileState.RoomMaxHp) — e.g.
        /// a hostile Gremlin/Warlock attack (see GremlinAgent/WarlockAgent).
        /// Returns true once that tile's HP is fully depleted. This only
        /// tracks the HP — it deliberately doesn't remove anything itself,
        /// since actually tearing down a room correctly (cleaning up every
        /// manager's own tile list/visuals/structures) only works through
        /// LairManager.TrySellRoom; the caller is expected to call that once
        /// this returns true. There's no per-tile removal — depleting one
        /// tile's HP is a signal to destroy the whole room, not just that
        /// tile (see design-doc.md's Happiness section for why).
        public bool ApplyRoomDamage(Vector2Int coord, int amount)
        {
            if (!InBounds(coord))
            {
                return true;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (!tile.HasRoom)
            {
                return true;
            }

            tile.Hp -= amount;
            if (tile.Hp <= 0)
            {
                return true;
            }

            RefreshVisual(coord);
            RoomDamaged?.Invoke(coord, tile.OwnerId);
            return false;
        }

        /// Restores HP to a damaged room tile (see ApplyRoomDamage) — an
        /// impling's repair "jump" (see BuilderJobBoard.ApplyRepairJump/
        /// ImplingAgent's RepairingRoom state). Returns true once the tile
        /// is back at full RoomMaxHp. A no-op-but-true return on a tile
        /// that no longer HasRoom (e.g. sold/destroyed since the repair job
        /// was queued) so the caller's job-completion check doesn't get
        /// stuck waiting on a room that isn't there anymore.
        public bool ApplyRoomRepair(Vector2Int coord, int amount)
        {
            if (!InBounds(coord))
            {
                return true;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (!tile.HasRoom)
            {
                return true;
            }

            tile.Hp = Mathf.Min(TileState.RoomMaxHp, tile.Hp + amount);
            RefreshVisual(coord);
            return tile.Hp >= TileState.RoomMaxHp;
        }

        /// diggerOwnerId is the owner of the impling (or creature) that dug
        /// this tile out — passed straight through to FloorNeedsClaim so the
        /// claim job lands on that player's board.
        public void CompleteDig(Vector2Int coord, int diggerOwnerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            tile.Type = TileType.Floor;
            tile.IsQueuedForDig = false;
            tile.IsBuildable = true;
            // A Floor tile never turns back into Rock, so leftover Rock-only
            // flags/decoration would just be permanently meaningless data.
            tile.IsReinforced = false;
            tile.IsQueuedForReinforce = false;
            tile.WallResourceType = WallResourceType.None;
            ClearWallDecoration(coord);

            RefreshVisual(coord);
            FloorNeedsClaim?.Invoke(coord, diggerOwnerId);
        }

        /// ownerId stamps the claiming player onto the tile — in ordinary
        /// single-player this is 0; on a multi-player map it's whichever
        /// keeper's impling completed the claim job (see
        /// BuilderJobBoard.ApplyClaim). Without this a live-claimed tile
        /// kept OwnerId at its Rock default (-1) and never tinted.
        public void ClaimTile(Vector2Int coord, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type == TileType.Floor && tile.Ownership != TileOwnership.Claimed)
            {
                tile.Ownership = TileOwnership.Claimed;
                tile.OwnerId = ownerId;
                RefreshVisual(coord);
            }
        }

        /// Flips an *already*-Claimed Floor tile to a new owner — the
        /// contested-border case ClaimTile deliberately refuses. Used when
        /// an Imp finishes a claim job on a rival keeper's frontier floor
        /// (see BuilderJobBoard.ApplyClaim / ScanForEnemyClaimCandidates):
        /// territory can now grow into a rival's, one border tile at a
        /// time, and the rival's board can flip it straight back. Never
        /// touches a room tile (a room manager owns that tile's
        /// bookkeeping) or a no-op same-owner call.
        public void ReclaimTile(Vector2Int coord, int ownerId)
        {
            if (!InBounds(coord))
            {
                return;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type == TileType.Floor && tile.Ownership == TileOwnership.Claimed
                && !tile.HasRoom && tile.OwnerId != ownerId)
            {
                tile.OwnerId = ownerId;
                RefreshVisual(coord);
            }
        }

        public bool TryAssignRoom(Vector2Int coord, string roomId)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if (tile.Type != TileType.Floor || tile.Ownership != TileOwnership.Claimed || tile.HasRoom)
            {
                return false;
            }

            tile.RoomId = roomId;
            tile.Hp = TileState.RoomMaxHp;
            RefreshVisual(coord);
            return true;
        }

        /// Same shape as TryAssignRoom, but for Bridge (see BridgeManager),
        /// which sits on Water/Lava instead of ordinary dug Floor — no
        /// Ownership/Claimed requirement to place one (unlike TryAssignRoom,
        /// see BridgeManager.CanPlaceBridgeTile's own adjacency rule
        /// instead). Building the bridge DOES claim its own tile, though —
        /// "bridges claim the lava tile" — so BordersClaimedTile treats it
        /// as claimed territory too, letting an impling claim onward past
        /// it. It still can never host an ordinary room: CanBuildRoomOn
        /// requires Type == Floor, which a bridged Water/Lava tile never is.
        public bool TryAssignBridgeRoom(Vector2Int coord, string roomId, int ownerId = 0)
        {
            if (!InBounds(coord))
            {
                return false;
            }

            ref var tile = ref _tiles[coord.x, coord.y];
            if ((tile.Type != TileType.Water && tile.Type != TileType.Lava) || tile.HasRoom)
            {
                return false;
            }

            tile.RoomId = roomId;
            tile.Hp = TileState.RoomMaxHp;
            tile.Ownership = TileOwnership.Claimed;
            tile.OwnerId = ownerId;
            RefreshVisual(coord);
            return true;
        }
    }
}

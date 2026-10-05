using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using KeepersDomain.Grid;
using KeepersDomain.Input;
using KeepersDomain.CameraControl;
using KeepersDomain.LevelDesigner;
using KeepersDomain.Net;
using KeepersDomain.Rooms;
using KeepersDomain.Implings;
using KeepersDomain.Monsters;
using KeepersDomain.UI;

namespace KeepersDomain.Core
{
    /// The mid-game save / Continue slot — snapshots the running world into a
    /// LevelData and writes it through LevelFileIO. See design-doc.md's
    /// "Saving a game".
    public static class GameSave
    {
        /// The name a mid-game save is written under (see SaveGame) — its
        /// own slot, separate from the "level1" starting template, so
        /// "Start Game" is always a fresh run and "Continue" resumes.
        public const string SaveGameSlot = "savegame";

        /// In-game "Save game" (BottomMenuBar's Settings menu). Snapshots
        /// the live world — full map + rooms, each keeper's gold / mana /
        /// bacon, and every creature's kind / position / owner / level /
        /// exp — into the SaveGameSlot save. Does NOT capture creature
        /// hunger / pay / happiness / current HP / task, queued jobs, or
        /// jail prisoners; those come back at their defaults on Continue.
        public static bool SaveGame()
        {
            var grid = Object.FindAnyObjectByType<DungeonGrid>();
            var contexts = KeeperContext.All;
            if (grid == null || contexts == null || contexts.Length == 0)
            {
                return false;
            }

            var data = CaptureWorldToLevelData(grid, contexts);
            LevelFileIO.Save(SaveGameSlot, data);
            Debug.Log($"GameBootstrap: saved game to '{SaveGameSlot}' ({data.Tiles.Count} tiles, {data.Creatures.Count} creatures).");
            return true;
        }

        /// Snapshots a running world into a LevelData — shared by SaveGame
        /// and SaveStartingLevelAsLevel1. A throwaway LevelDesignerSession
        /// does the tile/creature scan (reusing BuildLevelData rather than
        /// duplicating it); economy + the Throne/Portal structure markers
        /// are filled in per keeper from the live KeeperContext afterward.
        internal static LevelData CaptureWorldToLevelData(DungeonGrid grid, KeeperContext[] contexts)
        {
            var session = SceneSetup.CreateComponent<LevelDesignerSession>("WorldSnapshot");
            session.Initialize(grid, new LevelDesignerProperties
            {
                Multiplayer = contexts.Length > 1,
                PlayerCount = contexts.Length,
                MapWidth = grid.Width,
                MapHeight = grid.Height
            }, roomManagers: null);
            session.CaptureLiveCreatures();

            var data = session.BuildLevelData();

            for (int i = 0; i < contexts.Length && i < data.Players.Count; i++)
            {
                var ctx = contexts[i];
                var p = data.Players[i];
                p.IsAI = ctx.IsAI;
                p.ColorIndex = LevelDesignerColors.NearestIndex(ctx.Color);
                p.StartingGold = ctx.Treasury != null ? ctx.Treasury.TotalGold : 0;
                p.StartingMana = ctx.Throne != null ? ctx.Throne.MaxMana : 100;
                p.StartingBacon = ctx.Tavern != null ? ctx.Tavern.TotalBacon : 0;

                data.Structures.Add(new LevelStructureData { Kind = StructureKind.ThroneRoom, X = ctx.ThroneCoord.x, Y = ctx.ThroneCoord.y, OwnerId = i });
                data.Structures.Add(new LevelStructureData { Kind = StructureKind.PortalRoom, X = ctx.PortalCoord.x, Y = ctx.PortalCoord.y, OwnerId = i });
            }

            Object.Destroy(session.gameObject);
            return data;
        }
    }
}

---
title: Dev Status
---

# The Keeper's Domain — Dev Status

Mobile dig-and-build dungeon management prototype, original IP (inspired by, not derived from, Dungeon Keeper). This page is a hand-maintained snapshot of what's built, what's in progress, and what's next — edit it directly (`index.md` on the `gh-pages` branch) whenever status changes.

**Latest update: "Graphical Start" — v0.0004**

*Last updated: 2026-08-28*

For the full brief and system-by-system design detail, see [project-brief.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/project-brief.md) and [design-doc.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/design-doc.md). Engineering rationale (why things are built the way they are) lives in the [README's Architecture Notes](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/README.md#architecture-notes).

---

## Snapshot

Phase 1's core loop is implemented and playable: **dig → claim → build → impling appears.** Beyond that, the prototype has grown a resource economy, six creature types, nine room types, five terrain tile variants beyond Rock/Floor, a real Jail capture/prisoner mechanic, and a debug/UI layer well past the original Phase 1 brief.

**v0.0004, "Graphical Start," is the biggest single jump the project has had:** most of the dungeon is no longer flat-colored primitive cubes. Walls, floors, the Chaos Core throne, the Portal, and Water/Lava all now render with real modular art (a purpose-bought "dungeon_pack" asset set — the earlier KayKit experiment was tried, found unworkable, and fully reverted before this pack was adopted instead). The Level Designer went from "authors data nothing else uses" to a genuinely complete tool: it now builds the *same real room decorations* gameplay does (not placeholder cubes), tracks per-player ownership on floor **and** reinforced walls with live color updates, and ships a new Edit mode for reassigning who owns an already-placed tile/wall/room/structure/creature. And — the headline architectural change — **the game no longer regenerates a random map every time you press Start.** It now loads a persistent, Level-Designer-editable save (`level1`) if one exists, falling back to procedural generation only on a first-ever run. Edit the starting dungeon in the Level Designer, save, and that's what you play next time.

---

## Phase 1 — Core Loop ✅ Done

- [x] Tap-drag to queue rock tiles for excavation
- [x] Impling auto-digs queued tiles over time
- [x] Lair room placeable once territory claimed
- [x] Impling spawns and works/idles in the Lair
- [x] Pinch-zoom + pan isometric camera
- [x] Territory claimed by an impling task, not a radius or manual tap
- [x] In-game UI for build/impling tools, job priority, creature and task lists
- [x] Resource walls, impling inventory, Treasury + Chaos Core deposits

## Systems Implemented

**Grid & jobs** — Dig, Reinforce, Build, and Claim job pools with reorderable priority; A* pathing (now creature-type-aware — see Terrain, below); HP-based digging with reinforced walls; resource walls (Gold, Regenerating Gold, Mana Crystal) mined into impling inventory.

**Economy** — Gold + mana crystals, Treasury storage, Chaos Core mana pool, per-impling carry weight cap.

**Creatures** — Shared `Creature` base (HP, mana, stats, leveling 1–10, naming). Six creatures live:
- **Imp** — mana-conjured, no Lair/hunger/pay needed, mines for exp.
- **Gremlin** — recruited via Portal pool, has Hunger/Pay/Happiness, trains in Training Room or roams.
- **Warlock** — recruited via Portal pool, intelligent-creature requirements, researches in Library or falls back to training.
- **Maze Rattler** — recruited via Portal pool, requires a placed Jail (5 Rattlers per Jail room); trains in Training Room, otherwise haunts a Jail's pit tiles, otherwise roams. Counts toward the same Hatchery-tile population cap Gremlin/Warlock share.
- **Bean Counter** — recruited via Portal pool, requires a placed Conversion Class; periodically lectures there, torturing a random held prisoner into joining the domain or breaking down into an Elf (see Jail/Conversion Class below), otherwise trains/roams.
- **Elf** — never recruited through the Portal; only ever created as Conversion Class's torment-failure outcome — "weak and worthless," gimped stats, no preferred-room job tier, just Hunger/roam.

**Rooms** — Lair, Treasury, Slime Hatchery, Bacon Beacon, Training Room, Library, Jail, Conversion Class, and Bridge. All (except Bridge — see Terrain, below) sellable through one generic Sell tool; most merge cleanly when extended into a bigger rectangle.
- **Jail** is a sunken pit (5x5 minimum, inset 1 tile from the room's own footprint) ringed by a walkable walkway, a light gray fence, and a staircase/gate entrance — and now has a **real capture/prisoner mechanic**: dragging a misbehaving creature (Gremlin/Warlock/Maze Rattler/Elf) onto a pit tile via the Grab hand imprisons it as inert data until processed.
- **Conversion Class** is where a Bean Counter lectures a random held prisoner — rolls a per-creature-kind chance to rejoin the domain (spawned fresh at the pit tile) or transform into an Elf on failure.

**Terrain** — Water, Lava, Chasm, and Holy Ground tiles beyond Rock/Floor, plus a permanently-unminable Bedrock wall variant, darker than a reinforced wall. Walkability is now creature-type-aware (`DungeonGrid.IsWalkable`'s `isImp` flag): Imps can't cross unbridged Water, and nobody can cross Lava at all until it's bridged, since nothing is fire-resistant yet. **Bridge** is the room that lets creatures (Imps included) cross Water/Lava — placed with a straight-line paint gesture (locks onto whichever axis the drag first moves in) starting adjacent to already-owned territory, and a Lava bridge decays back to unbridged after 5 minutes with no refund. Holy Ground is walkable by everyone but can never be Claimed, so territory can't grow through it. All five tile types are placed today via a dev-only Build-menu tool ("[Dev] Place ..." buttons), standing in for a real map generator that doesn't exist yet.

**UI/Debug** — Permanent bottom menu bar (Build/Impling/Creatures/Tasks), F1/F2 debug panels, `Logs/gameplay-debug.log` for timing-sensitive bug repro. A new **Main Menu** (logo + Start/Level Designer) now gates entry into the game.

**Art & Visuals (new in v0.0004)** — Real modular art from a purpose-bought "dungeon_pack" asset set, replacing placeholder cubes across most of the dungeon:
- **Walls** — a real mesh per wall type (Stone, Gold, Regenerating Gold, Mana Crystal, Bedrock, Reinforced), full tile width with no gaps between neighbors. Reinforced walls are a multi-material mesh (brick body + stone cap + a glowing orb) — the orb is now tinted **per owner**, swapping in a dedicated material clone per player instead of one color shared by the whole map, so different players' walls read as different colors at once.
- **Floors** — real Claimed/Unclaimed floor textures (4 tile variants for Claimed, tile-hashed so neighbors don't look identical); Claimed floor is additionally tinted toward its owner's color where ownership tracking applies.
- **Chaos Core & Portal** — both now use real dungeon_pack props (a throne centerpiece, a portal/stairway prop) in place of the old primitive platform/staircase (kept as a code fallback).
- **Water & Lava** — real animated tiles (scrolling water, pulsing lava glow) instead of flat-colored cubes.
- **Wall selection & queued-action icons** — tapping a wall now shows a yellow inverted-hull outline (`DungeonGrid.SetSelectedWall`); queued dig/reinforce/build state shows as a floating pickaxe/shield/hammer+frame icon instead of a color tint, freeing that color channel up for ownership.

**Level Designer & persistent starting level (new in v0.0004)** — What used to be "authors JSON nobody reads" is now a genuinely working tool, and the game's own starting map is now data it produces:
- Placing a room in the Level Designer (or loading a saved one) builds the **exact same real room decorations** gameplay's own room managers build (carpet, nest, bookcases, dummies, coop, shrine, bench, pit/fence) — not a flat placeholder cube — via a new `IRestorableRoomManager`/`RestoreRoom` path shared by all 8 non-Bridge room managers, wired into the Level Designer with gold costs disabled and background simulation (Slime breeding, auto-reinforce scanning) turned off so editing a level never silently runs gameplay in the background.
- Per-tile ownership now covers Reinforced walls too, not just Claimed floor, with a live color refresh the moment a player's color is changed (previously required a reload).
- A new **Edit mode**: tap an already-placed tile, wall, room, structure, or creature and reassign which player owns it.
- **"Start Game" now loads a persistent `level1` save if one exists** instead of generating a fresh random map every launch (`GameBootstrap.StartGame`/`BuildWorld(LevelData)`) — real gameplay room managers, job board, and creature spawners reconstruct the saved map, including creatures as actual live agents (not the Level Designer's inert markers). Procedural generation still exists and runs automatically on a first-ever install (auto-saving its own output as `level1`), but from then on the Level Designer is how you shape what "Start Game" plays.

## In Progress / Partially Implemented

- **Room durability** — every room tile tracks 50 HP and Unhappy/Angry creatures can chip it down, but there's no HP UI and no repair mechanic.
- **Mana economy** — crystals raise Max Mana 1-for-1; this ratio is a placeholder, not a tuned economy.
- **Happiness/Hunger/Pay consequences** — hitting 0 happiness makes a creature leave or attack the dungeon; starving below 50 hunger currently has no extra penalty (decay just continues).
- **Ownership model is Level-Designer/tile-only so far** — no live creature agent (Impling/Gremlin/Warlock/Maze Rattler/Bean Counter/Elf) carries an owner/player field yet, and reassigning a Structure's (Chaos Core/Portal) owner in the new Edit mode updates the saved data but doesn't yet retint its throne visual live. Both are fine for the current single-player-only prototype, both block real multiplayer.
- **Bridge rooms aren't part of room reconstruction** — the one room type still excluded from `IRestorableRoomManager`/the Level Designer's Rooms menu; a saved Bridge tile still loads as a flat placeholder cube.
- **No "delete a room" tool in the Level Designer** — rooms can be placed and reassigned, not yet removed, from that UI.

## Not Started

- Combat system (no PvE/PvP yet — Imp's "Mine" attack only works on walls/other Imps)
- Imp → full-size Imp growth (noted in brief, unimplemented)
- Per-creature/per-level stat scaling curves (stats are flat placeholders past level 1)
- Real art for creatures/rooms — still cubes/capsules; only walls/floors/Chaos Core/Portal/Water/Lava have real art so far
- Additional creature races beyond the current six
- Skill slots 2–6 (only slot 1, the basic attack, is defined for any creature)
- Saving mid-game progress — only the very first "Start Game" run auto-saves itself as `level1`; there's no in-play "save my current game" flow yet, so playing doesn't persist beyond that starting snapshot
- Real multiplayer (the Level Designer authors multiple players/colors, but live gameplay is still single-player; see the ownership-model gap above)

## Known Placeholder Values (revisit before balancing)

| System | Placeholder | Where |
| --- | --- | --- |
| Mana Crystal → Max Mana | 1:1 | `ChaosCore.MaxManaPerCrystal` |
| Bacon per meal | 1 (fully restores hunger) | `Hunger.cs` |
| Wage | 5 gold/level, every 10 min | `Pay.cs` |
| Happiness decay/recovery | ±20-30 per 10 min, -15/missed payday | `Happiness.cs` |
| Room cost | 20 gold/tile (Training Room, Library, Jail, Conversion Class) | per-room managers |
| Bridge cost | 15 gold/tile, instant | `BridgeManager.CostPerTile` |
| Bridge Lava decay | 5 minutes, no refund | `BridgeManager` |
| Maze Rattler stats | Reuses Gremlin's stat block verbatim (80 HP, 3.5 Movespeed, 15 Strength, 0.8 Attackspeed) | `MazeRattlerAgent.cs` |
| Slime → Bacon | 1 slime = 4 bacon | Bacon Beacon |
| Exp per Mine hit | 5 | `ImplingAgent.cs` |
| Exp per train/research tick | Training +20 / Library +5, every 2s | Training Room / Library managers |
| Conversion Class join chance | Gremlin 80%, Warlock 30%, Maze Rattler 55%, other Evil 50% | `ConversionClassManager.cs` |

## Next Steps (TODO)

- [ ] Extend ownership beyond tiles: give live creature agents an owner/player field, and make Structure (Chaos Core/Portal) reassignment retint the throne visual live
- [ ] Bring Bridge into the same `IRestorableRoomManager` reconstruction as the other 8 room types
- [ ] Add a "delete a room" tool to the Level Designer
- [ ] A real "save my current game" flow, distinct from the one-time starting-level snapshot
- [ ] First real combat pass (targeting, damage, death) — currently the only "combat" is unhappy creatures chipping walls/rooms
- [ ] Decide and implement Imp → full Imp growth trigger
- [ ] Replace placeholder per-level stat curves with real per-creature scaling
- [ ] Start swapping remaining primitive placeholders (creatures, rooms) for real art/models
- [ ] Room durability: add UI feedback and a repair path
- [ ] Real procedural placement for Water/Lava/Chasm/Holy Ground/Bedrock, replacing the dev-only placement tool

---

## Reference

- [README](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/README.md) — setup instructions, controls, full architecture notes
- [design-doc.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/design-doc.md) — creature/room/terrain design detail
- [project-brief.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/project-brief.md) — original Phase 1 brief
- [Assets/Scripts](https://github.com/zoutbot-cpu/the-keepers-domain/tree/main/Assets/Scripts) — source

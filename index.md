---
title: Dev Status
---

# The Keeper's Domain — Dev Status

Mobile dig-and-build dungeon management prototype, original IP (inspired by, not derived from, Dungeon Keeper). This page is a hand-maintained snapshot of what's built, what's in progress, and what's next — edit it directly (`index.md` on the `gh-pages` branch) whenever status changes.

*Last updated: 2026-08-22*

For the full brief and system-by-system design detail, see [project-brief.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/project-brief.md) and [design-doc.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/design-doc.md). Engineering rationale (why things are built the way they are) lives in the [README's Architecture Notes](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/README.md#architecture-notes).

---

## Snapshot

Phase 1's core loop is implemented and playable: **dig → claim → build → impling appears.** Beyond that, the prototype has grown a resource economy, six creature types, nine room types, five terrain tile variants beyond Rock/Floor, a real Jail capture/prisoner mechanic, and a debug/UI layer well past the original Phase 1 brief. A Level Designer subsystem (multiplayer-aware level authoring, JSON save/load) and real modular dungeon wall art are both now in progress, gated behind a new Main Menu.

Everything gameplay-facing is still placeholder primitives (cubes/capsules) — real art is only just starting to land, for walls first (see below).

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

## In Progress / Partially Implemented

- **Level Designer** — a separate menu bar (`LevelDesignerMenuBar`) for authoring multiplayer-aware levels: player slots/colors, map painting, room/creature placement, and JSON save/load (`LevelFileIO`, under `Application.persistentDataPath`). How much of this is wired end-to-end (vs. still being built out) hasn't been fully verified against a Play-mode pass yet.
- **Real wall art** — an autotiler (`WallAutotiler`/`WallMeshCatalog`) picks a modular KayKit dungeon wall mesh per Rock tile based on its 4 cardinal wall neighbors, replacing the placeholder cube — first piece of real (non-primitive) art in the project. Rollout/verification status not yet confirmed in-Editor.
- **Room durability** — every room tile tracks 50 HP and Unhappy/Angry creatures can chip it down, but there's no HP UI and no repair mechanic.
- **Mana economy** — crystals raise Max Mana 1-for-1; this ratio is a placeholder, not a tuned economy.
- **Happiness/Hunger/Pay consequences** — hitting 0 happiness makes a creature leave or attack the dungeon; starving below 50 hunger currently has no extra penalty (decay just continues).

## Not Started

- Combat system (no PvE/PvP yet — Imp's "Mine" attack only works on walls/other Imps)
- Imp → full-size Imp growth (noted in brief, unimplemented)
- Per-creature/per-level stat scaling curves (stats are flat placeholders past level 1)
- Real art for anything besides walls — creatures/rooms are still cubes/capsules
- Additional creature races beyond the current six
- Skill slots 2–6 (only slot 1, the basic attack, is defined for any creature)
- A real procedural map generator (Water/Lava/Chasm/Holy Ground/Bedrock are dev-tool-placed only, see Terrain above)
- Live gameplay use of Level Designer-authored levels (save/load exists; whether `GameBootstrap` can actually load one to play hasn't been confirmed)

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

- [ ] Finish and verify the Level Designer end-to-end (author a level, load it into an actual Play session)
- [ ] Finish rolling out real wall art and confirm it renders correctly across all wall shapes
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

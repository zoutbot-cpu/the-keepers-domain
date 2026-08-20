---
title: Dev Status
---

# The Keeper's Domain — Dev Status

Mobile dig-and-build dungeon management prototype, original IP (inspired by, not derived from, Dungeon Keeper). This page is a hand-maintained snapshot of what's built, what's in progress, and what's next — edit it directly (`index.md` on the `gh-pages` branch) whenever status changes.

*Last updated: 2026-08-20*

For the full brief and system-by-system design detail, see [project-brief.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/project-brief.md) and [design-doc.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/design-doc.md). Engineering rationale (why things are built the way they are) lives in the [README's Architecture Notes](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/README.md#architecture-notes).

---

## Snapshot

Phase 1's core loop is implemented and playable: **dig → claim → build → impling appears.** Beyond that, the prototype has grown a resource economy, four creature types, seven room types, and a debug/UI layer well past the original Phase 1 brief.

Everything is still placeholder primitives (cubes/capsules) — no real art yet.

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

**Grid & jobs** — Dig, Reinforce, Build, and Claim job pools with reorderable priority; A* pathing; HP-based digging with reinforced walls; resource walls (Gold, Regenerating Gold, Mana Crystal) mined into impling inventory.

**Economy** — Gold + mana crystals, Treasury storage, Chaos Core mana pool, per-impling carry weight cap.

**Creatures** — Shared `Creature` base (HP, mana, stats, leveling 1–10, naming). Four creatures live:
- **Imp** — mana-conjured, no Lair/hunger/pay needed, mines for exp.
- **Gremlin** — recruited via Portal pool, has Hunger/Pay/Happiness, trains in Training Room or roams.
- **Warlock** — recruited via Portal pool, intelligent-creature requirements, researches in Library or falls back to training.
- **Maze Rattler** — recruited via Portal pool, requires a placed Jail (5 Rattlers per Jail room); trains in Training Room, otherwise wanders a Jail's pit tiles ("haunts the prisoners" — flavor movement only, no capture mechanic exists yet), otherwise roams. Counts toward the same Hatchery-tile population cap Gremlin/Warlock share.

**Rooms** — Lair, Treasury, Slime Hatchery, Bacon Beacon, Training Room, Library, Jail. All sellable through one generic Sell tool; all merge cleanly when extended into a bigger rectangle. Jail is a sunken pit (5x5 minimum, inset 1 tile from the room's own footprint) ringed by a walkable walkway, a light gray fence, and a staircase/gate entrance — placement and visuals only, see below for what's still missing.

**UI/Debug** — Permanent bottom menu bar (Build/Impling/Creatures/Tasks), F1/F2 debug panels, `Logs/gameplay-debug.log` for timing-sensitive bug repro.

## In Progress / Partially Implemented

- **Jail** — footprint, pit, fence, and staircase visuals are done, and the Maze Rattler (its target creature) is fully implemented and recruitable; the actual capture/prisoner mechanic itself is **not implemented** — Maze Rattlers currently just wander the pit as flavor, with nothing to capture or guard yet.
- **Room durability** — every room tile tracks 50 HP and Unhappy/Angry creatures can chip it down, but there's no HP UI and no repair mechanic.
- **Mana economy** — crystals raise Max Mana 1-for-1; this ratio is a placeholder, not a tuned economy.
- **Happiness/Hunger/Pay consequences** — hitting 0 happiness makes a creature leave or attack the dungeon; starving below 50 hunger currently has no extra penalty (decay just continues).

## Not Started

- Combat system (no PvE/PvP yet — Imp's "Mine" attack only works on walls/other Imps)
- Imp → full-size Imp growth (noted in brief, unimplemented)
- Per-creature/per-level stat scaling curves (stats are flat placeholders past level 1)
- Real art — everything is still cubes/capsules
- Additional creature races beyond Imp/Gremlin/Warlock/Maze Rattler
- Skill slots 2–6 (only slot 1, the basic attack, is defined for any creature)
- Multiple maps / per-map recruit pools (`Portal.SeedPool` is hardcoded for the one existing map)

## Known Placeholder Values (revisit before balancing)

| System | Placeholder | Where |
| --- | --- | --- |
| Mana Crystal → Max Mana | 1:1 | `ChaosCore.MaxManaPerCrystal` |
| Bacon per meal | 1 (fully restores hunger) | `Hunger.cs` |
| Wage | 5 gold/level, every 10 min | `Pay.cs` |
| Happiness decay/recovery | ±20-30 per 10 min, -15/missed payday | `Happiness.cs` |
| Room cost | 20 gold/tile (Training Room, Library, Jail) | per-room managers |
| Maze Rattler stats | Reuses Gremlin's stat block verbatim (80 HP, 3.5 Movespeed, 15 Strength, 0.8 Attackspeed) | `MazeRattlerAgent.cs` |
| Slime → Bacon | 1 slime = 4 bacon | Bacon Beacon |
| Exp per Mine hit | 5 | `ImplingAgent.cs` |
| Exp per train/research tick | Training +20 / Library +5, every 2s | Training Room / Library managers |

## Next Steps (TODO)

- [ ] Design and implement the Jail capture/prisoner mechanic (the Maze Rattler creature itself is done)
- [ ] First real combat pass (targeting, damage, death) — currently the only "combat" is unhappy creatures chipping walls/rooms
- [ ] Decide and implement Imp → full Imp growth trigger
- [ ] Replace placeholder per-level stat curves with real per-creature scaling
- [ ] Start swapping primitive placeholders for real art/models
- [ ] Room durability: add UI feedback and a repair path

---

## Reference

- [README](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/README.md) — setup instructions, controls, full architecture notes
- [design-doc.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/design-doc.md) — creature/room design detail
- [project-brief.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/project-brief.md) — original Phase 1 brief
- [Assets/Scripts](https://github.com/zoutbot-cpu/the-keepers-domain/tree/main/Assets/Scripts) — source

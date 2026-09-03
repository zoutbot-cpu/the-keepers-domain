---
title: Dev Status
---

# The Keeper's Domain — Dev Status

Mobile dig-and-build dungeon management prototype, original IP (inspired by, not derived from, Dungeon Keeper). This page is a hand-maintained snapshot of what's built, what's in progress, and what's next — edit it directly (`index.md` on the `gh-pages` branch) whenever status changes.

**Latest update: "Creature Combat" — v0.0007**

*Last updated: 2026-09-03*

For the full brief and system-by-system design detail, see [project-brief.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/project-brief.md) and [design-doc.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/design-doc.md). Engineering rationale (why things are built the way they are) lives in the [README's Architecture Notes](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/README.md#architecture-notes).

---

## Snapshot

Phase 1's core loop is implemented and playable: **dig → claim → build → impling appears.** Beyond that, the prototype has grown a resource economy, six creature types, nine room types, five terrain tile variants beyond Rock/Floor, a real Jail capture/prisoner mechanic, a debug/UI layer well past the original Phase 1 brief, and — over the last two updates — a first pass at both **multiplayer** (per-keeper systems, owner-tinted visuals) and **creature-vs-creature combat**.

**v0.0006, "Multiplayer Basics," splits the single-player gameplay stack into one per keeper.** Every player in a loaded roster now gets their own `KeeperContext` — its own job board, Portal + recruit pools, Throne Room mana pool, nine room managers (Treasury holds that keeper's gold), and six creature spawners — all layered on the one shared grid. Every live creature carries an owner now (`Creature.OwnerId`), not just tiles and walls. On a multi-keeper level each roster's reinforced-wall orbs, creature health rings, and claimed floor render in that player's own color. A debug player switcher (bottom bar, or number keys 1–9) repoints input, the grab hand, the HUD, and the camera at any keeper's stack so each roster can be inspected during testing; the view opens on the local player's Throne Room. Territory growth, the auto-reinforce sweep, room-placement eligibility, and population caps are all per-keeper, and room IDs are minted in disjoint per-owner bands so one keeper selling a room can't tear down another's tiles. **No AI drives the non-local keepers yet** — their creatures just run their own autonomous behavior off their own systems — and there is **no netcode**: this is per-keeper systems in one local process, not online play. This release also quietly gave **Treasury, Slime Hatchery, Jail, and Bridge** real dungeon_pack art (floor textures + prop meshes), leaving Conversion Class as the one room still on primitive cubes.

**v0.0007, "Creature Combat," is the first creature-vs-creature fighting pass** — built, and **first playtested on 2026-09-03**: the core fight loop holds up (both keepers engage across any claimed floor), with the numbers still needing a real balance pass and **jailing flagged for polish** (deferred). Each keeper holds a directional **stance** toward every other — **Aggressive** (the default), **Neutral**, or **Friendly** — so combat only actually happens on a multi-keeper level. A composed `Combatant` on every creature handles a throttled aggro scan (a new 5-tile radius stat, gated by a line-of-sight grid trace), nearest-single-target, chase-to-a-tile-beside-the-target-and-hold, melee on the `1/Attackspeed` cadence, `Armor` as flat damage reduction, `Lifesteal`, combat exp (+1 per damage dealt, +0.5 per taken), an assist/alarm broadcast when a creature is hit, and break-off rules for low HP (flee to the Throne Room), hunger, bad mood, being grabbed, or chasing too far from where the fight started. A creature knocked to 0 HP **faints** rather than dies: it drops as a draggable **downed body** (10%-MaxHP "finish" buffer, comes to on its own after a minute) that a friendly Imp can haul to a Lair to recover, or an enemy Imp can haul to a Jail — the player's Grab hand can carry bodies too. Permadeath only happens if a body is deliberately finished off (an off-by-default **"Finish off enemies"** Setting) or dropped on Lava/Chasm. Imps flee every hostile except enemy Imps. The **Throne Room is now attackable** — 1000 HP, regenerating 10/sec, with a health "foot-circle" that only shows when it's hurt; there is **no lose-condition** on it hitting 0 yet. Every hit is logged, keeper-tagged (`[P1]` / `[P2]`).

---

## Phase 1 — Core Loop ✅ Done

- [x] Tap-drag to queue rock tiles for excavation
- [x] Impling auto-digs queued tiles over time
- [x] Lair room placeable once territory claimed
- [x] Impling spawns and works/idles in the Lair
- [x] Pinch-zoom + pan isometric camera
- [x] Territory claimed by an impling task, not a radius or manual tap
- [x] In-game UI for build/impling tools, job priority, creature and task lists
- [x] Resource walls, impling inventory, Treasury + Throne Room deposits

## Systems Implemented

**Grid & jobs** — Dig, Reinforce, Build, and Claim job pools with reorderable priority; A* pathing (creature-type-aware — see Terrain); HP-based digging with reinforced walls; resource walls (Gold, Regenerating Gold, Mana Crystal) mined into impling inventory. Imps also auto-rescue knocked-out allies (carry to a Lair) and haul knocked-out enemies to a Jail — ahead of every other job kind.

**Economy** — Gold + mana crystals, Treasury storage, Throne Room mana pool, per-impling carry weight cap. Gold and the mana pool are now per-keeper (see Multiplayer).

**Multiplayer (basics)** — Each player in a loaded roster gets a full **`KeeperContext`**: its own job board, Portal + recruit pools, Throne Room, nine room managers, and six creature spawners, on the one shared grid. Every live creature carries `Creature.OwnerId`. Owner-tinted visuals (reinforced-wall orbs, health rings, claimed floor) on multi-keeper levels. A debug player switcher (bottom bar / number keys) repoints input, grab hand, HUD, and camera at any keeper's stack. Per-keeper territory growth, auto-reinforce, room-placement eligibility, population caps; disjoint per-owner room-ID bands. **No opponent AI, no netcode** — see In Progress.

**Creatures** — Shared `Creature` base (HP, mana, stats, leveling 1–10, naming, owner). Six creatures live:
- **Imp** — mana-conjured, no Lair/hunger/pay needed, mines for exp; flees all hostiles except enemy Imps; runs the rescue/capture carry jobs.
- **Gremlin** — recruited via Portal pool, has Hunger/Pay/Happiness, trains in Training Room or roams.
- **Warlock** — recruited via Portal pool, intelligent-creature requirements, researches in Library or falls back to training.
- **Maze Rattler** — recruited via Portal pool, requires a placed Jail; trains, otherwise haunts a Jail's pit tiles, otherwise roams.
- **Bean Counter** — recruited via Portal pool, requires a placed Conversion Class; lectures there, tormenting a random held prisoner into joining the domain or breaking down into an Elf, otherwise trains/roams.
- **Elf** — never recruited; only ever created as Conversion Class's torment-failure outcome — "weak and worthless," gimped stats, Hunger/roam only.

All six now carry a composed **`Combatant`** (below).

**Combat (first pass — see the v0.0007 note above)** — Directional per-keeper stances (`StanceRegistry`: Aggressive default / Neutral / Friendly). Aggro scan (5-tile stat + line-of-sight grid trace), nearest single target, melee on `1/Attackspeed`, `Armor` flat reduction, `Lifesteal`, combat exp, assist/alarm on being hit, break-off for HP≤20% (flee to Throne) / hunger / mood / grabbed / leash-from-engagement-spot. **Downed bodies**: 0 HP = faint (agent disabled, not destroyed); 10%-MaxHP finish buffer; 60s come-to; permadeath only on a deliberate finish (off-by-default Setting) or a Lava/Chasm drop. A creature hauled into a Jail (by an Imp or the Grab hand) stays parked in the pit as its own capsule and slowly patches itself up. **The Throne Room is attackable** (`IAttackTarget`): 1000 HP, +10/sec regen, hidden-at-full health ring, rallies nearby defenders when hit; no lose-condition on 0 HP yet. All hits keeper-tagged in `Logs/gameplay-debug.log`.

**Rooms** — Lair, Treasury, Slime Hatchery, Tavern, Training Room, Library, Jail, Conversion Class, and Bridge. All sellable through one generic Sell tool; most merge cleanly when extended (Bridge is the exception — each tile is its own room, never merged).
- **Jail** — a sunken pit ringed by a walkway, fence, and staircase/gate. Prisoners arrive three ways now: the Grab hand dropping a *live* creature on a pit tile (inert-blob prisoner), or an Imp / the Grab hand hauling a *knocked-out* creature in (the creature's own capsule stays in the pit). Held prisoners regen HP.
- **Conversion Class** — a Bean Counter lectures a random held prisoner; rolls a per-creature-kind chance to rejoin the domain or transform into an Elf. The one room still on primitive-cube art (may be reworked before it gets real meshes).
- **Tavern** — converts hauled-in slimes into bacon that non-Imp creatures eat to satisfy hunger. Real furniture/floor art as of v0.0005.

**Terrain** — Water, Lava, Chasm, and Holy Ground beyond Rock/Floor, plus a permanently-unminable Bedrock wall. Walkability is creature-type-aware (Imps can't cross unbridged Water; nobody crosses Lava until it's bridged). **Bridge** lets creatures cross — in-game a straight-line paint gesture that claims territory as it goes (Lava bridges decay after 5 min); in the Level Designer, a free per-tile paint tool. All five terrain tile types are placed today via a dev-only Build-menu tool, standing in for a real map generator.

**UI/Debug** — Permanent bottom menu bar (Build/Impling/Creatures/Tasks/Settings), F1/F2 debug panels, `Logs/gameplay-debug.log` (now keeper-tagged, and logs every combat hit). A **Main Menu** (logo + Start/Level Designer) gates entry. Settings menu carries the "Half wall" view toggle and a default-off **"Finish off enemies"** combat toggle; the top status bar shows Gold / Mana / Bacon / **Throne HP**.

**Art & Visuals** — Real modular art from a purpose-bought "dungeon_pack" set across most of the dungeon: a real mesh per wall type (owner-tinted reinforced orbs), real Claimed/Unclaimed floor textures, real Throne Room / Portal props, animated Water & Lava, and real furniture/floor art for **every room except Conversion Class** (Lair / Training Room / Library / Tavern in v0.0005; Treasury / Slime Hatchery / Jail / Bridge in v0.0006). Creatures are still placeholder capsules; a knocked-out one tips onto its side, and the Throne Room now carries a scaled-up health ring.

**Level Designer & persistent starting level** — Placing a room (or loading a save) builds the exact same real room decorations gameplay builds, via a shared `IRestorableRoomManager`/`RestoreRoom` path — all nine room types now, Bridge included: the Map Design menu has a **Bridge** tool that paints a bridge tile (real plank mesh) onto any Water/Lava it's dragged over, and it round-trips through save/load like everything else. Per-tile ownership covers Reinforced walls; an **Edit mode** reassigns which player owns a tile/wall/room/structure/creature, and a **Remove mode** deletes any placed tile/wall/room/structure/creature regardless of owner (a room takes its whole footprint back to Rock). **"Start Game" loads a persistent `level1` save** if one exists (real room managers, job board, spawners reconstruct it, including creatures as live agents); procedural generation still runs on a first-ever install and auto-saves its output as `level1`.

## In Progress / Partially Implemented

- **Combat had its first playtest (2026-09-03) — the core loop works, the numbers don't yet.** Both keepers engage across any claimed floor and the fight/faint/haul chain runs; but time-to-kill, the 5-tile aggro radius, the 10%-MaxHP faint buffer, the 7-tile leash, the Throne's 1000 HP / 10-per-sec regen, and every per-creature stat block are still placeholders that need a real balance pass.
- **Jailing needs polish** — flagged during the v0.0007 playtest as rough (capture flow / prisoner handling); deferred.
- **No opponent AI.** Non-local keepers' creatures act autonomously (claim a Lair, eat, train, roam) but nothing *directs* them — combat currently only happens by dropping creatures into contact, or default-Aggressive creatures wandering into aggro range of each other or an enemy Throne.
- **No netcode.** "Multiplayer" is per-keeper systems + a debug switcher in one local process. Real online play (host-authoritative is the intended model) is a separate track that hasn't started; combat's single damage funnel and stance lookup are shaped so it can slot in later.
- **No lose-condition** — the Throne clamps at 0 HP and regenerates back; nothing happens when it's emptied.
- **Structure owner retint** — reassigning a Throne Room / Portal owner in the Level Designer's Edit mode updates the saved data but doesn't retint the throne visual live.
- **Room durability** — every room tile tracks 50 HP and Unhappy/Angry creatures chip it down, with a repair job now, but no HP UI.
- **Mana economy** — crystals raise Max Mana 1-for-1, a placeholder ratio.
- **The `GridMover` extraction is deferred** — `Combatant` carries its own copy of the path/move helpers; the five Monster agents still duplicate theirs. (Flagged as the first prerequisite for the netcode track.)

## Not Started

- **PvE combat** — invading hero parties, waves, dungeon defense
- **An AI opponent** to direct a rival keeper's roster (build, recruit, attack)
- **Player attack-commands** — send creatures to a point / "defend here" / guard posts
- **Real netcode** — online host-authoritative multiplayer
- Imp → full-size Imp growth (noted in brief, unimplemented)
- Per-creature/per-level stat scaling curves (stats are flat placeholders past level 1)
- Real art for creatures, and for **Conversion Class** — the one room still on primitive-cube art (may be reworked first)
- Additional creature races beyond the current six
- Skill slots 2–6 (only slot 1, the basic attack, is defined) — where windup / cooldown / projectiles / mana costs / AoE will live
- Saving mid-game progress (only the first "Start Game" run auto-saves itself as `level1`)

## Known Placeholder Values (revisit before balancing)

| System | Placeholder | Where |
| --- | --- | --- |
| Creature combat stats / TTK | Unbalanced — one playtest (2026-09-03), core loop OK | per-agent `_baseStats` |
| Aggro radius | 5 tiles, every creature | `Creature.DefaultAggroRadius` |
| Faint-HP buffer | 10% of MaxHP | `DownedBody.cs` |
| Downed recovery | 60s come-to / 25%-MaxHP/min in a Lair / 5%/min + 10% on entry in a Jail | `DownedBody.cs` |
| Combat leash | 7 tiles from where the fight started | `Combatant.cs` |
| Combat exp | +1 per damage dealt, +0.5 per damage taken | `Combatant.cs` |
| Throne HP / regen | 1000 HP, +10/sec, no lose-condition | `ThroneRoom.cs` |
| Mana Crystal → Max Mana | 1:1 | `ThroneRoom.MaxManaPerCrystal` |
| Bacon per meal | 1 (fully restores hunger) | `Hunger.cs` |
| Wage | 5 gold/level, every 10 min | `Pay.cs` |
| Happiness decay/recovery | ±20-30 per 10 min, -15/missed payday | `Happiness.cs` |
| Room cost | 20 gold/tile (Training Room, Library, Jail, Conversion Class) | per-room managers |
| Bridge cost / Lava decay | 15 gold/tile, instant / 5 min, no refund | `BridgeManager` |
| Maze Rattler stats | Reuses Gremlin's stat block verbatim | `MazeRattlerAgent.cs` |
| Slime → Bacon | 1 slime = 4 bacon | `TavernManager` |
| Exp per Mine hit / train tick | 5 / Training +20, Library +5 (every 2s) | impling + room managers |
| Conversion Class join chance | Gremlin 80%, Warlock 30%, Maze Rattler 55%, other Evil 50% | `ConversionClassManager.cs` |

## Next Steps (TODO)

- [ ] **Balance combat** — TTK, aggro radius, faint-HP, leash, Throne HP/regen (one playtest done, core loop verified; numbers untuned)
- [ ] **Polish jailing** — capture flow / prisoner handling (rough in the v0.0007 playtest)
- [ ] Wire a **lose-condition** to the Throne hitting 0 HP
- [ ] An **AI opponent** so a rival keeper's creatures actually do something
- [ ] **Player attack-commands** (send creatures somewhere, defend a point)
- [ ] Start the **host-authoritative netcode** track — beginning with the `GridMover` extraction and a host-gated simulation tick
- [ ] A **stance UI** (currently every keeper is hard-Aggressive to every other)
- [ ] PvE: invading hero parties
- [ ] Extend the Throne's `IAttackTarget` pattern to other structures worth defending
- [ ] A real "save my current game" flow, distinct from the one-time starting-level snapshot
- [ ] Replace placeholder per-level stat curves with real per-creature scaling
- [ ] Real art for Conversion Class (last room on primitives — possibly after a rework) and for creatures
- [ ] Real procedural placement for Water/Lava/Chasm/Holy Ground/Bedrock

---

## Reference

- [README](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/README.md) — setup instructions, controls, full architecture notes
- [design-doc.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/design-doc.md) — creature/room/terrain/combat design detail
- [project-brief.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/project-brief.md) — original Phase 1 brief
- [Assets/Scripts](https://github.com/zoutbot-cpu/the-keepers-domain/tree/main/Assets/Scripts) — source

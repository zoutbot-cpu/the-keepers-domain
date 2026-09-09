---
title: Dev Status
---

# The Keeper's Domain — Dev Status

Mobile dig-and-build dungeon management prototype, original IP (inspired by, not derived from, Dungeon Keeper). This page is a hand-maintained snapshot of what's built, what's in progress, and what's next — edit it directly (`index.md` on the `gh-pages` branch) whenever status changes.

**Latest update: "Multiplayer Lobby + online play" — v0.0008**

*Last updated: 2026-09-08*

For the full brief and system-by-system design detail, see [project-brief.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/project-brief.md) and [design-doc.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/design-doc.md). Engineering rationale (why things are built the way they are) lives in the [README's Architecture Notes](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/README.md#architecture-notes).

---

## Snapshot

Phase 1's core loop is implemented and playable: **dig → claim → build → impling appears.** Beyond that, the prototype has grown a resource economy, six creature types, nine room types, five terrain tile variants beyond Rock/Floor, a real Jail capture/prisoner mechanic, a debug/UI layer well past the original Phase 1 brief, a first pass at **creature-vs-creature combat**, and **online multiplayer** — two builds now connect over a Relay session through a lobby, with the client rendering a replicated world and sending its actions to the host.

**v0.0006, "Multiplayer Basics," splits the single-player gameplay stack into one per keeper.** Every player in a loaded roster now gets their own `KeeperContext` — its own job board, Portal + recruit pools, Throne Room mana pool, nine room managers (Treasury holds that keeper's gold), and six creature spawners — all layered on the one shared grid. Every live creature carries an owner now (`Creature.OwnerId`), not just tiles and walls. On a multi-keeper level each roster's reinforced-wall orbs, creature health rings, and claimed floor render in that player's own color. A debug player switcher (bottom bar, or number keys 1–9) repoints input, the grab hand, the HUD, and the camera at any keeper's stack so each roster can be inspected during testing; the view opens on the local player's Throne Room. Territory growth, the auto-reinforce sweep, room-placement eligibility, and population caps are all per-keeper, and room IDs are minted in disjoint per-owner bands so one keeper selling a room can't tear down another's tiles. **No AI drives the non-local keepers yet** — their creatures just run their own autonomous behavior off their own systems — and there is **no netcode**: this is per-keeper systems in one local process, not online play. This release also quietly gave **Treasury, Slime Hatchery, Jail, and Bridge** real dungeon_pack art (floor textures + prop meshes), leaving Conversion Class as the one room still on primitive cubes.

**v0.0007, "Creature Combat," is the first creature-vs-creature fighting pass** — built, and **first playtested on 2026-09-03**: the core fight loop holds up (both keepers engage across any claimed floor), with the numbers still needing a real balance pass and **jailing flagged for polish** (deferred). Each keeper holds a directional **stance** toward every other — **Aggressive** (the default), **Neutral**, or **Friendly** — so combat only actually happens on a multi-keeper level. A composed `Combatant` on every creature handles a throttled aggro scan (a new 5-tile radius stat, gated by a line-of-sight grid trace), nearest-single-target, chase-to-a-tile-beside-the-target-and-hold, melee on the `1/Attackspeed` cadence, `Armor` as flat damage reduction, `Lifesteal`, combat exp (+1 per damage dealt, +0.5 per taken), an assist/alarm broadcast when a creature is hit, and break-off rules for low HP (flee to the Throne Room), hunger, bad mood, being grabbed, or chasing too far from where the fight started. A creature knocked to 0 HP **faints** rather than dies: it drops as a draggable **downed body** (10%-MaxHP "finish" buffer, comes to on its own after a minute) that a friendly Imp can haul to a Lair to recover, or an enemy Imp can haul to a Jail — the player's Grab hand can carry bodies too. Permadeath only happens if a body is deliberately finished off (an off-by-default **"Finish off enemies"** Setting) or dropped on Lava/Chasm. Imps flee every hostile except enemy Imps. The **Throne Room is now attackable** — 1000 HP, regenerating 10/sec, with a health "foot-circle" that only shows when it's hurt; there is **no lose-condition** on it hitting 0 yet. Every hit is logged, keeper-tagged (`[P1]` / `[P2]`).

**v0.0008, "Multiplayer Lobby + online play," makes two builds actually connect over the internet.** Host Game / Join Game now open a **lobby** (Netcode for GameObjects 2.13.2 over a Unity Relay session — the join code is the room): everyone readies up, the host picks the map, then the host hits Start and builds the authoritative world. The **client runs the host's real gameplay UI** and a render-only world — grid, rooms, creatures (as replicated ghosts), and the per-keeper HUD all stream from the host, with the client simulating nothing of its own. A growing set of client actions round-trip as server RPCs: **queue/cancel dig, summon impling, reinforce, sell, recruit, paint bridge**. Bulk grid replication is throttled (a one-shot full-grid dump overflows a real Relay link). `Tools > Quick Build` drops a zipped Win64 tester build in Downloads, and the default map (`level1`) is now packaged in the build so every player's client starts from the same 96×96 map instead of regenerating its own. **Combat and the full simulation are still host-only** — networking combat resolution is a later milestone.

**2026-09-08 — a playtest-bug pass** (compiles clean; not yet re-verified in the Editor): jailed prisoners stand upright in their cell instead of lying on their side; a defeated **Imp** now vanishes and refunds its reserved upkeep mana instead of leaving a rescuable body that locked the mana forever; **Rescue Ally / Capture Enemy** became reorderable job-priority entries in the Impling menu; a **Jail** must be placed on already-dug floor now (it used to auto-dig rock and silently delete dungeon walls); Imps now **contest a rival's border tiles**, slowly flipping claimed floor along the frontier; a networked **client** can inspect creatures in View mode; the **[Dev] Terrain** tools repaint any non-room tile freely (and can paint plain Floor / Rock back).

**2026-09-08/09 — a seed-based map generator** (same "compiles clean, not Editor-verified" caveat): `MapGenerator.Generate` builds a fair, reproducible `LevelData` — one Throne Room with an attached Portal Room per player, a minimal starting domain each (3×3 Treasury, 4×4 Lair, 4 Imps), a resource-wall scatter, and irregular **water/lava pools** — all *equal between keepers by construction* (the whole per-player layout is a grid-aligned rotation of one local template — 2 players mirror, 3–4 take the map corners — and every vein and every whole pool-blob is only committed where it's valid for every player's transform, so resource yields and terrain hazards match exactly). Pools are grown as blobs (random accretion + a light smooth) so they read as natural lakes; a pool never touches the kit and always leaves a mineable shore around a vein. Because it emits a `LevelData`, it rides the existing loaded-level path unchanged. Reachable from a new Main Menu **"Skirmish (generated)"** button, the multiplayer lobby's **Procedural** map option (now a real N-keeper map — it used to build a single keeper), and a **"Generate Map"** button in the Level Designer's properties screen (seed + Randomize). Not generated yet: Chasms, Holy/Unholy Ground, the other four economy rooms, fauna, props.

**2026-09-08 — a follow-up feature batch** (same "compiles clean, not Editor-verified" caveat): a real **lose-condition** — a Throne Room beaten to 0 HP ends the match with a VICTORY / DEFEAT screen; a **stance editor** in the Settings menu (set each rival keeper's stance instead of everyone being hard-Aggressive); a new **Unholy Ground** terrain tile (Holy Ground's dark twin, mechanically identical for now); the **Maze Rattler** got its own stat block — a fast, fragile skirmisher — instead of a Gremlin copy; the ~1500 lines of byte-identical path/move code across the six creature agents were pulled into one shared **`GridMover`**; and the networked **client's Creatures roster and Tasks list** now populate instead of showing "not available yet" — the roster from replicated creature ghosts (species / level / owner / HP + a coarse activity bucket), the cancelable dig/reinforce/build lists from queued-tile flags, and claim/repair job *counts* off `KeeperNetState`. A **mid-game save**: a "Save game" button in the Settings menu writes a resume slot, and a **Continue** button appears on the Main Menu whenever one exists. And a **multiplayer pause** — either player can pause (freezing the sim for both), opening an overlay where they can Resume, Leave, or cast a **Save & Quit** vote; once both agree the host saves and everyone exits to the menu. Resuming a saved online game is just picking it in the lobby's map list.

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

**Grid & jobs** — Dig, RepairRoom, Reinforce, Build, Claim, plus **RescueAlly / CaptureEnemy** (carry a knocked-out ally to a Lair / a knocked-out enemy to a Jail) — all one reorderable priority list in the Impling menu, RescueAlly/CaptureEnemy at the top by default. A* pathing (creature-type-aware — see Terrain); HP-based digging with reinforced walls; resource walls (Gold, Regenerating Gold, Mana Crystal) mined into impling inventory. A keeper's Imps also slowly **claim a rival's border floor** one tile at a time along a contested frontier.

**Economy** — Gold + mana crystals, Treasury storage, Throne Room mana pool, per-impling carry weight cap. Gold and the mana pool are now per-keeper (see Multiplayer).

**Multiplayer** — *Local split:* each player in a loaded roster gets a full **`KeeperContext`** (its own job board, Portal + recruit pools, Throne Room, nine room managers, six spawners) on the one shared grid; every live creature carries `Creature.OwnerId`; owner-tinted visuals on multi-keeper levels; a debug player switcher (bottom bar / number keys) repoints input, grab hand, HUD, and camera at any keeper's stack; per-keeper territory growth, auto-reinforce, room-placement eligibility, population caps; disjoint per-owner room-ID bands. *Online (v0.0008):* Host / Join over a **Unity Relay** session with a lobby + ready-up + host map-pick (Netcode for GameObjects 2.13.2). The host builds and runs the one authoritative simulation; the client runs the host's real UI over a render-only replicated world and sends actions (dig, summon, reinforce, sell, recruit, bridge) as server RPCs. The client's **View-mode inspect, Creatures roster, and Tasks list** all work now — mostly from state already on the wire (creature ghosts + queued-tile flags), plus a coarse per-creature activity byte and claim/repair job counts added to the replication. `Tools > Quick Build` produces zipped tester builds; `level1` is packaged so all clients share the same map. **Still 2-player capped, no opponent AI, and combat/full-sim networking (M3) hasn't started** — see In Progress.

**Creatures** — Shared `Creature` base (HP, mana, stats, leveling 1–10, naming, owner). Six creatures live:
- **Imp** — mana-conjured, no Lair/hunger/pay needed, mines for exp; flees all hostiles except enemy Imps; runs the rescue/capture carry jobs. Doesn't leave a body when it loses a fight — it just vanishes and its reserved upkeep mana returns to the Throne Room.
- **Gremlin** — recruited via Portal pool, has Hunger/Pay/Happiness, trains in Training Room or roams.
- **Warlock** — recruited via Portal pool, intelligent-creature requirements, researches in Library or falls back to training.
- **Maze Rattler** — recruited via Portal pool, requires a placed Jail; trains, otherwise haunts a Jail's pit tiles, otherwise roams. Its own stat block now — a fast, fragile skirmisher (60 HP, quick feet, fast attacks) — not the Gremlin copy it used to be.
- **Bean Counter** — recruited via Portal pool, requires a placed Conversion Class; lectures there, tormenting a random held prisoner into joining the domain or breaking down into an Elf, otherwise trains/roams.
- **Elf** — never recruited; only ever created as Conversion Class's torment-failure outcome — "weak and worthless," gimped stats, Hunger/roam only.

All six carry a composed **`Combatant`** (below) and a composed **`GridMover`** (the shared A*-route walker, extracted from the ~1500 lines of identical copies the agents used to each hold). Every creature has its **own per-level growth block** — no longer one shared ratio: the Warlock is slower in melee, the Bean Counter and Elf slower still, the Maze Rattler trades HP growth for the fastest speed scaling. Armor still reaches +1 by level 10 for everyone. All numbers unbalanced placeholders.

**Combat (first pass — see the v0.0007 note above)** — Directional per-keeper stances (`StanceRegistry`: Aggressive default / Neutral / Friendly). Aggro scan (5-tile stat + line-of-sight grid trace), nearest single target, melee on `1/Attackspeed`, `Armor` flat reduction, `Lifesteal`, combat exp, assist/alarm on being hit, break-off for HP≤20% (flee to Throne) / hunger / mood / grabbed / leash-from-engagement-spot. **Downed bodies**: 0 HP = faint (agent disabled, not destroyed); 10%-MaxHP finish buffer; 60s come-to; permadeath only on a deliberate finish (off-by-default Setting) or a Lava/Chasm drop — *except an Imp, which just dies and refunds its mana.* A creature hauled into a Jail (by an Imp or the Grab hand) stays parked in the pit as its own capsule, stands upright as a live prisoner, and slowly patches itself up. **The Throne Room is attackable** (`IAttackTarget`): 1000 HP, +10/sec regen, hidden-at-full health ring, rallies nearby defenders when hit. **Reaching 0 HP is that keeper's defeat** — a VICTORY / DEFEAT screen with a Main Menu button; on a networked host the result is pushed to the client too. Stances are editable in the **Settings menu** now (one row per rival keeper) instead of everyone being hard-Aggressive. All hits keeper-tagged in `Logs/gameplay-debug.log`. **Combat itself isn't networked yet** — it resolves host-side only.

**Rooms** — Lair, Treasury, Slime Hatchery, Tavern, Training Room, Library, Jail, Conversion Class, and Bridge. All sellable through one generic Sell tool; most merge cleanly when extended (Bridge is the exception — each tile is its own room, never merged).
- **Jail** — a sunken pit ringed by a walkway, fence, and staircase/gate. Prisoners arrive three ways now: the Grab hand dropping a *live* creature on a pit tile (inert-blob prisoner), or an Imp / the Grab hand hauling a *knocked-out* creature in (the creature's own capsule stays in the pit). Held prisoners regen HP.
- **Conversion Class** — a Bean Counter lectures a random held prisoner; rolls a per-creature-kind chance to rejoin the domain or transform into an Elf. The one room still on primitive-cube art (may be reworked before it gets real meshes).
- **Tavern** — converts hauled-in slimes into bacon that non-Imp creatures eat to satisfy hunger. Real furniture/floor art as of v0.0005.

**Terrain** — Water, Lava, Chasm, Holy Ground, and **Unholy Ground** (Holy Ground's dark twin — near-black tile, red star; mechanically identical for now, a placeholder for a real evil-ground mechanic) beyond Rock/Floor, plus a permanently-unminable Bedrock wall. Walkability is creature-type-aware (Imps can't cross unbridged Water; nobody crosses Lava until it's bridged). **Bridge** lets creatures cross — in-game a straight-line paint gesture that claims territory as it goes (Lava bridges decay after 5 min); in the Level Designer, a free per-tile paint tool. In gameplay, a dev-only Build-menu tool (`[Dev] Terrain`) repaints any non-room tile freely into any of these — or back to plain Floor/Rock. The seed-based map generator emits natural water/lava pools now (Chasm/Holy/Unholy still hand-placed — see Map generation below).

**UI/Debug** — Permanent bottom menu bar (Build/Impling/Creatures/Tasks/Settings), F1/F2 debug panels, `Logs/gameplay-debug.log` (now keeper-tagged, and logs every combat hit). A **Main Menu** (logo + Start/Level Designer) gates entry, and a match-over **EndScreen** exits back to it. Settings menu carries the "Half wall" view toggle, a default-off **"Finish off enemies"** combat toggle, and the **stance editor**; the top status bar shows Gold / Mana / Bacon / **Throne HP**, and the build version is in the top-right corner.

**Art & Visuals** — Real modular art from a purpose-bought "dungeon_pack" set across most of the dungeon: a real mesh per wall type (owner-tinted reinforced orbs), real Claimed/Unclaimed floor textures, real Throne Room / Portal props, animated Water & Lava, and real furniture/floor art for **every room except Conversion Class** (Lair / Training Room / Library / Tavern in v0.0005; Treasury / Slime Hatchery / Jail / Bridge in v0.0006). Creatures are still placeholder capsules; a knocked-out one tips onto its side (a jailed prisoner stands back up), and the Throne Room now carries a scaled-up health ring. The build version number is shown in the top-right corner in-game.

**Level Designer & persistent starting level** — Placing a room (or loading a save) builds the exact same real room decorations gameplay builds, via a shared `IRestorableRoomManager`/`RestoreRoom` path — all nine room types now, Bridge included: the Map Design menu has a **Bridge** tool that paints a bridge tile (real plank mesh) onto any Water/Lava it's dragged over, and it round-trips through save/load like everything else. Per-tile ownership covers Reinforced walls; an **Edit mode** reassigns which player owns a tile/wall/room/structure/creature, and a **Remove mode** deletes any placed tile/wall/room/structure/creature regardless of owner (a room takes its whole footprint back to Rock). **"Start Game" loads the bundled `level1` template** (real room managers, job board, spawners reconstruct it, including creatures as live agents) — always a fresh run.

**Map generation** — `MapGenerator.Generate(MapGenSettings)` (`Assets/Scripts/LevelDesigner/`) builds a fair, **seed-reproducible** `LevelData` — one Throne Room + attached Portal Room per player, a minimal domain each (3×3 Treasury, 4×4 Lair, 4 Imps), a bedrock border, an equal-per-keeper resource-wall scatter, and natural **water/lava pools**. Fairness is structural: the per-player layout is a grid-aligned rotation of one local template (1p centre, 2p mirrored, 3–4p corners), and every vein and every whole pool-blob is only placed where it's valid for *every* player's rotated transform, so resource yields (tuned ~2400 gold ≈ ~2400 mana each) and terrain hazards match exactly. Pools are grown as irregular blobs (random accretion, then two fill + one shave smoothing pass); a pool never touches the kit (2-tile buffer) and leaves a 1-tile mineable shore around every vein. Emitting a `LevelData` means it reuses every existing load path (loaded-level build, per-owner room reconstruction, JSON save/load, the Level Designer loader). Reachable from Main Menu **"Skirmish (generated)"** (offline, 2 keepers), the multiplayer lobby's **Procedural** option (now N-keeper), and the Level Designer's **"Generate Map"** button (seed + Randomize). Size scales with player count (64 / 80 / 96). Separate from the legacy fixed-probability `ScatterResourceWalls` on the from-scratch single-player map. **Not generated yet:** Chasms, Holy/Unholy Ground, the other four economy rooms, fauna, props.

**Mid-game save / Continue** — the in-game Settings menu has a **Save game** button (`GameBootstrap.SaveGame` → a `savegame` slot via the same `LevelData`/`LevelFileIO` path). The Main Menu grows a **Continue** button whenever that slot exists; "Start Game" and the match-over screen both delete it. The snapshot captures the map, every room, each keeper's gold / mana / bacon, and every creature's kind / position / owner / **level + exp**. It does **not** capture creature hunger / pay / happiness / current HP / task, queued jobs, or jail prisoners — those reset to defaults on Continue.

**Multiplayer pause** — either player can hit **Pause** (bottom bar, networked games only); a `Paused` netvar drives `Time.timeScale` on both sides so the host sim and the client render freeze, and a modal overlay takes over the HUD. From it: **Resume**, **Leave (no save)**, or a **Vote to Save & Quit** — once every connected player has voted, the host writes the mid-game save and everyone drops to the menu. **Resuming an online save** is just picking `savegame` (shown as "Resume saved game") in the lobby's map list — it rebuilds the 2-keeper world with the client rejoining as keeper 1.

## In Progress / Partially Implemented

- **Combat had its first playtest (2026-09-03) — the core loop works, the numbers don't yet.** Both keepers engage across any claimed floor and the fight/faint/haul chain runs; but time-to-kill, the 5-tile aggro radius, the 10%-MaxHP faint buffer, the 7-tile leash, the Throne's 1000 HP / 10-per-sec regen, and every per-creature stat block are still placeholders that need a real balance pass.
- **Jailing needs polish** — flagged rough in the v0.0007 playtest. The 2026-09-08 pass fixed the parked prisoner lying on its side; still open: confirm a held prisoner actually gets *converted* by a Bean Counter / Conversion Class (that pipeline wasn't touched), and the capture flow generally.
- **No opponent AI.** Non-local keepers' creatures act autonomously (claim a Lair, eat, train, roam) but nothing *directs* them — combat currently only happens by dropping creatures into contact, or default-Aggressive creatures wandering into aggro range of each other or an enemy Throne.
- **Netcode is partway in.** Online host/join over Relay, a lobby, client action-RPCs, and the client's read-only panels (inspect / roster / task list) work (v0.0008); the client renders a replicated world but simulates nothing. **Combat, faint/capture, and the rest of the sim still run host-only** — networking those (host-authoritative resolution of every HP/damage/RNG/transition) is the next milestone and hasn't started.
- **Lose-condition is minimal.** A Throne at 0 HP ends the match with a VICTORY / DEFEAT screen — but there's no AI or networked combat to actually threaten a Throne yet, so it only fires from debug-switcher play or creatures wandering into an enemy Throne.
- **`GridMover` extraction — mostly done.** The 5 Monster agents + the Imp share one now; `Combatant` still holds its own copy (different waypoint shape) and is the remaining piece before the netcode simulation-tick work.
- **Structure owner retint** — reassigning a Throne Room / Portal owner in the Level Designer's Edit mode updates the saved data but doesn't retint the throne visual live.
- **Room durability** — every room tile tracks 50 HP and Unhappy/Angry creatures chip it down, with a repair job now, but no HP UI.
- **Mana economy** — crystals raise Max Mana 1-for-1, a placeholder ratio.

## Not Started

- **PvE combat** — invading hero parties, waves, dungeon defense
- **An AI opponent** to direct a rival keeper's roster (build, recruit, attack)
- **Player attack-commands** — send creatures to a point / "defend here" / guard posts
- **Networked combat & simulation** — host-authoritative resolution of HP/damage/RNG/faint/capture (M3); online connect + client commands already land (v0.0008)
- Imp → full-size Imp growth (noted in brief, unimplemented)
- Real art for creatures, and for **Conversion Class** — the one room still on primitive-cube art (may be reworked first)
- Additional creature races beyond the current six
- Skill slots 2–6 (only slot 1, the basic attack, is defined) — where windup / cooldown / projectiles / mana costs / AoE will live
- Full-fidelity save (the mid-game save skips creature needs / HP / tasks, queued jobs, jail prisoners)

## Known Placeholder Values (revisit before balancing)

| System | Placeholder | Where |
| --- | --- | --- |
| Creature combat stats / TTK | Unbalanced — one playtest (2026-09-03), core loop OK | per-agent `_baseStats` |
| Aggro radius | 5 tiles, every creature | `Creature.DefaultAggroRadius` |
| Faint-HP buffer | 10% of MaxHP | `DownedBody.cs` |
| Downed recovery | 60s come-to / 25%-MaxHP/min in a Lair / 5%/min + 10% on entry in a Jail | `DownedBody.cs` |
| Combat leash | 7 tiles from where the fight started | `Combatant.cs` |
| Combat exp | +1 per damage dealt, +0.5 per damage taken | `Combatant.cs` |
| Throne HP / regen | 1000 HP, +10/sec; 0 HP = that keeper's defeat | `ThroneRoom.cs` |
| Per-level stat growth | Per-creature blocks, hand-differentiated but untuned (Armor still a shared +1-by-10) | per-agent `_growthPerLevel` |
| Mana Crystal → Max Mana | 1:1 | `ThroneRoom.MaxManaPerCrystal` |
| Bacon per meal | 1 (fully restores hunger) | `Hunger.cs` |
| Wage | 5 gold/level, every 10 min | `Pay.cs` |
| Happiness decay/recovery | ±20-30 per 10 min, -15/missed payday | `Happiness.cs` |
| Room cost | 20 gold/tile (Training Room, Library, Jail, Conversion Class) | per-room managers |
| Bridge cost / Lava decay | 15 gold/tile, instant / 5 min, no refund | `BridgeManager` |
| Maze Rattler stats | Own block now (fast/fragile skirmisher, 60 HP) — untuned | `MazeRattlerAgent.cs` |
| Slime → Bacon | 1 slime = 4 bacon | `TavernManager` |
| Exp per Mine hit / train tick | 5 / Training +20, Library +5 (every 2s) | impling + room managers |
| Conversion Class join chance | Gremlin 80%, Warlock 30%, Maze Rattler 55%, other Evil 50% | `ConversionClassManager.cs` |

## Next Steps (TODO)

- [ ] **Editor/MP re-verify the 2026-09-08 changes** — the bug pass (jailed-prisoner upright, Imp-death mana refund, reorderable rescue/capture, Jail pre-dug placement, contested border claiming, client creature inspect, dev terrain repaint) *and* the feature batch (lose-condition screen, stance editor, Unholy Ground, Maze Rattler stats, `GridMover` — creature movement especially, the client Creatures/Tasks panels, the mid-game save → Continue round-trip, and the MP pause / Save & Quit vote / lobby resume) *and* the **map generator** (Skirmish start, lobby Procedural = N keepers, Level Designer "Generate Map" — check throne/portal/corridor connectivity, per-keeper room reconstruction, that the resource scatter + water/lava pools really are symmetric in-Editor, and that no pool blocks a starting route or moats a vein)
- [ ] **Balance combat** — TTK, aggro radius, faint-HP, leash, Throne HP/regen, and every per-creature stat + growth block (all hand-set placeholders)
- [ ] **Polish jailing** — verify held prisoners actually convert (Bean Counter → Conversion Class pipeline), plus the capture flow generally
- [ ] An **AI opponent** so a rival keeper's creatures actually do something (and can threaten a Throne for real)
- [ ] **Player attack-commands** (send creatures somewhere, defend a point)
- [ ] **Network the simulation** (M3) — host-authoritative combat/HP/RNG/faint/capture and a host-gated simulation tick; fold `Combatant`'s path/move into `GridMover` first. Also: lift the 2-player cap, de-hardcode `ClientOwnerId = 1`
- [ ] Decide what **Unholy Ground** actually *does* (currently a Holy Ground clone)
- [ ] A proper **stance UI** — the Settings-menu editor is functional but bare; give it a real screen, and a default other than all-Aggressive if that's wanted
- [ ] PvE: invading hero parties
- [ ] Extend the Throne's `IAttackTarget` pattern to other structures worth defending
- [ ] Deepen the mid-game save — creature needs / HP / task, queued jobs, jail prisoners
- [ ] A single-player pause (the pause + Save & Quit flow is multiplayer-only right now), and a dedicated resume screen rather than the lobby map list
- [ ] Real art for Conversion Class (last room on primitives — possibly after a rework) and for creatures
- [ ] Extend the map generator — Chasms, Holy/Unholy Ground, the other four economy rooms, fauna, treasure props; a seed field in the lobby + Skirmish (both use a random seed today)

---

## Reference

- [README](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/README.md) — setup instructions, controls, full architecture notes
- [design-doc.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/design-doc.md) — creature/room/terrain/combat design detail
- [project-brief.md](https://github.com/zoutbot-cpu/the-keepers-domain/blob/main/Docs/project-brief.md) — original Phase 1 brief
- [Assets/Scripts](https://github.com/zoutbot-cpu/the-keepers-domain/tree/main/Assets/Scripts) — source

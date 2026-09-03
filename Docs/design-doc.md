# Design Doc

Placeholder — paste the full design doc here.

See [project-brief.md](project-brief.md) (Phase 1 brief) for the scope currently implemented in `/Assets/Scripts`.

## Creatures

Shared base for every creature in the game (Imps first, more to follow). This is the common frame every specific creature is defined against — individual creatures (Imp, etc.) get their own subsection once designed.

### Core stats
Every creature has:
- HP, HP regen
- Mana, mana regen
- Strength
- Movespeed
- Attackspeed
- Intelligence
- Craftmanship
- Armor
- Lifesteal

All of these scale up with level. Exact per-level scaling curve is TBD per stat (likely differs by creature — e.g. an Imp scales Strength/Craftmanship more than Intelligence).

### Leveling
- Levels run 1–10.
- Every minion starts at level 1 the moment it comes through the Portal.
- Exp is earned from: completing tasks, training, and combat.
- Exp-per-level curve is per creature type, not shared: exp needed to go from Level to Level+1 is `Level * ExpPerLevelStep`, where `ExpPerLevelStep` is set per creature (see `Creature`'s constructor). A rare/strong unit can be given a higher step to level up slower than a common one, independent of how fast any given exp source grants exp. Exact values per creature: TBD.

### Joining the domain
- Every non-Imp creature has to "join" the domain by coming down the Portal's stairway — it can't be placed/spawned freely the way an Imp can (Imps are mana-conjured, see ImplingSpawner).
- Each Portal owns a pool of available creatures to recruit, depending on the map being played (`Portal.SeedPool`/`GetPoolCount`/`TryTakeFromPool`, keyed by creature kind). Per-map pool data doesn't exist yet — `GameBootstrap` just seeds the pool directly for the one map that exists so far.
- There are also per-race requirements to join, beyond pool availability — see each creature's own subsection for its requirements (e.g. Gremlin, below).

### Naming
Every creature (Imps included) gets a random name at spawn, kept for its whole life — see `Assets/Scripts/Creatures/CreatureNames.cs`.
- Each race has its own pool of 50 names, picked once in `Awake` and never re-rolled: Imp names lean short and goblin-ish, Gremlin names lean short and sneaky/feral, Warlock names lean long and mystical.
- Displayed as `"{name} #{Id}"` (e.g. "Snig #3") so two creatures rolling the same name (a 50-name pool, easy to collide with more than a handful alive) still read as distinct.
- One of the 50 Warlock names is "Tim," mixed in among the grandiose ones, per request.
- Shown in the Creatures debug menu and in Inspect mode (see below) in place of the old generic "Impling#3"/"Gremlin#3" labels.

### Ownership
Every live creature belongs to a player — `Creature.OwnerId`, the same int player index `TileState.OwnerId` uses (0 is the implicit single player in ordinary gameplay; `DungeonGrid.OwnerColors` maps it to a color). Set once from each agent's `Initialize` via `Creature.SetOwner` (the agent builds its `Creature` in `Awake`, before the owner is known, so it can't be a constructor argument).
- Recruits (Portal pool), Imp summons, and Conversion Class outcomes all default to owner 0 — the local Keeper. A converted/transformed prisoner "joins the domain," i.e. becomes the Keeper's.
- Save/load round-trips it: `LevelCreatureData.OwnerId` is written from `Creature.OwnerId` (`LevelDesignerSession.CaptureLiveCreatures`) and restored into the live agent (`GameBootstrap.RestoreWorldCreatures`), instead of the old hard-coded 0.
- `DungeonGrid` gameplay actions now stamp `TileState.OwnerId`: `ClaimTile(coord, ownerId)`, `CarveRoom`/`CarveRect` (ownerId 0 for fresh geometry), `CompleteReinforce(coord, ownerId)` (wall-orb color), `TryAssignBridgeRoom(coord, roomId, ownerId)`. Before this a live-claimed tile kept `OwnerId` at its Rock default (`-1`) and never tinted even with `TintFloorByOwner` on.

#### Per-keeper systems (`KeeperContext`)
Each player in the loaded roster gets a full **`KeeperContext`** (`Assets/Scripts/Core/KeeperContext.cs`) — its own `BuilderJobBoard` (task lists), `Portal` + recruit pools, `ThroneRoom` (mana, seeded from `LevelPlayerData.StartingMana`), the nine room managers (`TreasuryManager` holds that keeper's gold), and the six creature spawners. `GameBootstrap.BuildKeeperContext` builds one; `BuildWorld` builds `KeeperContext[]` (exactly one for a freshly generated map — behaves identically to before) and stores it as `KeeperContext.All` (reset on `BuildWorld` entry and in `ReturnToMainMenu`).
- **Routing**: the shared `DungeonGrid`'s dig/reinforce/build/claim/room-damage events carry the acting owner; each `BuilderJobBoard` early-returns on a mismatch. Territory growth (`BordersClaimedTile(coord, ownerId)`), the auto-reinforce sweep, and `CanBuildRoomOn(coord, ownerId)` are all per-keeper. roomIds are minted in disjoint bands (`ownerId * DungeonGrid.RoomIdOwnerStride`) so one keeper selling a room can't tear down another's tiles. `LairManager.TrySellRoom` rejects tiles that aren't its keeper's; hostile cross-owner room destruction (an unhappy creature) routes through `KeeperContext.TrySellRoomAt` to the owning keeper's manager. `ConversionClassManager` outcomes join the captor.
- Spawner population caps (`MeetsJoinRequirements`) count only that keeper's creatures via `<Species>Agent.CountForOwner(ownerId)`.
- No AI drives non-local keepers yet — their autonomous creature agents just run off their own set. This is the systems half of "Multiplayer — Basic".

#### Local player + debug switcher
Input (`TileInteractionController`), the grab hand (`MinionGrabController`), the HUD (`BottomMenuBar`), and the camera are all built once and bound to the **local player (owner 0)** via `LocalPlayerController`. Grab and Sell are restricted to your own creatures/rooms. On a multi-player level, `BottomMenuBar` shows a **player switcher** (P1..PN, also number keys 1-9) — `LocalPlayerController.SetActivePlayer` aborts any in-progress gesture, repoints all four at that keeper's `KeeperContext`, and recenters the camera on its Throne Room (`IsoCameraController.CenterOn`). Gameplay stays single-player; the switcher is a testing aid.
- The view opens on the **local player's Throne Room**, not the map's geometric middle: `FindStructureCoordOrDefault` resolves each keeper's `StructureKind.ThroneRoom`/`PortalRoom` by `preferredOwnerId` (falling back to first-of-kind, then a spread-out per-index coord), and `CreateIsoCamera` takes owner 0's as its `focusGroundPoint`.
- Owner colors: `BuildWorld` populates `grid.OwnerColors` from the roster palette and sets `TintFloorByOwner = true` when `specs.Length > 1`, mirroring `LevelDesignerSession.RefreshGridOwnerColors`. A single-player game keeps the plain green `PlayerColor` with tinting off.
- Pan bounds are always anchored to the **map center** (`mapCenterCamPos ± panMargin`), never the opening position, so a Throne-Room-focused start still reaches every edge. `panMargin` is `22.5f` for a freshly generated map (unchanged) but scales to `max(W, H) * CellSize / 2 + 10` when a level is loaded — a saved level can be up to 256 tiles per side, and the old fixed `22.5f` left most of a large map unreachable.

#### Health / owner ring
A flat "donut" at each creature's feet (`CreatureHealthRing`, built from primitive cubes like `DungeonGrid.BuildHolyGroundStar`) that doubles as the ownership marker and the health bar:
- 8 equal segments, each worth `MaxHP / 8`. `ceil(HP / MaxHP * 8)` segments are lit.
- Dark-gray track always visible underneath; lit segments fill with the owner's color (`DungeonGrid.GetOwnerColor`).
- Nothing damages a non-attacking creature yet (no combat), so today it always reads as full, in the owner's color — it's the ownership indicator now and the health bar once combat exists.
- The ring GameObject isn't parented to the (variably-scaled) capsule — it follows the creature's X/Z at a fixed floor Y each frame so it stays flat on the ground.

### Inspecting a creature
View mode's tap-to-inspect (see `TileInteractionController.Inspect`) shows every stat/property a creature has, not just a name and position:
- Name, current task/state, position.
- Owner (`Player {OwnerId + 1}`).
- Full stat block via `Creature.DescribeStats()` — level, exp, HP/regen, Mana/regen, Strength, Movespeed, Attackspeed, Intelligence, Craftmanship, Armor, Lifesteal. Same method for every creature type, so this always stays in sync with whatever the Core stats section (above) actually contains.
- Imp-specific: carried Gold/Mana Crystals/Slimes (`ImplingInventory`).
- Gremlin/Warlock-specific: Hunger value (+ "(hungry)" tag), wage + "(unpaid!)" tag, Happiness value and tier.

### Hunger
Non-Imp minions only — Imps don't get hungry. See `Assets/Scripts/Creatures/Hunger.cs`.
- Starts at 100, decays linearly down to 50 over 10 minutes, then keeps decaying at the same rate below 50 (no starvation consequence exists yet — that's just unbuilt, not a deliberate "hunger caps at 50" design).
- At 50 or below, the creature is "hungry" and prioritizes eating (see each creature's priority list below).
- Eating (1 bacon, from a Tavern storage tile with any on it) fully restores hunger to 100. The 1-bacon-per-meal amount is a placeholder, not a tuned value.

### Pay
Non-Imp minions only — Imps don't get paid, the Keeper mana-conjured them into existence rather than recruiting them. See `Assets/Scripts/Creatures/Pay.cs`.
- Wage is 5 gold per level: base 5 at level 1, +5 per level gained (so a level 3 creature draws 15 gold on payday).
- Payday happens every 10 minutes per creature, drawn straight from the Treasury — no walking/task involved, unlike eating.
- If the Treasury can't afford the payment, the creature goes "unhappy" instead (tracked, shown in the Creatures menu) — no desertion/morale consequence exists yet, same unbuilt-consequence pattern Hunger's missing starvation penalty uses.

### Happiness
Non-Imp minions only — Imps don't have moods. See `Assets/Scripts/Creatures/Happiness.cs`.
- 0-100, starts at 60. Driven by Hunger, Pay, and productive work: decays 5 per minute while hungry, recovers 20 over 10 minutes while not (capped at the 60 starting value), takes a flat -15 hit every payday it goes unpaid, gains a flat +5 every payday it's actually paid, and trickles up +1 per minute further (capped at 85 — Ecstatic, 90+, still isn't reachable) while doing a job in its preferred room: Training for Gremlin/Maze Rattler, Researching for Warlock (Training is only Warlock's fallback and doesn't count). All placeholder tuning, not balanced.
- Bands and their behavior, highest first:
  - **90-100 Ecstatic**, **75-90 Enjoying themselves**, **50-75 Happy** — no behavioral difference between these three yet, just mood flavor. 60 is the starting value, inside Happy.
  - **40-50 Getting unhappy** — refuses to do tasks (training/research/roaming — not eating or claiming a Lair, those aren't "tasks").
  - **25-40 Unhappy** — refuses tasks, and occasionally attacks a room or wall instead (see each creature's own AI priority list for the actual roll rate).
  - **10-25 Angry** — refuses tasks, attacks rooms/walls often (same mechanic as Unhappy, higher roll rate).
  - **0-10 Leaving** — overrides every other concern, even hunger/Lair-seeking: paths to the Portal and, on arrival, walks up the stairs and despawns (releasing any Lair tile it had claimed). If no path to the Portal exists at all, it "begins destroying the domain" instead — the same room/wall-attacking behavior Unhappy/Angry use, but unconditional rather than an occasional roll, for as long as it stays stuck.
- Attacking a wall chips away at its HP via the same `DungeonGrid.ApplyDigDamage` a dig job uses; attacking a room chips away at that specific tile's own HP (`TileState.RoomMaxHp`, 50 — see `DungeonGrid.ApplyRoomDamage`) the same way. Both hit at a rate driven by the creature's own Strength/Attackspeed (first real use of those stats on Gremlin/Warlock — see each creature's own stat block). Once the targeted room tile's HP is depleted, the *whole* room is torn down (`LairManager.TrySellRoom`, the same correct whole-room removal every room type already relies on for the player's Sell tool) — there's no true per-tile room removal (a bigger room isn't tougher than a 1-tile one under this model), since none of the room managers support losing a single tile out of an otherwise-intact room yet. A wall, by contrast, is genuinely just that one tile's HP with no wider consequence.

### Room tile repair (current implementation note)
Implings automatically repair damaged room tiles (see Happiness, above, for how they get damaged in the first place) — see `BuilderJobBoard`'s `RepairRoom` job kind and `ImplingAgent`'s `RepairingRoom` state.
- Queued automatically whenever a room tile survives a hit (`DungeonGrid.RoomDamaged`), the same "no player tap needed" way Claim jobs are queued — not player-initiated, and not cancelable.
- Defaults to just below Mining in job priority (`Dig, RepairRoom, Reinforce, Build, Claim`) — an impling finishes whatever it's currently digging/mining before picking up a repair job, but repair still comes ahead of Reinforce/Build/Claim.
- An impling walks directly onto the damaged tile itself (room tiles are normally walkable, unlike a Dig/Reinforce/Build target) and jumps in place, "leaving magical impling sweat that fixes the tile": each landed jump restores 5 HP and leaves a brief glowing droplet on the tile, repeating until it's back to full HP (or the job stops being valid, e.g. the room was sold from under it).
- Jump speed is stat-driven — 1/Movespeed seconds per jump, the same "1/stat" shape Mining's hit interval uses for Attackspeed — so a faster impling repairs faster.
- Damaged room tiles now visibly darken toward how badly hurt they are (same HP-based color lerp Rock walls already use), easing back to full color as they're repaired, instead of silently tracking HP with no visual feedback.

### Skill slots
- Every creature has 6 skill slots. Not all slots need to be filled — a simple creature can use just slot 1.
- Slot 1 is always the creature's basic attack, and it's creature-specific:
  - **Imp**: "Mine" — a weak pickaxe swing, only effective against other Imps and resource objects (walls). Not a real combat attack.
- Slots 2–6: open-ended, filled in as each creature's kit is designed.

### Imp mapping (current implementation note)
`ImplingAgent.cs` is wired onto this base (`Assets/Scripts/Creatures/Creature.cs`):
- Strength drives "Mine"'s damage per hit, Attackspeed drives its hit interval (`1 / Attackspeed`) — level-1 values (Strength 20, Attackspeed 1) match the old hardcoded `_hitDamage`/`_hitInterval` exactly, so this didn't change level-1 behavior.
- Movespeed drives movement, replacing the old `_moveSpeed`.
- Mana, Intelligence, Craftmanship, Armor, and Lifesteal exist on the Imp's stat block but aren't consumed by any gameplay system yet (no combat, no mana-cost abilities, no crafting).
- Per-level growth values on `ImplingAgent` are placeholder tunables, not final balance.
- Exp source is live: every landed "Mine" hit (Digging or Mining, regardless of wall type or whether that hit finishes the wall) grants 5 exp — a solo Imp gets 25 exp mining a plain Rock wall alone (100 HP / 20 dmg = 5 hits), 50 exp for a solo Gold wall (200 HP / 20 dmg = 10 hits). With 2 Imps splitting a wall's hits evenly, it takes ~8 Rock walls or ~4 Gold walls per Imp to reach level 2 (100 exp) under the current placeholder leveling curve above. Training and combat as exp sources aren't implemented yet.

### Gremlin (current implementation note)
The first non-Imp creature. A small, thin, green-blue-ish humanoid — visual is a placeholder capsule ("a green pill") until a real model exists, per `GremlinAgent.cs`/`GremlinSpawner.cs` (`Assets/Scripts/Monsters`).
- Starts with 80 HP. Movespeed 3.5, Strength 15, Attackspeed 0.8 — all placeholders (no design-brief values exist yet), just enough to make movement and the Unhappy/Angry attack behavior (see Happiness, above) work. Every other stat sits at 0 — Gremlin has no crafting behavior yet to consume them.
- Joins via the Portal's pool, seeded with 10 to start (see "Joining the domain" above) — recruiting spawns it at the Portal's own coord.
- Join requirements (all must hold, on top of pool availability): at least 1 free (unclaimed) Lair spot; fewer non-Imp creatures already in the domain than there are Slime Hatchery tiles (summed across every placed Hatchery, counting Warlocks, Maze Rattlers, and Bean Counters too — see below); at least 9 Training Room tiles placed (summed across every placed Training Room). See `GremlinSpawner.MeetsJoinRequirements`.
- AI is a priority list, re-evaluated every frame (higher number = acted on first, preempting whatever's lower). Happiness (see above) gates all of it — Leaving overrides everything, Unhappy/Angry/Getting unhappy refuse the tiers below 80:
  - **100** — no personal Lair claimed yet: claim the nearest unclaimed Lair *tile* (claims are per-tile, not per-room — a multi-tile Lair like the starting one can house one creature per tile) if one is reachable; only if none is, place a brand-new 1x1 Lair at a random reachable, buildable spot and claim that instead.
  - **80** — hungry: go eat at the nearest Tavern storage tile that has bacon (see Hunger, above).
  - **40** — a Training Room exists: train there (+20 exp every 2 seconds) — not standing still: alternates between walking to a training-dummy tile, pausing there 3-5 seconds (randomized per stop), and moving on to a different dummy, for as long as it keeps training. Same shape as Warlock's research (below), but a dummy isn't blocked to pathfinding the way a bookcase is, so it can stand right on one rather than needing an adjacent tile.
  - **30** — no Training Room exists: roam to random reachable floor tiles ("to find combat") instead, pausing briefly at each before picking a new one.

### Warlock (current implementation note)
The second non-Imp creature, and the first "intelligent" creature (Tavern's "food for intelligent creatures" line refers to this). A placeholder capsule like Gremlin's, colored dark purple, until a real model exists — see `WarlockAgent.cs`/`WarlockSpawner.cs` (`Assets/Scripts/Monsters`).
- Starts with 60 HP. Movespeed 2.5, Strength 10, Attackspeed 0.6 — all placeholders, weaker/slower than Gremlin's to read as a heavier caster-type; no design-brief values exist yet. Every other stat sits at 0, same as Gremlin, until a system exists to consume them.
- Joins via the Portal's pool, seeded with 10 to start — recruiting spawns it at the Portal's own coord.
- Join requirements (all must hold, on top of pool availability): at least 1 Lair tile placed anywhere (claimed or not — unlike Gremlin's requirement, which needs a *free* Lair spot); at least one placed Library that's at least 3x3; fewer non-Imp creatures already in the domain (Gremlin + Warlock + Maze Rattler + Bean Counter combined) than there are Slime Hatchery tiles; fewer intelligent creatures already in the domain (only Warlock counts so far) than there are Tavern tiles. See `WarlockSpawner.MeetsJoinRequirements`.
- AI is the same priority-list shape as Gremlin's (Happiness gates it the same way too), re-evaluated every frame:
  - **100** — no personal Lair claimed yet: same as Gremlin's (claim the nearest existing unclaimed Lair if one's reachable, otherwise place a new random 1x1 Lair and claim that).
  - **80** — hungry: same as Gremlin's (nearest Tavern tile with bacon).
  - **40** — a Library exists: research there (+5 exp every 2 seconds) — not standing still: alternates between walking to a bookcase-adjacent tile, pausing there 3-5 seconds (randomized per stop), and moving on to a different bookcase, for as long as it keeps researching. See Library's own entry, below, for why it can't stand on a bookcase tile directly.
  - **30** — no Library exists: train in a Training Room instead (+20 exp every 2 seconds), if one exists — same walk-pause-move-on pattern between dummies Gremlin's training uses, not standing still. If neither a Library nor a Training Room exists, the Warlock just idles — unlike Gremlin, it has no roam fallback.

### Maze Rattler (current implementation note)
The third non-Imp creature — a ratman-ish humanoid, per the Jail room's own brief ("attracts the Maze Rattler"). A placeholder capsule copied straight from Gremlin's shape and stat block, colored brown, until a real model exists — see `MazeRattlerAgent.cs`/`MazeRattlerSpawner.cs` (`Assets/Scripts/Monsters`). No prisoner/capture mechanic exists yet for it to actually interact with — this is the creature and its idle wandering only.
- Same stats as Gremlin (80 HP, Movespeed 3.5, Strength 15, Attackspeed 0.8) — no design-brief values of its own exist yet, so this reuses Gremlin's placeholders rather than inventing new numbers.
- Joins via the Portal's pool, seeded with 5 to start — recruiting spawns it at the Portal's own coord.
- Join requirements (all must hold, on top of pool availability): at least 1 free (unclaimed) Lair spot (same universal "needs somewhere to rest" requirement every recruitable creature has); fewer Maze Rattlers already in the domain than 5 times the number of placed Jail *rooms* (not tiles — see `JailManager.RoomCount`'s own comment for why a Jail's much bigger minimum footprint makes a per-tile ratio meaningless here, unlike the Hatchery/Tavern tile-based caps Gremlin/Warlock use). See `MazeRattlerSpawner.MeetsJoinRequirements`. Note this is a separate cap from — not instead of — the shared Hatchery-tile population cap below: a Maze Rattler still counts toward Gremlin/Warlock's own Hatchery requirement even though its own recruitment isn't gated on Hatchery capacity at all.
- Counted alongside Gremlin and Warlock in the Hatchery-tile "non-Imp creatures" population cap those two check as part of their own join requirements (see Gremlin's and Warlock's entries, above) — a Maze Rattler still eats Bacon like any other non-Imp creature, so it still consumes that shared capacity even though nothing gates its own recruitment on it.
- AI is the same priority-list shape as Gremlin's (Happiness gates it the same way too), re-evaluated every frame:
  - **100** — no personal Lair claimed yet: same as Gremlin's.
  - **80** — hungry: same as Gremlin's (nearest Tavern tile with bacon).
  - **40** — a Training Room exists: train there, identical to Gremlin's own training behavior.
  - **35** — no Training Room, but a Jail is placed: "haunt the prisoners" — walks to a random reachable pit tile of any placed Jail and pauses there a few seconds, then (via falling back through Idle) drifts to a different pit tile, same walk-pause-repeat shape Gremlin's roam uses. Grants no exp; purely flavor movement — haunting itself doesn't interact with a held prisoner (see Jail's own entry, above, for the actual capture mechanic; that's driven by the Grab hand and Conversion Class's Bean Counter, not by Maze Rattler). See `JailManager.TryFindRandomPitTile`.
  - **30** — neither a Training Room nor a Jail exists: roam to random reachable floor tiles, identical to Gremlin's own fallback.

### Bean Counter
The fourth non-Imp creature — Conversion Class's own staff, a fanatical clipboard-wielding zealot who lectures jailed creatures out of existence on the evils of meat. A placeholder capsule like every other creature's, colored sickly yellow-green, until a real model exists — see `BeanCounterAgent.cs`/`BeanCounterSpawner.cs` (`Assets/Scripts/Monsters`).
- Starts with 50 HP. Movespeed 2.2, Strength 6, Attackspeed 0.5 — a preacher, not a brawler, deliberately weaker/slower than Gremlin's own numbers; no design-brief values exist yet. Every other stat sits at 0, same as every other creature, until a system exists to consume them.
- Joins via the Portal's pool, seeded with 5 to start — recruiting spawns it at the Portal's own coord.
- Join requirements (all must hold, on top of pool availability): at least 1 free (unclaimed) Lair spot (same universal requirement every recruitable creature has); fewer Bean Counters already in the domain than 3 times the number of placed Conversion Class *rooms* (not tiles — same "room count, not tile count" shape Maze Rattler's own Jail requirement uses, since Conversion Class's 4x5 minimum makes a per-tile ratio meaningless here too). See `BeanCounterSpawner.MeetsJoinRequirements`. Counted alongside Gremlin/Warlock/Maze Rattler in the Hatchery-tile "non-Imp creatures" population cap those three check as part of their own join requirements (see Gremlin's/Warlock's entries, above) — a Bean Counter still eats Bacon like any other non-Imp creature, so it still consumes that shared capacity even though nothing gates its own recruitment on it.
- AI is the same priority-list shape as Gremlin's (Happiness gates it the same way too), re-evaluated every frame:
  - **100** — no personal Lair claimed yet: same as Gremlin's.
  - **80** — hungry: same as Gremlin's (nearest Tavern tile with bacon).
  - **40** — a Conversion Class is placed: "teach" — alternates between walking to a bench-adjacent tile, pausing there 3-5 seconds (+10 exp every 2 seconds while it keeps lecturing, same tick shape Training/Library use), and moving on to a different bench, same walk-pause-move-on pattern Gremlin's training uses. Partway through each lecture session (once, not every frame — see `_tormentDelaySeconds`), if any Jail is currently holding a prisoner, it processes one via `ConversionClassManager.TryTormentRandomPrisoner` — see Conversion Class's own entry, below, for the outcome table.
  - **30** — no Conversion Class exists: roam to random reachable floor tiles, identical to Gremlin's own fallback.

### Elf
Conversion Class's torment-failure outcome for an Evil-alignment prisoner — "weak and worthless," a deliberately gimped creature, never recruited through the Portal's pool at all (see `ElfSpawner.cs`, which has no `MeetsJoinRequirements`/pool gate, just a direct `SpawnElf(coord)` called by `ConversionClassManager.TryTormentRandomPrisoner`). A placeholder capsule, pale sickly green and noticeably smaller than every other creature's own, until a real model exists — see `ElfAgent.cs` (`Assets/Scripts/Monsters`).
- Starts with 20 HP. Movespeed 3, Strength 4, Attackspeed 0.5 — well below Gremlin's own placeholder stats in every dimension, "weak" per the brief. No design-brief values exist yet.
- No join requirements at all — it can't be recruited, only created.
- AI is the same Happiness-gated shape every other creature uses, but with no preferred-room job tier — "worthless" per the brief, so there's nothing it's good at:
  - **100** — no personal Lair claimed yet: same as Gremlin's.
  - **80** — hungry: same as Gremlin's (nearest Tavern tile with bacon).
  - **30** — otherwise: roam to random reachable floor tiles, identical to Gremlin's own fallback — this is the only tier below Hunger an Elf has.

## Combat

The first real creature-vs-creature system. It generalizes the wall/room-attacking mechanic Happiness already uses (Strength/Attackspeed-driven hits — see Happiness, above) into fighting between live creatures, and adds everything that only matters once two creatures can hurt each other: who counts as an enemy, target acquisition, breaking off, and what happens to the loser. Nothing here is implemented yet; every number is a placeholder.

**First use case is Keeper-vs-Keeper PvP** over an online peer-to-peer connection — see "Multiplayer authority", below, for why the netcode model has to be settled before this ships.

### Keeper stances

Every Keeper holds a **stance** toward every other Keeper: **Aggressive** (the default), **Neutral**, or **Friendly**. Stances are **directional** — P1 can be Friendly toward P2 while P2 is Aggressive toward P1 — and each creature reads *its own* Keeper's stance toward the other side. Stored in a single `StanceRegistry` service, host-authoritative and replicated so both machines resolve hostility identically.

- **Aggressive** — every hostile creature within aggro range (below) is a target; attack on sight.
- **Neutral** — no creature is a target until that Keeper's side has actually hit it; from then on *that individual* retaliates against its attacker, and same-owner creatures near it (within aggro range) assist. Hitting a Neutral Keeper's creature does **not** flip your Keeper stance — only the struck creature reacts.
- **Friendly** — a struck creature still retaliates against its attacker, but nearby allies do **not** assist, and nothing ever auto-aggros.

**Wild/neutral creatures** (anything not owned by a Keeper — future map fauna, etc.) belong to a pseudo-owner whose stance toward everyone defaults to **Neutral**.

Stance can be changed at any time, including mid-fight; a change never auto-disengages fights already in progress.

### Detection and targeting

- **Aggro radius** — a new creature stat, **5 tiles** for every creature to start (per-creature tuning later). A creature scans for hostiles (per the stance rules above) within this radius.
- **Line of sight** — a straight grid trace, blocked only by Rock/Bedrock walls (not by other creatures, rooms, or terrain). A target must be in LOS *and* in aggro radius to be acquired.
- **Target selection** — nearest valid hostile. Swappable for lowest-HP / highest-threat / player-directed later (attack commands are a separate, later feature).
- **One current target** at a time — no threat table yet. Being hit by someone who isn't the current target does not switch targets, except via the Neutral-stance "the individual now retaliates against its attacker" rule.
- **Drop conditions** — the current target is released when it is downed, when it leaves aggro range and stays out, or when LOS to it has been broken for **3 continuous seconds**. On drop, the creature re-scans; with nothing to fight it returns to its normal priority list (below).
- **Leash** — a creature that chases a target more than **7 tiles from where the fight started** breaks off, so a runner can't drag a whole roster across the map. Measured from the engagement spot, not from home territory — a creature dropped deep in an enemy base still defends itself and fights where it stands.

### Attacking

- **Range** is measured in **tiles**. The basic attack is melee (range 1) until skills define otherwise.
- **Hit cadence** — `1 / Attackspeed` seconds per hit, the same shape the Happiness wall-attack and the Imp's "Mine" already use.
- **Damage per hit** — driven by **Strength** (same as the existing wall/room attack).
- **Armor** — flat reduction: `damage_taken = max(0, incoming − Armor)`.
- **Lifesteal** — the attacker heals for its **Lifesteal** value on every hit that deals damage (capped at MaxHP). First use of the stat.
- **Multiple attackers** may share a tile and all hit the same target — no surround/slot limit, hits just stack against one HP pool (same as two Imps sharing a dig tile).
- **Windup, cooldown, projectiles, mana cost, AoE, and friendly fire** are all deferred to the skills system — the basic attack has none of them. Skill slots 2–6 (see "Skill slots", above) are where those come in per creature.

### The Throne as a target

The Throne Room is attackable (`IAttackTarget`) — a hostile creature with no creature to fight walks up to the enemy Throne and hits it on the same Strength/cadence rule. **1000 HP, regenerating 10/sec**, so a lone wanderer can't scratch it — it takes a sustained warband to out-damage the regen. It carries a health "foot-circle" like a creature's (`CreatureHealthRing`, scaled up to circle the platform) that's **hidden while at full HP**. Attacking it rallies the owning Keeper's nearby idle defenders onto the attacker. There's **no lose-condition** on it hitting 0 yet — it just clamps and regens back; the Throne HP shows in the top status bar.

### Breaking off (priority)

While a creature has a target, combat overrides its entire normal priority list — Lair-claiming included — *unless* one of these pulls it out, checked in this order every frame:

1. **Happiness Leaving (0–10)** — unchanged; walk to the Portal / "destroy the domain" still wins over everything.
2. **HP ≤ 20%** — flee: path to the creature's own Keeper's Throne Room and stay there. If no path exists, it fights cornered.
3. **Grabbed by the hand** — being picked up drops the target immediately; combat does not resume on release (the creature re-evaluates fresh from wherever it's dropped).
4. **Hunger < 30** — break off and go eat (see Hunger).
5. **Happiness < 30** (Unhappy / Angry) — leave the fight if a path away from the target exists; otherwise keep fighting.

**After combat ends** (target dropped, none of the above firing): if HP ≥ 80%, resume the task that was interrupted; otherwise go to a claimed Lair tile and rest until HP ≥ 80%, then resume. A creature resting on its own claimed Lair tile regenerates **25% of MaxHP per minute** (independent of its normal passive HPRegen, which keeps ticking everywhere).

### Downed bodies and defeat

A creature reduced to 0 HP does **not** die — it **faints**. The live agent is removed and replaced by an inert **downed body** on its tile (same "live agent becomes inert data" pattern Jail's `JailedPrisoner` uses, but as a physical, draggable object). The downed body carries the *entire* creature — kind, name, level, exp, stat block, owner, alignment — so a rescued creature comes back exactly as it was.

- A downed body has a small **faint-HP** pool — **10% of the creature's MaxHP** — that does not regenerate while downed and resets when the creature stands back up. Creatures don't keep attacking a downed target (they drop it on down), so depleting this is a **deliberate** re-target — an Aggressive creature standing next to one will do it. If faint-HP hits 0, the creature is **permanently dead** — removed from the game, no recovery. Permadeath is real. (Scaling with MaxHP means a tanky creature is also harder to finish off.)
- **Coming to** — a downed body left undisturbed (not attacked, not being carried by an Imp) for **1 continuous minute** stands back up on its own as a live agent, at whatever faint-HP it has left, and immediately paths to a claimed Lair tile to rest (the post-combat heal). With no Lair reachable it flees toward its Keeper's Throne Room instead. Any hit resets the minute. In PvP this is the capture/finish window — get an Imp to the body within a minute or it walks away.
- A downed body dropped onto Lava or into a Chasm is **permanently dead** immediately.
- **Recovery** — a downed body carried to one of its own Keeper's Lair tiles (see the Rescue Ally job, below) becomes a recovering body: **+25% MaxHP per minute**. At full HP it stands back up as a live agent with its level/exp intact.
- **Capture** — a downed body carried to an enemy Jail's pit tile becomes that Keeper's `JailedPrisoner`, feeding the existing Jail → Conversion Class pipeline unchanged (see those rooms).
- The player's hand can grab and carry downed bodies directly, same as grabbing a live minion.

### Imp jobs: Rescue Ally and Capture Enemy

Two new auto-queued `BuilderJobBoard` job kinds, both non-cancelable (same "no player tap needed" model as RepairRoom):

- **Rescue Ally** — **highest priority of any Imp job**. Queued whenever a friendly (same-owner) downed body exists on a tile reachable by that Keeper's Imps. An Imp walks to the body, picks it up, and carries it to the nearest owned Lair with a free tile, where it becomes a recovering body (above).
- **Capture Enemy** — **one priority below Rescue Ally**, and only queued if that Keeper has a Jail with a free pit tile. An Imp carries a hostile downed body to that Jail pit, imprisoning it via the existing `JailManager` capture path.

Full Imp job order becomes: **RescueAlly, CaptureEnemy, Dig, RepairRoom, Reinforce, Build, Claim**.

- Both sides' Imps can race for the same body; whoever picks it up first wins and the body drops off every other board.
- An Imp carrying a body that comes within aggro range of any non-Imp hostile **drops the body where it stands and flees** (see "Imps in combat", below) — the body stays put for another attempt.

### Imps in combat

- An Imp **flees all hostiles** — pathing away toward its own Throne Room — **except other Imps**.
- An Imp **is hostile toward enemy Imps** and fights them with "Mine" (consistent with "Mine" being "only effective against other Imps and resource objects", see Skill slots). Imp-vs-Imp is the one fight an Imp will actually pick.
- Imps have no morale or hunger (see Creatures), so the only break-off rules that apply to a fighting Imp are HP ≤ 20% (flee) and being grabbed.

### Exp from combat

Placeholder rates, balancing later:

- **+1 exp per 1 point of damage dealt.**
- **+0.5 exp per 1 point of damage received.**
- Granted **on each hit**, not on kill — there's no separate bonus for downing or killing a creature (the damage that got it there already paid out).
- This is the "combat" exp source the Creatures section lists alongside tasks and training.

### Assist and alarm

- **Assist** — when a Neutral-stance creature is first attacked, every same-owner creature within its aggro radius that isn't already fighting switches to target the attacker. Friendly-stance creatures get no assist; Aggressive-stance creatures are all already engaging anyway.
- **Alarm** — any creature entering combat pings same-owner combat-capable creatures within **5 tiles**; idle ones path toward the fight. Imps ignore the alarm (they only ever fight enemy Imps, and flee everything else).

### Multiplayer authority (implementation note)

Combat is the system that forces the netcode decision. The recommended model is **host-authoritative** (one peer hosts the simulation; the other is a client that sends intents and renders replicated state), not deterministic lockstep — the current simulation is thoroughly non-deterministic (`Time.deltaTime` and `UnityEngine.Random` throughout, client-local ID counters, Unity pathfinding) and making it lockstep-safe would be a permanent tax on every future feature.

Under host-authority the host owns and resolves: every `CreatureStats.HP`, all damage and Lifesteal math, all combat RNG, the `StanceRegistry`, and every faint / recover / permadeath / capture transition. Clients predict only their own hand/camera.

Prerequisite refactors, in order:
1. Extract the duplicated per-creature movement (`PlanPathTo` / `MoveAlongPathThen` / `ReplanPathFromCurrentPosition`, currently byte-identical across every agent) into a composed `GridMover`, and add combat as a composed `Combatant` component — same composition pattern `Creature` / `Hunger` / `Pay` / `Happiness` already use. Each species keeps its own `EvaluateAndAct` priority ladder.
2. Route gameplay ticks through one host-gated simulation step instead of every agent's own `Update()`.
3. Consolidate gameplay RNG and ID assignment into host-authoritative services.
4. Split `GameBootstrap` world-building into "host builds, client receives" (the `LevelData` load path is already most of the way there).

### Tuning (implementation note)

Every combat number above is an unbalanced placeholder. Per-creature stat blocks should move to ScriptableObjects (from the current per-agent `[SerializeField] CreatureStatBlock`) so time-to-kill, aggro radius, faint-HP, and recovery rates can be iterated without touching prefabs.

### Current implementation

Shipped (compiles clean; **first playtested 2026-09-03** — the core fight loop holds up: both keepers engage across any claimed floor and the fight → faint → haul chain runs. The numbers are still untuned placeholders needing a real balance pass, and **jailing was flagged as rough** (capture flow / prisoner handling) and deferred):

- **`StanceRegistry`** (`Assets/Scripts/Core`) — the directional stance table, `StanceRegistry.Current` set once per game by `GameBootstrap.BuildWorld`, cleared in `ReturnToMainMenu`. Default Aggressive between keepers, so combat only actually fires on a multi-keeper level (via the debug player switcher, until an AI or real netcode exists). No stance-editing UI yet — a menu would call `StanceRegistry.Set`.
- **`Combatant`** (`Assets/Scripts/Creatures`) — the whole system, composed into every agent like `Hunger`/`Happiness` and ticked at the top of `Update` before `EvaluateAndAct` (or the Imp's state machine). Returns `true` to mean "combat drove the creature this frame, skip normal behavior." Covers: aggro scan (throttled 0.25s) filtered by stance + `DungeonGrid.HasLineOfSight` (a supercover grid trace blocked only by Rock), nearest-target, single target, LOS-lost-3s / out-of-range drop, leash (7 tiles from where the fight started, so a dropped-in creature still defends itself), melee attack on the `1/Attackspeed` cadence (chases to a tile *beside* the target and holds), `Armor` as flat reduction, `Lifesteal` per hit, combat exp (+1/dmg dealt, +0.5/dmg taken), the assist/alarm broadcast on being hit, HP≤20% flee-to-Throne (only while a fight is active), Hunger<30 / Happiness<30 / grabbed break-off, and post-combat healing.
- **"Finish off enemies" setting** — an Aggressive creature standing over a knocked-out enemy only beats it to death (`Combatant.TryFinishDownedEnemy`) when this is on. **Off by default** (BottomMenuBar → Settings; `Combatant.AllowFinishOffEnemies`, reset each game by `GameBootstrap`) — so by default a downed creature always gets its full come-to window / a chance to be captured.
- **`GameplayLog` is keeper-tagged** — `GameplayLog.Write(int ownerId, string)` prefixes `[P1]` / `[P2]` (`[wild]` for none); every creature agent, spawner, `Combatant`, `DownedBody`, `BuilderJobBoard`, and the Jail/Conversion/Bridge managers now route through it. `Combatant` also logs every creature-vs-creature hit (`[P1] Croak #0 hits [P2] Hexley #3 for 12 (34/80 HP)`) and every finishing blow.
- **`DownedBody`** (`Assets/Scripts/Creatures`) — added to the creature's own GameObject on faint; the agent is **disabled, not destroyed** (so the creature, level, exp, name are all preserved for a clean stand-up — a functional equivalent of the "inert data" model in the design above). 10%-MaxHP faint pool, 60s come-to (routes the revived creature to its Lair to finish healing), 25%/min recovery when delivered to a Lair, permadeath on faint-pool depletion or a Lava/Chasm drop. The placeholder capsule tips on its side while down.
- **Imp behavior** — Imps flee every hostile except enemy Imps (pathing toward their Throne Room), and fight enemy Imps with "Mine". `OnDisable`/`OnEnable` now keep a frozen (grabbed or downed) Imp off the job board.
- **Rescue Ally / Capture Enemy** — implemented Imp-side (checked in `ImplingAgent.TrySeekJob` ahead of every `BuilderJobBoard` job) rather than as new `JobKind`s, because a downed body is a moving entity the coord-keyed board can't track. Rescue outranks capture; capture only starts if the keeper has a Jail with a free pit. New Imp states `MovingToDownedBody` / `CarryingBody`; the body is dragged along, delivered to a Lair tile (`LairManager.TryFindNearestLairTile` → `DownedBody.BeginRecovery`) or a Jail pit. Two Imps race; first to `BeginCarry` wins. A carrier that comes within aggro of a non-Imp hostile drops the body and flees.
- **Jailing a downed creature** keeps its own capsule parked in the pit as the prisoner (`JailManager.TryCaptureBody` → `DownedBody.MarkJailed`), rather than `TryCapture`'s tiny inert gray blob — the blob sat below the pit rim and read as "the creature vanished". The `JailedPrisoner` bookkeeping and the Jail → Conversion Class pipeline are unchanged; the parked capsule is tracked in `_prisonerVisuals` so a torment / Jail-sale tears it down. (`TryCapture` — the live-creature Grab-drop-onto-pit path — still makes a blob; same visibility caveat applies there.) A jailed prisoner patches itself up while held — an instant **+10% MaxHP** on entry, then **+5% MaxHP/min** — visible on its health ring.
- **Grab hand** — `MinionGrabController` picks up and carries any keeper's downed body: onto your Jail pit jails it, onto your own Lair tile starts recovery, onto Lava/Chasm kills it, anywhere else just moves it. Carrying a body *over* Lava/Chasm no longer kills it — only a body left at rest on one dies.

Deferred: the `GridMover` extraction (netcode prerequisite #1 — `Combatant` carries its own copy of the path/move helpers for now; the five Monster agents still duplicate theirs), the health-ring damage flash, ScriptableObject stat blocks, and any stance UI.

## Terrain (current implementation note)
New tile types beyond Rock/Floor, each with its own walkability rule (`DungeonGrid.IsWalkable`/`TileType`):
- **Water** — undeep: every creature can wade through it except Imps, who need a Bridge (see below) to cross.
- **Lava** — undeep in principle, but nothing in the game is fire-resistant yet, so it's impassable to everyone, Imps included, until a Bridge is built on it.
- **Chasm** — deep, sunk one full grid level like a Jail's own pit (`DungeonGrid.SetTerrainFeature`/`SetPitDepth`), with a scatter of spikes at the bottom. Never walkable by anyone, and a Bridge can never be built across it.
- **Holy Ground** — white, with a golden 8-pointed star at its center (`DungeonGrid.BuildHolyGroundStar`). Walkable by everyone, same as Floor, but can never be Claimed — `ClaimTile`/`CanBuildRoomOn`/`BordersClaimedTile` are all already gated to `Type == Floor`, so unclaimability falls out of that same check for free rather than needing its own guard. Territory can't grow *through* it either, for the same reason — it's a hole in the claimable map, not just a tile that itself refuses ownership.

A further tile is a Rock wall variant rather than a new `TileType` — **Bedrock** (`DungeonGrid.SetBedrock`/`TileState.IsBedrock`) is permanently unminable: `RequestDig`/`RequestReinforce` both refuse it outright, so it can never be queued for either. Darker than a reinforced wall. Mutually exclusive with `IsReinforced`/`WallResourceType`, same as those already are with each other.

No real map generator exists yet, so today these are placed with a dev-only Build-menu tool (`BuildMode.PlaceWater`/`PlaceLava`/`PlaceChasm`/`PlaceHolyGround`/`PlaceBedrock`, see `TileInteractionController`/`BottomMenuBar`'s "[Dev] Terrain" buttons) that paints a bare Rock tile directly into one of the five, free of charge — a placeholder standing in for real procedural placement later.

Pathfinding needed to learn a per-creature-type rule for the first time here — `DungeonGrid.IsWalkable`/`GetReachableFloorDistances` and `AStarPathfinder.TryFindPath` all take an `isImp` flag (default `false`) rather than duplicating pathfinding per creature type; only Impling-specific call sites (`ImplingAgent.PlanPathTo`, `BuilderJobBoard`'s own worker-distance queries, `ImplingSpawner`'s spawn-location check, and the Impling-only deposit/haul queries on `TreasuryManager`/`TavernManager`/`SlimeHatcheryManager`) pass `isImp: true`; every other creature agent is unaffected.

## Rooms

### Selling
Every room type shares the same generic Sell tool (`LairManager.TrySellRoom`, regardless of which manager actually owns the sold room). Selling refunds gold: each cleared tile pays back that room type's own `CostPerTile` (e.g. selling 4 Lair tiles refunds 4x5=20 gold), deposited into the Treasury the same way `TrySpendGold` charges it — into whatever tiles have room, no particular order. If the Treasury has nowhere to put it (every tile already full, or no Treasury tiles exist at all), the excess is simply lost rather than blocking the sale. A sold room's own *stored* contents (Treasury gold, Tavern bacon) are separately lost, not refunded — only the placement cost comes back.

### Slime Hatchery
Slimes are bred here in a chicken-coop-like box. Food for barbaric creatures.

- Minimum size: 3x3, with the chicken coop box structure occupying the middle tile.
- If larger than 3x3 and there's no single middle tile (even width/height), the coop structure goes in the square one tile in from each edge on the top-right corner.
- Breeds 1 slime every 2 seconds, capped at 1 slime per tile the room occupies.
- Slimes are visible little blue balls that wander freely within the hatchery's own tiles. If a slime ever ends up off those tiles (e.g. the hatchery is sold), it disappears.
- Placing a new footprint directly against an existing Hatchery, such that the two together still form a clean rectangle, extends that Hatchery rather than starting a second one — the coop and fence relocate/rebuild for the bigger room's shape, and existing slimes/breed progress carry over untouched. A drag that doesn't complete a rectangle with any single existing Hatchery (an L-shape, or not touching one at all) just places its own separate Hatchery instead.

### Tavern
Offer up slimes to the gods of good taste, and receive bacon instead. Food for intelligent creatures.

- Minimum size: 4x4, with a shrine occupying the middle 2x2 — a tube going up and a tube going down.
- Implings transport slimes here to convert them to bacon: 1 slime = 4 bacon.
- Storage cap: 12 bacon per Tavern tile adjacent to the shrine structure (so implings aren't overtasked).
- Same merge-on-adjacent-placement rule as Slime Hatchery, above — extending an existing Tavern into a bigger rectangle recenters the shrine and recomputes every storage tile for the new shape (any bacon stored on tiles at the time is lost, same as selling the room would lose it).

### Training Room
Where non-Imp units train to gain exp. Imps get their exp from mining instead (see the Creatures section above), so this room has no effect on them.

- No minimum size, placed like a Lair/Treasury (drag a footprint). 20 gold per tile. Green floor on every tile.
- Training-dummy structures (a stick cross with a head) — a brick pattern, reverse-engineered from exact grid examples the user drew rather than derivable from a text description (`TrainingRoomManager.GetStructureCoords`/`GetRowPositions`/`GetColumnsNear`/`GetColumnsFar`):
  - Rows (Y): 1 (near edge) and `height-2` (far edge) are both kept once distinct, then the same "step 2 in from each end" repeats inward until the two ends meet or cross — height 4 → rows {1,2} (adjacent), height 5 → {1,3}, height 6 → {1,4}, height 7 → {1,3,5}.
  - Columns (X): each row picks its columns from one of two sets — `ColumnsNear` (1, 3, 5, ... up to `width-2`) or `ColumnsFar` (`width-2`, `width-4`, ... down to 1) — and rows alternate between them starting with Far on the bottom row. For an odd width (or width 3) the two sets are identical, so alternation is a no-op; for an even width 4+ they're genuinely different, so consecutive rows cover different columns instead of repeating the same ones (e.g. a 6-wide room's two rows use {2,4} then {1,3}, covering every interior column between them).
  - When the column sets are identical (odd/3-wide) *and* two rows end up only 1 tile apart (only possible when height is exactly 4), that pair would otherwise touch — the earlier (lower) row is dropped, keeping just the later one. E.g. a 3x4 room ends up with a single dummy on its top row only.
  - Worked examples: 6x4 → 4 dummies at (2,1),(4,1),(1,2),(3,2). 7x5 → 6 dummies, two rows of 3. 5x5 → 4 dummies (both rows use the same columns since width 5 is odd). 3x3 → 1, centered.
- Grants +20 exp every 2 seconds to whichever non-Imp unit is training here, for as long as it keeps training (see Gremlin/Warlock's own AI priority lists, above, for when that is). Dummy tiles are blocked to pathfinding (same as Library bookcases) — a training creature stands one tile off to the side of a dummy, not on top of it, and walks between different dummies' adjacent tiles rather than standing in one spot; see `TrainingRoomManager.TryFindNearestDummyTile`/`TryFindRandomDummyTile`.
- Same merge-on-adjacent-placement rule as Slime Hatchery, above — extending an existing Training Room into a bigger rectangle tears down and recomputes every dummy for the new shape instead of leaving stale ones.

### Library
Where intelligent creatures (Warlock, so far) do research to gain exp.

- No minimum size, placed like a Lair/Treasury/Training Room (drag a footprint). 20 gold per tile. Dark purple floor with a slightly lighter purple inset on every tile.
- A bookcase structure occupies every OTHER row of tiles that isn't on the room's own outer edge (the interior rows in between are left as plain open floor instead) — a room smaller than 3x3 has no interior row at all and gets no bookcases.
- Bookcases connect from east to west within a row (forming one continuous shelf) but never from north to south — the interior row between two bookcase rows is a walkable aisle, not a third row of shelving.
- Bookcase tiles are not walkable; every other Library tile (the outer edge and the aisle rows) is — so a creature can always walk between two rows of bookcases, or around them along the room's border, rather than the whole interior being sealed off.
- Same merge-on-adjacent-placement rule as Slime Hatchery, above (see that entry) — extending an existing Library into a bigger rectangle (e.g. dragging one more row onto its side) recomputes the whole bookcase/aisle layout for the new shape, rather than the new tiles sitting there as plain floor.
- Grants +5 exp every 2 seconds to whichever intelligent unit is researching here (see Warlock's own AI priority list, above, for when that is). The "fall back to studying combat for a smaller trickle of exp" idea from earlier design isn't implemented — Warlock's actual fallback (see above) is training in a Training Room instead, not a combat-study mode within the Library.

### Jail
Storage for defeated creatures the player wishes to keep — a pit sunk into the middle of the room, ringed by a walkable ground-level walkway with a low fence around the pit itself and one staircase-and-gate entrance down into it. Attracts the Maze Rattler (a ratman-ish creature, haunts the pit tiles with no interaction with prisoners — see its own entry above).

Capture is real now, hung off the Grab hand rather than any combat system (there still isn't one) — dropping a carried Gremlin/Warlock/Maze Rattler/Elf onto a pit tile (`MinionGrabController.TryDrop`, checked before its Training Room special-case) imprisons it via `JailManager.TryCapture`: the live creature/GameObject is destroyed and it becomes inert `JailedPrisoner` data (creature kind, name, level, an alignment flag, and which pit tile it's held on) sitting on that tile until Conversion Class's own Bean Counter processes it — see that room's own entry, below, for what happens next. Not every creature is jailable: Impling (mana-conjured, not a moral subject) and Bean Counter (the lecturer, not a candidate for its own class) are excluded — see `MinionGrabController.TryGetJailableInfo`. Capture is opportunistic: a full Jail (every pit tile already occupied) or an unreachable one just falls through to a normal drop-in-place instead of blocking the gesture.

- A small bound gray blob sits on each occupied pit tile (`JailManager.BuildPrisonerVisual`) — distinct from the "haunting" Maze Rattler capsules that pass through the same tiles.
- `JailManager.PrisonerCount` (across every placed Jail) is what a Bean Counter checks before starting a torment; `TryReleaseRandomPrisoner` pops one at random for it to process.
- Selling a Jail that's holding prisoners loses them the same way it loses everything else stored in a room — no refund, no rescue.

- Hard 5x5 minimum footprint (checked against the dragged rectangle itself, same enforcement as Slime Hatchery/Tavern's own minimums, above) — 20 gold per tile, a placeholder like every other room's current cost.
- The pit is inset exactly 1 tile from every edge of the room's own footprint — a 5x5 Jail is a 3x3 pit surrounded by a 1-tile-wide walkway ring, a 7x7 is a 5x5 pit with the same 1-tile ring, and so on. The ring tiles are ordinary walkable Claimed floor (ground level, no sink) — the room is never pit wall-to-wall.
- Unlike every other room, a Jail's footprint doesn't need to be pre-dug — placing it on undug Rock digs and claims those tiles as part of placement itself (`JailManager.TryPlaceJail`/`CanPlaceFootprint`). Placing it on already-dug Claimed floor works too, same rule (`DungeonGrid.CanBuildRoomOn`) every other room follows. This applies to the whole footprint, ring included.
- The pit's floor renders one full grid level below the surrounding ground — sunken, not raised — with a dirt-brown floor overlay (flush on top of the shared purple room-tile color every room's base grid tile has). This is a render-time-only offset (`DungeonGrid.SetPitDepth`/`TileState.PitDepth`); walkability never looks at a tile's Y position, so the pit floor is exactly as walkable as any other room floor — implings can path across it like any other room (a held prisoner isn't a live agent at all, see the capture note above, so it never actually walks anywhere).
- The ring's own floor gets a "grate" look instead of the plain room color: a black panel with a light gray plus/cross centered on it, arms reaching to the middle of each tile's four sides — the same block pattern the rim wall (below) used to carry when it went far deeper, now moved onto the walkway floor.
- The pit's own boundary (against the walkway ring, not the room's outer edge) gets a short, plain dark wall — just 2 tile-heights deep, grounded at ordinary floor level (the walkway ring's own floor height) and reaching down from there, so it never pokes up above the ring's floor surface the way an earlier version (grounded at Rock's taller top face, a leftover from before the pit/ring redesign) visibly did. Just enough to close the gap under the fence without a void showing below ground, since the fence itself (not this wall) is the pit's one real decorated rim marker. An even earlier, much deeper (and fully textured) version of this wall had a separate bug where it read as one solid black box per tile, from sitting in front of (above) the sunk floor rather than staying inset to the tile's true edge — fixed by keeping the wall as a thin rim strip rather than a full-tile column, a constraint that still holds now that it's short. Interior pit tiles have no elevation seam against their neighbors and don't need a wall at all, same as ordinary floor never does.
- A light gray fence rail rings every outward-facing edge of the pit except one — the middle tile of the pit's own south edge, which gets a three-step staircase down into the pit instead, flanked by two gate posts ("one staircase with a gate"). The fence, rim wall, grate floor, and staircase/gate are all cosmetic; the tile underneath stays ordinary walkable Floor.
- Same merge-on-adjacent-placement rule as Slime Hatchery, above — extending an existing Jail into a bigger rectangle (itself allowed to be smaller than the 5x5 minimum, same as every other mergeable room type's extensions) recomputes the fence/rim wall/gate for the new pit boundary. A merge can only grow the room, which can only turn a former ring tile into a pit tile (if the seam between the two merged pieces becomes interior) — never the reverse, so an already-sunk tile's pit sink is never undone by a merge; the one thing that does need swapping on such a promotion is that tile's own floor overlay, from the ring's grate to the pit's dirt.
- Selling a Jail resets its tiles' pit depth back to 0 (ordinary flush floor) on top of the usual generic Sell refund (`LairManager.TrySellRoom`), so nothing built there afterward inherits a leftover sunken tile.

### Conversion Class
Where jailed creatures get lectured out of existence — a Bean Counter (see its own entry above) periodically pulls a random prisoner out of whichever Jail is holding one and tortures it with a sermon on the evils of meat, right here, forcing it to either join the Keeper or break down into something worse. The room itself owns the actual torment/outcome logic (`ConversionClassManager.TryTormentRandomPrisoner`) — the Bean Counter just triggers it, same relationship Training Room has to a training Gremlin's exp tick.

- Hard 4x5 minimum footprint, checked in either orientation (at least 4 in one dimension and 5 in the other) — same "reject anything smaller than the minimum in either dimension" enforcement Jail/Slime Hatchery/Tavern use, just two different numbers instead of one square one. 20 gold per tile.
- Olive/khaki floor overlay on every tile — Training Room's own border/fill grammar, recolored so the two rooms don't read as the same shade of green.
- A bench structure sits on every OTHER interior column — Library's own bookcase-row algorithm, axis-swapped: instead of a shelf spanning the room's width on every other row, a bench spans the room's height on every other column, so its long axis runs north-south and every seat in it faces east — toward the room's one wall board (see below). Aisle columns between benches stay walkable, same "creature can't stand on the structure itself, walks its adjacent tile instead" rule Library's bookcases use. At the 4x5 minimum this is exactly one bench column plus one aisle column inside the ring; the same alternation stays walkable at any larger merged size, for the same reason Library's own row alternation does.
- One wall board sits on the room's east ring column, vertically centered (biased low on a tie, same convention Jail's own gate-tile placement uses) — a thin non-blocking panel with a procedural broccoli icon (a cluster of green spheres over a brown stem) centered on it. Purely cosmetic; the tile underneath stays ordinary walkable floor.
- Same merge-on-adjacent-placement rule as Slime Hatchery, above — extending an existing Conversion Class into a bigger rectangle recomputes the whole bench/aisle layout and relocates the wall board for the new shape.
- **Torment** (`ConversionClassManager.TryTormentRandomPrisoner`, triggered by a Bean Counter partway through a lecture session — see its own entry above): pulls one random prisoner out of any Jail (`JailManager.TryReleaseRandomPrisoner`), then rolls a per-creature-kind chance to join the domain. All placeholder, unbalanced numbers, per the brief's own worked examples:
  - **Evil alignment** (Gremlin/Warlock/Maze Rattler/Elf — the only alignment anything in the game can actually have today, see below): Gremlin 80% join ("gremlins hate it and join to end their suffering"), Warlock 30% join (intelligent, resists), Maze Rattler 55% join, anything else (including Elf) 50% join. On success, the prisoner rejoins the domain as a fresh instance of its own kind, spawned at the pit tile it was held on (reusing `GremlinSpawner.SpawnGremlin`/`WarlockSpawner.SpawnWarlock`/`MazeRattlerSpawner.SpawnMazeRattler`/`ElfSpawner.SpawnElf` directly, bypassing the Portal pool since it was already a domain creature). On failure, it transforms into a new Elf instead (see that creature's own entry, below) — "weak and worthless."
  - **Good alignment**: real, correct code — same "documented but currently unreachable" honesty Jail's own prisoner mechanic used to carry before this shipped — but nothing in the game can produce a Good creature yet, so this branch never actually fires today. On success it would rejoin the domain the same way an Evil success does (flavor: "eats meat, does regular jobs" — mechanically identical to any other creature joining, since Bacon/Pay/jobs already work the same for everyone). On failure it "explodes into gold coins" instead: 50 gold per level, deposited to the Treasury (`TreasuryManager.AddGold`), the prisoner simply gone.

### Bridge
The only way across Water/Lava for creatures that can't otherwise cross them (see the Terrain section, above) — builds only on top of Water/Lava tiles, nowhere else.

- Doesn't fit the usual drag-a-rectangle room shape — it's placed with a line paint gesture instead (`BuildMode.Bridge`, the same `TileInteractionController` paint machinery Mine/Reinforce/Construct use), one tile at a time, each tile charged and built instantly (`BridgeManager.CostPerTile`, 15 gold — placeholder, unbalanced like every other room's current cost). Unlike every other paint tool, a Bridge drag can never square-fill and never follows the raw pointer path either — it locks onto whichever axis (horizontal/vertical) the drag first moves toward, then every further tile is that axis's straight-line projection of the pointer, so an unsteady drag still comes out as a clean line instead of a jagged or diagonal one.
- A placement must start adjacent to already-Claimed floor, and can then keep extending tile-by-tile as long as each new tile is adjacent to the line already built — territory grows the bridge outward the same way `DungeonGrid.BordersClaimedTile` grows ordinary claimed floor outward from its own frontier. Building a bridge tile also Claims that tile itself (`DungeonGrid.TryAssignBridgeRoom`), which is what lets the line keep extending past it and lets an impling claim onward past the far shore too — it still can never host an ordinary room, since `CanBuildRoomOn` only ever accepts Floor, and a Claimed bridge tile is Water/Lava, never Floor.
- Unlike every other room, each bridge tile is tracked as its own independent room (`Bridge_{n}` per tile, never merged into a bigger rectangle) — that's what lets a single Lava tile decay on its own without touching its neighbors.
- A bridge over Lava decays back to unbridged Lava after 5 minutes (`BridgeManager`'s own timer) — no gold refund, it just needs rebuilding. A bridge over Water never decays.
- Sellable through the same generic Sell tool every room uses, refunding `CostPerTile` per tile like normal — only the Lava timer's own decay skips the refund, since that's attrition, not a sale.
- **Save/load reconstruction** — `BridgeManager` implements `IRestorableRoomManager` like every other room manager. A saved bridge tile is a `Water`/`Lava` `LevelTileData` carrying a `Bridge_` `RoomId`; both restore paths (`LevelDesignerSession.RestoreTile`, `GameBootstrap.RestoreWorldTiles`) now defer it into the footprint map so `RoomReconstruction` dispatches it to `BridgeManager.RestoreRoom` — one call per tile, `start == end`, gold-free and adjacency-free (the save is authoritative). The shared `CommitBridgeTile` (claim + per-tile `Bridge_{n}` id + plank mesh + Lava decay timer) backs both live placement and restore. In the Level Designer, `BridgeManager` is created with `simulateDecay: false` so a restored Lava bridge doesn't decay while a map is being edited; it has no Rooms-menu button (a bridge is a line, not a rectangle) and can't be authored there yet — the reconstruction path just makes a hand-authored or future save-game bridge tile load correctly.

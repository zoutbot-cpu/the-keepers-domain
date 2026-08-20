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

### Inspecting a creature
View mode's tap-to-inspect (see `TileInteractionController.Inspect`) shows every stat/property a creature has, not just a name and position:
- Name, current task/state, position.
- Full stat block via `Creature.DescribeStats()` — level, exp, HP/regen, Mana/regen, Strength, Movespeed, Attackspeed, Intelligence, Craftmanship, Armor, Lifesteal. Same method for every creature type, so this always stays in sync with whatever the Core stats section (above) actually contains.
- Imp-specific: carried Gold/Mana Crystals/Slimes (`ImplingInventory`).
- Gremlin/Warlock-specific: Hunger value (+ "(hungry)" tag), wage + "(unpaid!)" tag, Happiness value and tier.

### Hunger
Non-Imp minions only — Imps don't get hungry. See `Assets/Scripts/Creatures/Hunger.cs`.
- Starts at 100, decays linearly down to 50 over 10 minutes, then keeps decaying at the same rate below 50 (no starvation consequence exists yet — that's just unbuilt, not a deliberate "hunger caps at 50" design).
- At 50 or below, the creature is "hungry" and prioritizes eating (see each creature's priority list below).
- Eating (1 bacon, from a Bacon Beacon storage tile with any on it) fully restores hunger to 100. The 1-bacon-per-meal amount is a placeholder, not a tuned value.

### Pay
Non-Imp minions only — Imps don't get paid, the Keeper mana-conjured them into existence rather than recruiting them. See `Assets/Scripts/Creatures/Pay.cs`.
- Wage is 5 gold per level: base 5 at level 1, +5 per level gained (so a level 3 creature draws 15 gold on payday).
- Payday happens every 10 minutes per creature, drawn straight from the Treasury — no walking/task involved, unlike eating.
- If the Treasury can't afford the payment, the creature goes "unhappy" instead (tracked, shown in the Creatures menu) — no desertion/morale consequence exists yet, same unbuilt-consequence pattern Hunger's missing starvation penalty uses.

### Happiness
Non-Imp minions only — Imps don't have moods. See `Assets/Scripts/Creatures/Happiness.cs`.
- 0-100, starts at 60. Driven entirely by Hunger and Pay (no independent source exists): decays 30 over 10 minutes while hungry, recovers 20 over 10 minutes while not (capped at the 60 starting value — nothing currently pushes it higher, so Enjoying/Ecstatic aren't reachable yet), and takes a flat -15 hit every payday it goes unpaid. All placeholder tuning, not balanced.
- Bands and their behavior, highest first:
  - **90-100 Ecstatic**, **75-90 Enjoying themselves**, **50-75 Happy** — no behavioral difference between these three yet, just mood flavor. 60 is the starting value, inside Happy.
  - **40-50 Getting unhappy** — refuses to do tasks (training/research/roaming — not eating or claiming a Lair, those aren't "tasks").
  - **25-40 Unhappy** — refuses tasks, and occasionally attacks a room or wall instead (see each creature's own AI priority list for the actual roll rate).
  - **10-25 Angry** — refuses tasks, attacks rooms/walls often (same mechanic as Unhappy, higher roll rate).
  - **0-10 Leaving** — overrides every other concern, even hunger/Lair-seeking: paths to the Portal and, on arrival, walks up the stairs and despawns (releasing any Lair tile it had claimed). If no path to the Portal exists at all, it "begins destroying the domain" instead — the same room/wall-attacking behavior Unhappy/Angry use, but unconditional rather than an occasional roll, for as long as it stays stuck.
- Attacking a wall chips away at its HP via the same `DungeonGrid.ApplyDigDamage` a dig job uses; attacking a room chips away at that specific tile's own HP (`TileState.RoomMaxHp`, 50 — see `DungeonGrid.ApplyRoomDamage`) the same way. Both hit at a rate driven by the creature's own Strength/Attackspeed (first real use of those stats on Gremlin/Warlock — see each creature's own stat block). Once the targeted room tile's HP is depleted, the *whole* room is torn down (`LairManager.TrySellRoom`, the same correct whole-room removal every room type already relies on for the player's Sell tool) — there's no true per-tile room removal (a bigger room isn't tougher than a 1-tile one under this model), since none of the room managers support losing a single tile out of an otherwise-intact room yet. A wall, by contrast, is genuinely just that one tile's HP with no wider consequence.

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
- Join requirements (all must hold, on top of pool availability): at least 1 free (unclaimed) Lair spot; fewer non-Imp creatures already in the domain than there are Slime Hatchery tiles (summed across every placed Hatchery, now counting Warlocks too — see below); at least 9 Training Room tiles placed (summed across every placed Training Room). See `GremlinSpawner.MeetsJoinRequirements`.
- AI is a priority list, re-evaluated every frame (higher number = acted on first, preempting whatever's lower). Happiness (see above) gates all of it — Leaving overrides everything, Unhappy/Angry/Getting unhappy refuse the tiers below 80:
  - **100** — no personal Lair claimed yet: claim the nearest unclaimed Lair *tile* (claims are per-tile, not per-room — a multi-tile Lair like the starting one can house one creature per tile) if one is reachable; only if none is, place a brand-new 1x1 Lair at a random reachable, buildable spot and claim that instead.
  - **80** — hungry: go eat at the nearest Bacon Beacon storage tile that has bacon (see Hunger, above).
  - **40** — a Training Room exists: train there (+20 exp every 2 seconds) — not standing still: alternates between walking to a training-dummy tile, pausing there 3-5 seconds (randomized per stop), and moving on to a different dummy, for as long as it keeps training. Same shape as Warlock's research (below), but a dummy isn't blocked to pathfinding the way a bookcase is, so it can stand right on one rather than needing an adjacent tile.
  - **30** — no Training Room exists: roam to random reachable floor tiles ("to find combat") instead, pausing briefly at each before picking a new one.

### Warlock (current implementation note)
The second non-Imp creature, and the first "intelligent" creature (Bacon Beacon's "food for intelligent creatures" line refers to this). A placeholder capsule like Gremlin's, colored dark purple, until a real model exists — see `WarlockAgent.cs`/`WarlockSpawner.cs` (`Assets/Scripts/Monsters`).
- Starts with 60 HP. Movespeed 2.5, Strength 10, Attackspeed 0.6 — all placeholders, weaker/slower than Gremlin's to read as a heavier caster-type; no design-brief values exist yet. Every other stat sits at 0, same as Gremlin, until a system exists to consume them.
- Joins via the Portal's pool, seeded with 10 to start — recruiting spawns it at the Portal's own coord.
- Join requirements (all must hold, on top of pool availability): at least 1 Lair tile placed anywhere (claimed or not — unlike Gremlin's requirement, which needs a *free* Lair spot); at least one placed Library that's at least 3x3; fewer non-Imp creatures already in the domain (Gremlin + Warlock combined) than there are Slime Hatchery tiles; fewer intelligent creatures already in the domain (only Warlock counts so far) than there are Bacon Beacon tiles. See `WarlockSpawner.MeetsJoinRequirements`.
- AI is the same priority-list shape as Gremlin's (Happiness gates it the same way too), re-evaluated every frame:
  - **100** — no personal Lair claimed yet: same as Gremlin's (claim the nearest existing unclaimed Lair if one's reachable, otherwise place a new random 1x1 Lair and claim that).
  - **80** — hungry: same as Gremlin's (nearest Bacon Beacon tile with bacon).
  - **40** — a Library exists: research there (+5 exp every 2 seconds) — not standing still: alternates between walking to a bookcase-adjacent tile, pausing there 3-5 seconds (randomized per stop), and moving on to a different bookcase, for as long as it keeps researching. See Library's own entry, below, for why it can't stand on a bookcase tile directly.
  - **30** — no Library exists: train in a Training Room instead (+20 exp every 2 seconds), if one exists — same walk-pause-move-on pattern between dummies Gremlin's training uses, not standing still. If neither a Library nor a Training Room exists, the Warlock just idles — unlike Gremlin, it has no roam fallback.

## Rooms

### Selling
Every room type shares the same generic Sell tool (`LairManager.TrySellRoom`, regardless of which manager actually owns the sold room). Selling refunds gold: each cleared tile pays back that room type's own `CostPerTile` (e.g. selling 4 Lair tiles refunds 4x5=20 gold), deposited into the Treasury the same way `TrySpendGold` charges it — into whatever tiles have room, no particular order. If the Treasury has nowhere to put it (every tile already full, or no Treasury tiles exist at all), the excess is simply lost rather than blocking the sale. A sold room's own *stored* contents (Treasury gold, Bacon Beacon bacon) are separately lost, not refunded — only the placement cost comes back.

### Slime Hatchery
Slimes are bred here in a chicken-coop-like box. Food for barbaric creatures.

- Minimum size: 3x3, with the chicken coop box structure occupying the middle tile.
- If larger than 3x3 and there's no single middle tile (even width/height), the coop structure goes in the square one tile in from each edge on the top-right corner.
- Breeds 1 slime every 2 seconds, capped at 1 slime per tile the room occupies.
- Slimes are visible little blue balls that wander freely within the hatchery's own tiles. If a slime ever ends up off those tiles (e.g. the hatchery is sold), it disappears.
- Placing a new footprint directly against an existing Hatchery, such that the two together still form a clean rectangle, extends that Hatchery rather than starting a second one — the coop and fence relocate/rebuild for the bigger room's shape, and existing slimes/breed progress carry over untouched. A drag that doesn't complete a rectangle with any single existing Hatchery (an L-shape, or not touching one at all) just places its own separate Hatchery instead.

### Bacon Beacon
Offer up slimes to the gods of good taste, and receive bacon instead. Food for intelligent creatures.

- Minimum size: 4x4, with a shrine occupying the middle 2x2 — a tube going up and a tube going down.
- Implings transport slimes here to convert them to bacon: 1 slime = 4 bacon.
- Storage cap: 12 bacon per Bacon Beacon tile adjacent to the shrine structure (so implings aren't overtasked).
- Same merge-on-adjacent-placement rule as Slime Hatchery, above — extending an existing Beacon into a bigger rectangle recenters the shrine and recomputes every storage tile for the new shape (any bacon stored on tiles at the time is lost, same as selling the room would lose it).

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
Storage for defeated creatures the player wishes to keep — a pit sunk into the middle of the room, ringed by a walkable ground-level walkway with a low fence around the pit itself and one staircase-and-gate entrance down into it. Attracts the Maze Rattler (a ratman-ish creature) — not yet implemented, and neither is any actual capture/prisoner mechanic, so right now this is placement and visuals only, same as Training Room/Library were before their gameplay landed.

- Hard 5x5 minimum footprint (checked against the dragged rectangle itself, same enforcement as Slime Hatchery/Bacon Beacon's own minimums, above) — 20 gold per tile, a placeholder like every other room's current cost.
- The pit is inset exactly 1 tile from every edge of the room's own footprint — a 5x5 Jail is a 3x3 pit surrounded by a 1-tile-wide walkway ring, a 7x7 is a 5x5 pit with the same 1-tile ring, and so on. The ring tiles are ordinary walkable Claimed floor (ground level, no sink) — the room is never pit wall-to-wall.
- Unlike every other room, a Jail's footprint doesn't need to be pre-dug — placing it on undug Rock digs and claims those tiles as part of placement itself (`JailManager.TryPlaceJail`/`CanPlaceFootprint`). Placing it on already-dug Claimed floor works too, same rule (`DungeonGrid.CanBuildRoomOn`) every other room follows. This applies to the whole footprint, ring included.
- The pit's floor renders one full grid level below the surrounding ground — sunken, not raised — with a dirt-brown floor overlay (flush on top of the shared purple room-tile color every room's base grid tile has) rather than the plain room color the ring tiles show. This is a render-time-only offset (`DungeonGrid.SetPitDepth`/`TileState.PitDepth`); walkability never looks at a tile's Y position, so the pit floor is exactly as walkable as any other room floor — prisoners (once they exist) and implings can path across it like any other room.
- The apparent "infinite" rock below the pit is a stack of black square "blocks" — each with a light gray plus/cross centered on it, arms reaching to the middle of each of the block's four sides — standing right at the pit's own boundary (against the walkway ring, not the room's outer edge) and reaching far enough down that the camera (fixed min pitch, max zoom) can never see its bottom from any angle the player can reach — not a real second layer of the grid, which stays a flat 2D plane. Only the handful of blocks nearest the rim are actually textured; the remaining depth is one plain filler panel, since nothing below the visible few is ever seen. Deliberately a rim wall and not a slab under the whole tile: a full-footprint column reaching up to ground level would sit in front of (above) the sunk floor from any downward-looking angle and hide it entirely — an earlier version had exactly that bug, rendering as one solid black box per tile. Interior pit tiles have no elevation seam against their neighbors and don't need a wall at all, same as ordinary floor never does.
- A low fence rail rings every outward-facing edge of the pit except one — the middle tile of the pit's own south edge, which gets a three-step staircase down into the pit instead, flanked by two gate posts ("one staircase with a gate"). The fence, rim wall, and staircase/gate are all cosmetic; the tile underneath stays ordinary walkable Floor.
- Same merge-on-adjacent-placement rule as Slime Hatchery, above — extending an existing Jail into a bigger rectangle (itself allowed to be smaller than the 5x5 minimum, same as every other mergeable room type's extensions) recomputes the fence/rim wall/gate for the new pit boundary. A merge can only grow the room, which can only turn a former ring tile into a pit tile (if the seam between the two merged pieces becomes interior) — never the reverse, so an already-sunk tile's pit sink is never undone by a merge.
- Selling a Jail resets its tiles' pit depth back to 0 (ordinary flush floor) on top of the usual generic Sell refund (`LairManager.TrySellRoom`), so nothing built there afterward inherits a leftover sunken tile.

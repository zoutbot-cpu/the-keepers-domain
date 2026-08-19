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
- Exp-per-level curve: TBD.

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
- Nothing currently grants exp (no tasks/training/combat reward it yet) — Imps are wired for leveling but always sit at level 1 in practice until an exp source exists.
- Per-level growth values on `ImplingAgent` are placeholder tunables, not final balance.

## Rooms

### Slime Hatchery
Slimes are bred here in a chicken-coop-like box. Food for barbaric creatures.

- Minimum size: 3x3, with the chicken coop box structure occupying the middle tile.
- If larger than 3x3 and there's no single middle tile (even width/height), the coop structure goes in the square one tile in from each edge on the top-right corner.
- Breeds 1 slime every 2 seconds, capped at 1 slime per tile the room occupies.
- Slimes are visible little blue balls that wander freely within the hatchery's own tiles. If a slime ever ends up off those tiles (e.g. the hatchery is sold), it disappears.

### Bacon Beacon
Offer up slimes to the gods of good taste, and receive bacon instead. Food for intelligent creatures.

- Minimum size: 4x4, with a shrine occupying the middle 2x2 — a tube going up and a tube going down.
- Implings transport slimes here to convert them to bacon: 1 slime = 4 bacon.
- Storage cap: 12 bacon per Bacon Beacon tile adjacent to the shrine structure (so implings aren't overtasked).

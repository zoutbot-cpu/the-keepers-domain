# Project Brief: Dungeon Lord (working title)

Mobile dig-and-build dungeon management game, inspired by Dungeon Keeper but with fully original IP (no EA names/assets/logos — original creature/room/game names only).

## Stack
- Engine: Unity (Personal license — free, non-commercial, no revenue)
- Target: iOS + Android, touch-first controls, landscape primary
- 2.5D isometric, low-poly/stylized art (placeholder primitives fine for prototype)

## Phase 1 Goal (start here)
Build a single-scene prototype proving the core loop feels good on touch:
1. Tap-and-drag to select rock tiles for excavation
2. Imp-equivalent minion auto-digs queued tiles over time
3. One room type (Lair) placeable once territory is claimed
4. One minion type spawns and idles/works in the Lair
5. Pinch-zoom + pan camera (isometric top-down)

No UI polish, no combat, no economy yet — just: dig → claim → build → minion appears.

## Core Design Reference
- Digging: tap-drag queues tiles, minions execute automatically (not instant)
- Territory: claimed via portal-tile influence radius, not manual per-tile claiming
- Controls: one-handed thumb-reachable action zone, radial menu for spells (later phase)
- No predatory monetization — cosmetic-only if any IAP is ever added

## Repo Structure to Set Up
```
/Assets
  /Scripts
    /Grid        - tile/rock data, dig queue logic
    /Minions     - minion behavior, pathing, job system
    /Rooms       - room placement, territory claiming
    /Camera      - pinch/pan/zoom controls
  /Scenes
    Prototype.unity
  /Prefabs
/Docs
  design-doc.md  (full doc, paste in separately)
```

## Constraints
- Unity Personal tier only (no paid features needed at this stage)
- Original names/art only — nothing from EA's Dungeon Keeper
- Keep systems modular — digging, minion AI, and room placement should be decoupled so later phases (combat, economy) can plug in without rewrites

Start by scaffolding the grid/dig system and a basic touch input handler.

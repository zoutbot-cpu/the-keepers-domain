using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Grid
{
    /// Per-tile rendering: building and refreshing each tile's GameObject
    /// (wall meshes, floor materials, owner tint, fog), floor grout, the wall
    /// selection outline and half-wall mode.
    public partial class DungeonGrid
    {
        [SerializeField] private Color _rockColor = new Color(0.35f, 0.32f, 0.3f);
        // Was a near-black tint from back when Reinforced was just a
        // flat-colored cube and needed a dark hue of its own to read as
        // distinct from plain Rock. Now that it's the dungeon_pack's own
        // grey-brick mesh (which already reads as visually distinct on
        // its own), that same dark tint was instead crushing the brick
        // texture toward black — lightened to near-neutral so the
        // texture's own gray shows through, while still leaving headroom
        // for the damage-lerp tint to read clearly against it.
        [SerializeField] private Color _rockReinforcedColor = new Color(0.85f, 0.85f, 0.85f);

        /// Bedrock — darker than a reinforced wall, per the brief.
        [SerializeField] private Color _bedrockColor = new Color(0.07f, 0.06f, 0.055f);

        [SerializeField] private Color _rockDamagedColor = new Color(0.25f, 0.12f, 0.1f);
        [SerializeField] private Color _rockUnreachableColor = new Color(0.2f, 0.28f, 0.38f);
        [SerializeField] private Color _floorUnclaimedColor = new Color(0.2f, 0.18f, 0.15f);
        [SerializeField] private Color _floorClaimedColor = new Color(0.25f, 0.4f, 0.25f);
        [SerializeField] private Color _roomColor = new Color(0.55f, 0.15f, 0.5f);
        [SerializeField] private Color _roomDamagedColor = new Color(0.2f, 0.05f, 0.18f);
        [SerializeField] private Color _goldWallColor = new Color(0.5f, 0.42f, 0.2f);
        [SerializeField] private Color _regeneratingGoldWallColor = new Color(0.55f, 0.45f, 0.15f);
        [SerializeField] private Color _manaCrystalWallColor = new Color(0.15f, 0.5f, 0.55f);

        // New terrain tiles (see TileType/SetTerrainFeature) — placed today
        // by a dev-only Build-menu tool pending a real map generator.
        [SerializeField] private Color _waterColor = new Color(0.15f, 0.35f, 0.75f);
        [SerializeField] private Color _lavaColor = new Color(0.8f, 0.3f, 0.1f);
        [SerializeField] private Color _chasmColor = new Color(0.03f, 0.03f, 0.04f);
        [SerializeField] private Color _holyGroundColor = new Color(0.92f, 0.92f, 0.88f);
        [SerializeField] private Color _unholyGroundColor = new Color(0.06f, 0.04f, 0.06f);

        // A full-cell dark-gray slab set just under each Claimed floor tile,
        // showing through the ~5% gap the 0.95-scale floor cube leaves on
        // every side so a paved area reads as grouted tiles instead of
        // cubes hovering over a void — same trick JailManager's "Seam" runs
        // under its room-floor panels. Parallel to _visuals; only ever
        // non-null for a plain Claimed floor tile (see UpdateFloorGrout).
        [SerializeField] private Color _claimedGroutColor = new Color(0.16f, 0.16f, 0.17f);

        // dungeon_pack wall meshes are pivoted at their base (min Y ≈ 0) —
        // see DungeonPackWallSetup's bounds log — and RefreshVisual seats
        // that base half a unit below the tile centre so it sits flush with
        // the floor. "Half wall" mode then scales height about this base.
        private const float WallBaseLocalY = -0.5f;

        // "Half wall" display mode (see SetHalfWalls, wired to BottomMenuBar's
        // Settings menu): every wall mesh is squashed to half height on Y
        // about its base, so the bottom half stays put and the top is pressed
        // down to the midpoint — a see-over view that changes nothing about
        // the walls themselves. Purely cosmetic.
        private bool _halfWalls;

        /// The wall tile currently selected (see SetSelectedWall) — null
        /// when nothing is. Only ever one at a time; _selectionOutline is
        /// the single "inverted hull" visual for it (see SetSelectedWall).
        private Vector2Int? _selectedWallCoord;
        private GameObject _selectionOutline;
        private const float SelectionOutlineScale = 1.04f;

        private static MaterialPropertyBlock _sharedPropertyBlock;
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");

        /// Re-runs RefreshVisual across every tile — needed whenever
        /// something that affects a tile's *color* (not its type/shape)
        /// changes after tiles have already been painted, since
        /// RefreshVisual only reruns when something explicitly asks it to.
        /// OwnerColors (see PlayerColor and
        /// LevelDesignerSession.RefreshGridOwnerColors) qualifies — changing a
        /// player's color in the Level Designer must retint every already-
        /// placed Claimed tile of theirs immediately, not just tiles
        /// painted from then on).
        public void RefreshAllVisuals(bool suppressNotify = false)
        {
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    RefreshVisual(new Vector2Int(x, y), suppressNotify);
                }
            }
        }

        /// Re-renders one tile after its fog-of-war visibility changed (see
        /// FogOfWar). suppressNotify: the tile's real state hasn't changed,
        /// so listeners (NetGame's replication delta) must not fire.
        public void NotifyFogChanged(Vector2Int coord)
        {
            if (InBounds(coord))
            {
                RefreshVisual(coord, suppressNotify: true);
            }
        }

        private void BuildAllVisuals()
        {
            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    var root = new GameObject($"Tile_{x}_{y}");
                    root.transform.SetParent(transform, false);
                    root.transform.localPosition = GridToWorld(coord);
                    _visuals[x, y] = root;
                    RefreshVisual(coord);
                }
            }
        }

        private void RefreshVisual(Vector2Int coord, bool suppressNotify = false)
        {
            var tile = _tiles[coord.x, coord.y];
            var realTile = tile;
            var visual = _visuals[coord.x, coord.y];
            if (visual == null)
            {
                return;
            }

            // Fog of war (Fog is null on every non-fogged path — see the
            // property). An Unseen tile is drawn as plain unmined Rock so it
            // gives nothing away; an Explored one keeps its real shape but is
            // dimmed, and its moving parts / props / creatures are hidden via
            // the decoration toggles below + FogObscurable + the health-ring
            // hook.
            var fogView = Fog != null ? Fog.ViewAt(coord) : FogView.Visible;
            var fogHidden = fogView == FogView.Unseen;
            var fogDim = fogView == FogView.Explored;
            if (fogHidden)
            {
                tile = TileState.Rock;
            }

            Color color;
            if (tile.HasRoom)
            {
                // Same damaged->full HP lerp Rock walls use — darkens
                // toward _roomDamagedColor as a room tile takes damage (see
                // ApplyRoomDamage) and eases back as an impling repairs it
                // (see ApplyRoomRepair), so repair progress is actually
                // visible rather than silent tracked data.
                var hpFraction = Mathf.Clamp01(tile.Hp / (float)TileState.RoomMaxHp);
                color = Color.Lerp(_roomDamagedColor, _roomColor, hpFraction);
            }
            else if (tile.Type == TileType.Rock)
            {
                // Queued-for-dig/reinforce no longer get their own color
                // here — a floating icon communicates that now instead
                // (see UpdateQueuedActionIcon), so a queued wall just
                // shows its ordinary type color underneath. Unreachable
                // is kept as a color, not an icon — it's a warning about
                // the queue itself (an impling can't path to it), not the
                // queued action, and stacking a 4th icon meaning on top of
                // the other 3 wasn't worth it.
                Color baseColor;
                if ((tile.IsQueuedForDig || tile.IsQueuedForReinforce) && tile.IsUnreachable)
                {
                    baseColor = _rockUnreachableColor;
                }
                else if (tile.IsBedrock)
                {
                    baseColor = _bedrockColor;
                }
                else if (tile.IsReinforced)
                {
                    baseColor = _rockReinforcedColor;
                }
                else if (tile.WallResourceType == WallResourceType.GoldWall)
                {
                    baseColor = _goldWallColor;
                }
                else if (tile.WallResourceType == WallResourceType.RegeneratingGoldWall)
                {
                    baseColor = _regeneratingGoldWallColor;
                }
                else if (tile.WallResourceType == WallResourceType.ManaCrystalWall)
                {
                    baseColor = _manaCrystalWallColor;
                }
                else
                {
                    baseColor = _rockColor;
                }

                var hpFraction = Mathf.Clamp01(tile.Hp / (float)tile.MaxHp);
                color = Color.Lerp(_rockDamagedColor, baseColor, hpFraction);
            }
            else if (tile.Type == TileType.Water)
            {
                color = _waterColor;
            }
            else if (tile.Type == TileType.Lava)
            {
                color = _lavaColor;
            }
            else if (tile.Type == TileType.Chasm)
            {
                color = _chasmColor;
            }
            else if (tile.Type == TileType.HolyGround)
            {
                color = _holyGroundColor;
            }
            else if (tile.Type == TileType.UnholyGround)
            {
                color = _unholyGroundColor;
            }
            else if (tile.Ownership == TileOwnership.Claimed)
            {
                // Tinted toward the owning player's color when the Level
                // Designer has opted in (see TintFloorByOwner/OwnerColors/
                // TileState.OwnerId) — false in ordinary gameplay, where
                // this just falls back to the plain claimed color exactly
                // as before.
                color = TintFloorByOwner && tile.OwnerId >= 0 && OwnerColors != null && tile.OwnerId < OwnerColors.Length
                    ? Color.Lerp(_floorClaimedColor, OwnerColors[tile.OwnerId], 0.6f)
                    : _floorClaimedColor;
            }
            else
            {
                color = _floorUnclaimedColor;
            }

            // Explored-but-unwatched ground reads dimmer than live territory.
            if (fogDim)
            {
                color = new Color(color.r * FogOfWar.ExploredDim, color.g * FogOfWar.ExploredDim,
                    color.b * FogOfWar.ExploredDim, color.a);
            }

            var wallPrefab = GetWallMeshPrefab(tile);
            GameObject terrainMeshPrefab = tile.Type switch
            {
                TileType.Water => _waterMesh,
                TileType.Lava => _lavaMesh,
                _ => null
            };
            var meshPrefab = wallPrefab != null ? wallPrefab : terrainMeshPrefab;

            bool needsRebuild = _visualChildren[coord.x, coord.y] == null || _currentWallPrefab[coord.x, coord.y] != meshPrefab;
            if (needsRebuild)
            {
                if (_visualChildren[coord.x, coord.y] != null)
                {
                    Destroy(_visualChildren[coord.x, coord.y]);
                }

                GameObject child;
                if (wallPrefab != null)
                {
                    // Positioning/scaling (base flush with the floor, full
                    // cellSize on X/Z so neighbours butt together, optional
                    // half-height squash) all live in ApplyWallChildTransform
                    // — re-applied unconditionally below so a fog transition
                    // or a half-wall toggle always corrects the height.
                    child = Instantiate(wallPrefab, visual.transform, false);
                }
                else if (terrainMeshPrefab != null)
                {
                    // Already an exact 1x1 quad sitting just below its own
                    // local Y=0 (its "floor plane" per the pack's own
                    // LIQUID_TILES_README.txt) — putting the root at
                    // FloorSurfaceY lines that up with where this project's
                    // floor tiles actually sit, no scale correction needed.
                    child = Instantiate(terrainMeshPrefab, visual.transform, false);
                    child.transform.localPosition = new Vector3(0f, FloorSurfaceY, 0f);
                    child.transform.localRotation = Quaternion.identity;
                    child.transform.localScale = Vector3.one * _cellSize;

                    if (meshPrefab == _lavaMesh)
                    {
                        LiquidParticles.AttachLavaBubbles(child.transform, _cellSize);
                    }
                    else if (meshPrefab == _waterMesh)
                    {
                        WaterWaveMesh.Attach(child);
                    }
                }
                else
                {
                    child = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    child.transform.SetParent(visual.transform, false);
                }

                child.name = "Visual";
                _visualChildren[coord.x, coord.y] = child;
                _currentWallPrefab[coord.x, coord.y] = meshPrefab;
            }

            var visualChild = _visualChildren[coord.x, coord.y];
            if (terrainMeshPrefab != null)
            {
                // Water/lava textures already carry their own real color —
                // tinting with the old flat _waterColor/_lavaColor (still
                // used by the cube fallback below) would just muddy them,
                // same lesson as the wall meshes.
                ApplyTint(visualChild, fogDim
                    ? new Color(FogOfWar.ExploredDim, FogOfWar.ExploredDim, FogOfWar.ExploredDim)
                    : Color.white);
            }
            else if (wallPrefab != null)
            {
                // Height/scale every refresh, not just on rebuild — a fog
                // Unseen wall stays FULL height even with "half walls" on, so
                // squashing the dungeon to see over it can't also expose the
                // fog by shrinking the rock that's standing in for it.
                ApplyWallChildTransform(visualChild.transform, forceFullHeight: fogHidden);

                // The Reinforced mesh's brick/cap/orb are one combined
                // renderer (see PlayerColor's own comment) — a uniform
                // property-block tint can't leave the orb's player color
                // alone while still tinting the brick/cap, so in its
                // normal resting state (nothing to actually communicate)
                // this skips tinting entirely and lets each material's own
                // baked color show: gray brick/cap, player-colored orb.
                // Queued/damaged states still tint the whole renderer
                // uniformly (orb included) — an acceptable, rare/transient
                // exception rather than something worth losing the correct
                // steady-state look over.
                bool isPristineReinforced = wallPrefab == _wallMeshReinforced
                    && !tile.IsQueuedForDig && !tile.IsQueuedForReinforce && tile.Hp >= tile.MaxHp
                    && !fogDim;
                if (isPristineReinforced)
                {
                    ClearTint(visualChild);
                }
                else
                {
                    ApplyTint(visualChild, color);
                }

                // Per-owner orb color (see ApplyOrbOwnerColor) — runs
                // every refresh, independent of isPristineReinforced,
                // since the orb's own material slot is swapped directly
                // rather than tinted through the property block either
                // branch above uses.
                if (wallPrefab == _wallMeshReinforced)
                {
                    ApplyOrbOwnerColor(visualChild, tile.OwnerId);
                }
            }
            else
            {
                visualChild.transform.localPosition = Vector3.down * (tile.Type == TileType.Rock ? 0f : (0.5f + tile.PitDepth));
                visualChild.transform.localScale = new Vector3(_cellSize * 0.95f, tile.Type == TileType.Rock ? 1f : 0.15f, _cellSize * 0.95f);

                var renderer = visualChild.GetComponent<Renderer>();
                // Plain Claimed/Unclaimed floor gets a real dungeon_pack
                // texture; every other non-mesh case (rooms, chasm/holy
                // ground, build-queued, or water/lava if their mesh
                // failed to load) keeps today's flat-colored look.
                bool isPlainFloor = tile.Type == TileType.Floor && !tile.HasRoom && !tile.IsQueuedForBuild;
                if (isPlainFloor && tile.Ownership == TileOwnership.Claimed && _floorClaimedMaterial != null)
                {
                    renderer.sharedMaterial = _floorClaimedMaterial;
                    var variantIndex = Mathf.Abs(coord.x * 92821 + coord.y * 68917) % _claimedTileTextures.Length;
                    ApplyTint(visualChild, color, _claimedTileTextures[variantIndex]);
                }
                else if (isPlainFloor && tile.Ownership == TileOwnership.Unclaimed && _floorUnclaimedMaterial != null)
                {
                    renderer.sharedMaterial = _floorUnclaimedMaterial;
                    ApplyTint(visualChild, color);
                }
                else
                {
                    renderer.sharedMaterial = _plainFloorMaterial;
                    ApplyTint(visualChild, color);
                }
            }

            UpdateFloorGrout(coord, tile);
            // Real tile, not the fog-faked Rock — your own queued Mine /
            // Reinforce / Construct marker stays visible through the fog so
            // you can see what you've already selected out in the dark.
            UpdateQueuedActionIcon(coord, realTile);

            // Fog: the separate decoration child (gold nuggets / chasm spikes
            // / holy-ground star — see _wallDecorations) would otherwise
            // float on an Unseen tile's rock face. Grout and the queued-action
            // icon self-clear above, since `tile` is plain Rock when hidden.
            var wallDecoration = _wallDecorations[coord.x, coord.y];
            if (wallDecoration != null && wallDecoration.activeSelf == fogHidden)
            {
                wallDecoration.SetActive(!fogHidden);
            }

            // Every path that turns a tile into Floor (CarveRoom/Rect,
            // CompleteDig, the dev terrain tool, ...) already ends up here
            // via RefreshVisual(coord) for that tile — piggybacking the
            // torch-neighbor scan on that single choke point covers all of
            // them without touching each call site. realTile (not the
            // fog-faked one) so a currently-fogged tile still resolves —
            // torch placement is gameplay state, not a visual concern.
            if (realTile.Type == TileType.Floor)
            {
                ConsiderTorchesOnNeighbors(coord);
            }

            // Selection outline is a duplicate of the wall's own current
            // visual (see SetSelectedWall) — if this tile is the selected
            // one and its shape/prefab just changed (needsRebuild above),
            // the outline would otherwise still be duplicating the old,
            // now-destroyed mesh. Re-running the same selection keeps it
            // in sync; harmless/cheap on every other call since
            // SetSelectedWall no-ops when the coord isn't selected.
            if (_selectedWallCoord == coord)
            {
                SetSelectedWall(coord);
            }

            // A fog-only refresh hasn't changed the tile's real state — don't
            // fire a replication delta (NetGame) for a purely visual change.
            if (!suppressNotify)
            {
                TileChanged?.Invoke(coord);
            }
        }

        /// Ensures/clears the dark-gray grout slab under a plain Claimed
        /// floor tile — see the _floorGrout field's own header. Full cell
        /// footprint (so adjacent slabs meet and read as continuous grout
        /// lines) sitting ~1cm below the textured floor cube's top so it
        /// only shows in the gaps. Cheap to call every RefreshVisual: it
        /// only creates/destroys the slab when the claimed-ness actually
        /// changes, otherwise just re-applies the transform/tint.
        private void UpdateFloorGrout(Vector2Int coord, TileState tile)
        {
            bool wantsGrout = tile.Type == TileType.Floor
                && tile.Ownership == TileOwnership.Claimed
                && !tile.HasRoom
                && !tile.IsQueuedForBuild;

            var existing = _floorGrout[coord.x, coord.y];
            if (!wantsGrout)
            {
                if (existing != null)
                {
                    Destroy(existing);
                    _floorGrout[coord.x, coord.y] = null;
                }
                return;
            }

            if (existing == null)
            {
                existing = GameObject.CreatePrimitive(PrimitiveType.Cube);
                existing.name = "Grout";
                existing.transform.SetParent(_visuals[coord.x, coord.y].transform, false);
                Destroy(existing.GetComponent<Collider>());
                existing.GetComponent<Renderer>().sharedMaterial = _plainFloorMaterial;
                _floorGrout[coord.x, coord.y] = existing;
            }

            existing.transform.localPosition = Vector3.down * (0.5f + tile.PitDepth);
            existing.transform.localScale = new Vector3(_cellSize, 0.13f, _cellSize);
            ApplyTint(existing, _claimedGroutColor);
        }

        /// Selects coord's tile for the yellow outline highlight, or
        /// clears the current selection if coord is null / out of bounds
        /// / has no visual built yet. Only one tile can be selected at a
        /// time — selecting a new one replaces whatever was selected
        /// before. The outline itself is an "inverted hull": a duplicate
        /// of the tile's own current visual (whatever mesh/cube
        /// _visualChildren currently holds for it — not Rock-specific,
        /// despite the name/its original gameplay-only use case, see the
        /// Level Designer's edit mode), scaled up slightly and rendered
        /// with a front-face-culled flat yellow material (see
        /// _selectionOutlineMaterial) so only its silhouette margin shows
        /// around the real mesh.
        public void SetSelectedWall(Vector2Int? coord)
        {
            if (_selectionOutline != null)
            {
                Destroy(_selectionOutline);
                _selectionOutline = null;
            }

            _selectedWallCoord = null;

            if (coord == null || !InBounds(coord.Value))
            {
                return;
            }

            var source = _visualChildren[coord.Value.x, coord.Value.y];
            if (source == null)
            {
                return;
            }

            _selectedWallCoord = coord;

            var outline = Instantiate(source, source.transform.parent, false);
            outline.name = "SelectionOutline";
            outline.transform.localPosition = source.transform.localPosition;
            outline.transform.localRotation = source.transform.localRotation;
            outline.transform.localScale = source.transform.localScale * SelectionOutlineScale;

            foreach (var renderer in outline.GetComponentsInChildren<Renderer>())
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    materials[i] = _selectionOutlineMaterial;
                }

                renderer.sharedMaterials = materials;

                var collider = renderer.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }
            }

            _selectionOutline = outline;
        }

        /// Seats a wall mesh child in its tile: base flush with the floor,
        /// full cellSize on X/Z so adjacent walls read as one continuous
        /// surface. In "half wall" mode (see SetHalfWalls) the mesh is
        /// squashed to half height on Y about its base — the bottom half
        /// stays put and the top is pressed down to the midpoint.
        /// forceFullHeight overrides that for a fog-of-war Unseen tile: the
        /// rock standing in for the fog must stay full height regardless, or
        /// squashing the dungeon to see over your own walls would also let
        /// you see over the fog.
        private void ApplyWallChildTransform(Transform child, bool forceFullHeight = false)
        {
            var heightScale = _halfWalls && !forceFullHeight ? 0.5f : 1f;
            child.localPosition = new Vector3(0f, WallBaseLocalY, 0f);
            child.localRotation = Quaternion.identity;
            child.localScale = new Vector3(_cellSize, heightScale, _cellSize);
        }

        /// Toggles "half wall" display mode — every wall mesh is squashed to
        /// half its height about its base (bottom half kept, top pressed
        /// down), letting the player see over the dungeon without altering
        /// the walls in any gameplay sense. Purely visual; wired to
        /// BottomMenuBar's Settings menu.
        public void SetHalfWalls(bool enabled)
        {
            if (_halfWalls == enabled)
            {
                return;
            }

            _halfWalls = enabled;

            for (int x = 0; x < _width; x++)
            {
                for (int y = 0; y < _height; y++)
                {
                    var coord = new Vector2Int(x, y);
                    var child = _visualChildren[x, y];
                    // GetWallMeshPrefab(real tile) misses a fogged wall over
                    // real Floor — but that child is a fog rock mesh and must
                    // stay full height, which is what it already is, so
                    // skipping it is correct.
                    if (child != null && GetWallMeshPrefab(_tiles[x, y]) != null)
                    {
                        var fogHidden = Fog != null && Fog.ViewAt(coord) == FogView.Unseen;
                        ApplyWallChildTransform(child.transform, forceFullHeight: fogHidden);
                    }

                    // Re-seat any dig/reinforce icon on this tile at the new
                    // wall height (UpdateQueuedActionIcon only runs on a tile
                    // change, not on this toggle).
                    var iconRoot = _queuedActionIcons[x, y];
                    var iconKind = _queuedActionIconKind[x, y];
                    if (iconRoot != null && (iconKind == QueuedIcon.Pickaxe || iconKind == QueuedIcon.Shield))
                    {
                        iconRoot.transform.localPosition = new Vector3(0f, WallFaceIconLocalY(coord), 0f);
                    }
                }
            }

            // The selection outline is a clone of one wall's child transform
            // (see SetSelectedWall) — rebuild it so it tracks the new height.
            if (_selectedWallCoord.HasValue)
            {
                SetSelectedWall(_selectedWallCoord);
            }
        }

        /// Which dungeon_pack wall prefab (if any) a tile should render as
        /// — null means fall back to the plain colored cube (only
        /// possible today if a mesh failed to load; every Rock variant
        /// has a dedicated mesh now). IsBedrock/IsReinforced take
        /// priority over WallResourceType since TileState keeps them
        /// mutually exclusive already (see RequestReinforce/SetBedrock).
        private GameObject GetWallMeshPrefab(TileState tile)
        {
            if (tile.Type != TileType.Rock)
            {
                return null;
            }

            if (tile.IsBedrock)
            {
                return _wallMeshBedrock;
            }

            if (tile.IsReinforced)
            {
                return _wallMeshReinforced;
            }

            switch (tile.WallResourceType)
            {
                case WallResourceType.GoldWall:
                    return _wallMeshGold;
                case WallResourceType.RegeneratingGoldWall:
                    return _wallMeshGoldRegen;
                case WallResourceType.ManaCrystalWall:
                    return _wallMeshManaCrystal;
                default:
                    return _wallMeshStone;
            }
        }

        /// Applies a MaterialPropertyBlock color tint to every renderer
        /// under visual, instead of touching .material (which would
        /// instantiate a per-object material copy) — the same shared
        /// M_StoneWall material is reused across every wall tile.
        /// baseMapOverride optionally swaps which texture a shared
        /// material's _BaseMap shows for this instance (e.g. picking one
        /// of the 4 claimed-floor texture variants) — always explicitly
        /// cleared/set from scratch each call (Clear(), not GetPropertyBlock
        /// first) so a texture override from a tile's previous state (a
        /// different WallResourceType, a different floor Ownership, ...)
        /// can never linger on a renderer that's since switched away from
        /// needing one.
        /// Removes any per-instance tint override entirely, so a renderer
        /// falls back to each of its materials' own baked-in color —
        /// used for the Reinforced wall's pristine state (see its own
        /// call site) rather than ApplyTint(..., Color.white), since a
        /// white property-block override would still multiply-blend with
        /// (and wash out) M_ReinforcedOrb's own colored/emissive look.
        private static void ClearTint(GameObject visual)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                renderer.SetPropertyBlock(null);
            }
        }

        private void ApplyTint(GameObject visual, Color color, Texture2D baseMapOverride = null)
        {
            _sharedPropertyBlock ??= new MaterialPropertyBlock();
            var renderers = visual.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                _sharedPropertyBlock.Clear();
                _sharedPropertyBlock.SetColor(BaseColorId, color);
                if (baseMapOverride != null)
                {
                    _sharedPropertyBlock.SetTexture(BaseMapId, baseMapOverride);
                }

                renderer.SetPropertyBlock(_sharedPropertyBlock);
            }
        }
    }
}

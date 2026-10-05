using System;
using System.Collections.Generic;
using UnityEngine;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Grid
{
    /// The floating Mine / Reinforce / Construct icons drawn over tiles with
    /// a queued job.
    public partial class DungeonGrid
    {
        // Floats the Mine/Reinforce icon just above a wall's peak
        // (dungeon_pack wall meshes stand ~2.03 units tall on a 1-unit
        // cell — see DungeonPackWallSetup's bounds log — based at
        // floorSurfaceY - 0.5, i.e. topping out around 1.53).
        private const float QueuedIconFloatHeight = 1.75f;

        // The Construct-wall frame stands roughly where the future wall
        // will rise, base at the floor surface rather than floating high
        // like the Mine/Reinforce icons — see BuildConstructIcon.
        private const float ConstructFrameWidth = 0.8f;
        private const float ConstructFrameHeight = 1.6f;
        private const float ConstructFrameBarThickness = 0.05f;

        /// Ensures/clears the floating icon for a queued Rock/Floor tile —
        /// see QueuedIcon's own header for why this replaced flat color
        /// tinting. Only rebuilds when the icon kind actually changes
        /// (RefreshVisual fires per dig-damage hit).
        private void UpdateQueuedActionIcon(Vector2Int coord, TileState tile)
        {
            // A player only sees their own selections. QueuedByOwnerId is
            // set alongside every IsQueuedFor* flag (see RequestDig et al.),
            // so a mismatch here means this is a rival keeper's job.
            var mine = LocalViewerOwnerId < 0 || tile.QueuedByOwnerId == LocalViewerOwnerId;

            QueuedIcon icon;
            if (!mine)
            {
                icon = QueuedIcon.None;
            }
            else if (tile.Type == TileType.Rock && tile.IsQueuedForDig)
            {
                icon = QueuedIcon.Pickaxe;
            }
            else if (tile.Type == TileType.Rock && tile.IsQueuedForReinforce)
            {
                icon = QueuedIcon.Shield;
            }
            else if (tile.Type == TileType.Floor && tile.IsQueuedForBuild)
            {
                icon = QueuedIcon.Hammer;
            }
            else
            {
                icon = QueuedIcon.None;
            }

            if (_queuedActionIconKind[coord.x, coord.y] == icon)
            {
                return;
            }

            _queuedActionIconKind[coord.x, coord.y] = icon;

            var existing = _queuedActionIcons[coord.x, coord.y];
            if (existing != null)
            {
                Destroy(existing);
                _queuedActionIcons[coord.x, coord.y] = null;
            }

            if (icon == QueuedIcon.None)
            {
                return;
            }

            var parent = _visuals[coord.x, coord.y].transform;
            GameObject iconRoot = icon switch
            {
                QueuedIcon.Pickaxe => BuildPickaxeIcon(parent, tile.QueuedByOwnerId),
                QueuedIcon.Shield => BuildShieldIcon(parent, tile.QueuedByOwnerId),
                QueuedIcon.Hammer => BuildConstructIcon(parent),
                _ => null
            };
            _queuedActionIcons[coord.x, coord.y] = iconRoot;

            // Pickaxe / Shield sit right on the wall's own top surface
            // (Hammer sits on the floor and is unaffected — it keeps its
            // own Y, set inside BuildConstructIcon).
            if (iconRoot != null && (icon == QueuedIcon.Pickaxe || icon == QueuedIcon.Shield))
            {
                iconRoot.transform.localPosition = new Vector3(0f, WallFaceIconLocalY(coord), 0f);
            }
        }

        // "A tiny bit above the face of the wall" per the brief — not a
        // fixed float height (that read as hovering far above the tile,
        // especially with Half Walls on), but measured off the wall mesh's
        // own current top so it hugs whatever's actually there.
        private const float WallFaceIconGap = 0.03f;

        /// Local Y for a wall-face queued icon (Pickaxe / Shield): the wall
        /// mesh's own current top (works for both full and half-wall height
        /// — see ApplyWallChildTransform — since it reads the live renderer
        /// bounds rather than assuming a size) plus a small gap. Falls back
        /// to the old fixed float height if there's no wall mesh yet (rare —
        /// only reachable if the icon somehow builds before RefreshVisual's
        /// own wall-mesh step, which precedes this call in practice).
        private float WallFaceIconLocalY(Vector2Int coord)
        {
            var child = _visualChildren[coord.x, coord.y];
            var renderer = child != null ? child.GetComponentInChildren<Renderer>() : null;
            if (renderer == null)
            {
                return QueuedIconFloatHeight;
            }

            var localTop = _visuals[coord.x, coord.y].transform
                .InverseTransformPoint(new Vector3(0f, renderer.bounds.max.y, 0f)).y;
            return localTop + WallFaceIconGap;
        }

        private const float QueuedIconTextureSize = 0.5f;

        /// The queued-job icon's real look: a flat textured decal (see
        /// Assets/Resources/Dungeon/Icons/{Pickaxe,Shield}) tinted per the
        /// queuing keeper's own color at the source (one PNG per palette
        /// color, not tinted at runtime) — "so we know who selected the
        /// wall for mining," per the brief, ahead of allied-keeper
        /// visibility later. Falls back to the original primitive-built
        /// shape (see BuildPickaxeIconFallback/BuildShieldIconFallback) if
        /// the matching texture hasn't been found, same graceful-
        /// degradation convention every other DungeonPack-backed prop uses.
        private GameObject BuildPickaxeIcon(Transform parent, int ownerId)
        {
            var texture = GetQueuedIconTexture("Pickaxe", ownerId);
            return texture != null
                ? BuildWallFaceIconQuad(parent, "QueuedIcon_Mine", texture)
                : BuildPickaxeIconFallback(parent);
        }

        private GameObject BuildShieldIcon(Transform parent, int ownerId)
        {
            var texture = GetQueuedIconTexture("Shield", ownerId);
            return texture != null
                ? BuildWallFaceIconQuad(parent, "QueuedIcon_Reinforce", texture)
                : BuildShieldIconFallback(parent);
        }

        // folder ("Pickaxe"/"Shield") -> 8 textures, one per
        // LevelDesignerColors.Palette entry, loaded once and reused.
        private readonly Dictionary<string, Texture2D[]> _queuedIconTextures = new Dictionary<string, Texture2D[]>();

        private Texture2D GetQueuedIconTexture(string folder, int ownerId)
        {
            if (!_queuedIconTextures.TryGetValue(folder, out var textures))
            {
                textures = new Texture2D[LevelDesignerColors.Palette.Length];
                _queuedIconTextures[folder] = textures;
            }

            var colorIndex = ResolveClosestPaletteIndex(GetOwnerColor(ownerId));
            if (textures[colorIndex] == null)
            {
                var colorName = LevelDesignerColors.Names[colorIndex].ToLowerInvariant();
                textures[colorIndex] = Resources.Load<Texture2D>($"Dungeon/Icons/{folder}/icon_{folder.ToLowerInvariant()}_{colorName}");
            }

            return textures[colorIndex];
        }

        /// Which LevelDesignerColors.Palette entry color is closest to —
        /// ownerId's resolved color (GetOwnerColor) doesn't necessarily
        /// come FROM that exact palette (a Level-Designer-authored roster
        /// could pick any color), so this maps back to whichever pre-tinted
        /// icon PNG reads closest instead of assuming ownerId indexes the
        /// palette directly.
        private static int ResolveClosestPaletteIndex(Color color)
        {
            var palette = LevelDesignerColors.Palette;
            var closestIndex = 0;
            var closestDistance = float.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                var distance = ((Vector4)palette[i] - (Vector4)color).sqrMagnitude;
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestIndex = i;
                }
            }

            return closestIndex;
        }

        /// A flat, double-sided textured quad lying on the XZ plane (facing
        /// up) — the shared shape both wall-face icons use now.
        private GameObject BuildWallFaceIconQuad(Transform parent, string name, Texture2D texture)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            quad.transform.SetParent(parent, false);
            quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            quad.transform.localScale = Vector3.one * QueuedIconTextureSize;
            quad.GetComponent<Renderer>().sharedMaterial = Prims.NewUnlitTransparentMaterial(texture);
            Destroy(quad.GetComponent<Collider>());
            return quad;
        }

        /// Original primitive-built look — only reached if the real icon
        /// texture hasn't been imported (see GetQueuedIconTexture). A
        /// diagonal handle crossed by a shorter head near one end — read
        /// from roughly above (this project's fixed-ish isometric angle),
        /// same "flat, top-down-legible" convention BuildGroundStar
        /// already uses rather than a billboard that'd need per-frame
        /// facing logic.
        private GameObject BuildPickaxeIconFallback(Transform parent)
        {
            var root = new GameObject("QueuedIcon_Mine");
            root.transform.SetParent(parent, false);

            var handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            handle.name = "Handle";
            handle.transform.SetParent(root.transform, false);
            handle.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
            handle.transform.localScale = new Vector3(0.06f, 0.06f, 0.5f);
            Prims.Tint(handle, new Color(0.4f, 0.28f, 0.15f));
            Destroy(handle.GetComponent<Collider>());

            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            head.name = "Head";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0.1f, 0f, 0.1f);
            head.transform.localRotation = Quaternion.Euler(0f, -45f, 0f);
            head.transform.localScale = new Vector3(0.05f, 0.05f, 0.32f);
            Prims.Tint(head, new Color(0.55f, 0.55f, 0.58f));
            Destroy(head.GetComponent<Collider>());

            return root;
        }

        /// Original primitive-built look — only reached if the real icon
        /// texture hasn't been imported (see GetQueuedIconTexture). A round
        /// disc with a small raised boss in the center — same flat,
        /// top-down-legible convention as the pickaxe icon above.
        private GameObject BuildShieldIconFallback(Transform parent)
        {
            var root = new GameObject("QueuedIcon_Reinforce");
            root.transform.SetParent(parent, false);

            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Disc";
            disc.transform.SetParent(root.transform, false);
            disc.transform.localScale = new Vector3(0.32f, 0.03f, 0.32f);
            Prims.Tint(disc, new Color(0.55f, 0.6f, 0.68f));
            Destroy(disc.GetComponent<Collider>());

            var boss = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            boss.name = "Boss";
            boss.transform.SetParent(root.transform, false);
            boss.transform.localPosition = Vector3.up * 0.02f;
            boss.transform.localScale = Vector3.one * 0.12f;
            Prims.Tint(boss, new Color(0.85f, 0.75f, 0.3f));
            Destroy(boss.GetComponent<Collider>());

            return root;
        }

        /// "A hammer on an empty yellow frame" — a hollow rectangle
        /// standing roughly where the future wall will rise (base at the
        /// floor surface, not floating high like the Mine/Reinforce
        /// icons), built from 4 thin bars the same way BuildGroundStar/
        /// JailManager's fence rails already do, plus a small hammer
        /// shape sitting in the middle of it.
        private GameObject BuildConstructIcon(Transform parent)
        {
            var root = new GameObject("QueuedIcon_Construct");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(0f, FloorSurfaceY + ConstructFrameHeight * 0.5f, 0f);

            BuildFrameBar(root.transform, new Vector3(0f, ConstructFrameHeight * 0.5f, 0f), new Vector3(ConstructFrameWidth, ConstructFrameBarThickness, ConstructFrameBarThickness));
            BuildFrameBar(root.transform, new Vector3(0f, -ConstructFrameHeight * 0.5f, 0f), new Vector3(ConstructFrameWidth, ConstructFrameBarThickness, ConstructFrameBarThickness));
            BuildFrameBar(root.transform, new Vector3(-ConstructFrameWidth * 0.5f, 0f, 0f), new Vector3(ConstructFrameBarThickness, ConstructFrameHeight, ConstructFrameBarThickness));
            BuildFrameBar(root.transform, new Vector3(ConstructFrameWidth * 0.5f, 0f, 0f), new Vector3(ConstructFrameBarThickness, ConstructFrameHeight, ConstructFrameBarThickness));

            var handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            handle.name = "HammerHandle";
            handle.transform.SetParent(root.transform, false);
            handle.transform.localScale = new Vector3(0.05f, 0.4f, 0.05f);
            Prims.Tint(handle, new Color(0.4f, 0.28f, 0.15f));
            Destroy(handle.GetComponent<Collider>());

            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            head.name = "HammerHead";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = Vector3.up * 0.2f;
            head.transform.localScale = new Vector3(0.22f, 0.1f, 0.1f);
            Prims.Tint(head, new Color(0.5f, 0.5f, 0.52f));
            Destroy(head.GetComponent<Collider>());

            return root;
        }

        private static void BuildFrameBar(Transform parent, Vector3 localPosition, Vector3 localScale)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = "FrameBar";
            bar.transform.SetParent(parent, false);
            bar.transform.localPosition = localPosition;
            bar.transform.localScale = localScale;
            Prims.Tint(bar, Color.yellow);
            Destroy(bar.GetComponent<Collider>());
        }
    }
}

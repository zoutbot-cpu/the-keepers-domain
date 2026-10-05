using UnityEngine;

namespace KeepersDomain.Grid
{
    /// Wall-mounted torch props — KayKit dungeon_pack's freestanding
    /// "torch_lit" model (a stake-based torch, not a wall bracket; the
    /// pack ships no wall-mount variant with a matching lit flame) parked
    /// against a wall tile's exposed face rather than embedded flush, so
    /// it reads as mounted without needing bracket geometry this project
    /// doesn't have. Mesh + texture live under Resources/Dungeon/Props
    /// directly (not the usual Art/ + thin Resources/ wrapper prefab
    /// every other dungeon_pack asset uses) because that wrapper pattern
    /// depends on fileIDs Unity's own model importer assigns on first
    /// import — fine to reproduce by hand when copying an already-working
    /// asset (see Tile_Water.prefab), not something to guess for a model
    /// nobody has imported yet. Material is hand-authored (M_Torch, same
    /// file) and assigned here in code rather than via the model's own
    /// .mtl, sidestepping any question of whether Unity's OBJ-import
    /// material step produces something URP-compatible out of the box.
    public static class WallTorches
    {
        private const float Height = 0.35f;
        private const float OutwardOffsetFraction = 0.4f;
        private const float Scale = 0.55f;

        // Hand-tuned after an in-Editor look: tilts the torch back against
        // the wall face (X) and nudges it up/in from the base placement
        // above (Y/Z) — additive on top of Height/OutwardOffsetFraction,
        // not a replacement for them.
        private const float TiltXDegrees = -25f;
        private static readonly Vector3 PositionAdjust = new(0f, 0.5f, -0.25f);

        // Roughly 1 in 3 eligible wall faces — enough for atmosphere
        // along a corridor without a torch on every single tile.
        private const uint Sparseness = 3;

        private static GameObject _mesh;
        private static Material _material;
        private static bool _loaded;

        /// coord: the plain-Rock wall tile being considered. outwardDir:
        /// unit cardinal step from the wall toward the floor tile that
        /// exposed it (see DungeonGrid's neighbor scan) — used both to
        /// pick which face to sit against and to seed the sparseness
        /// hash, so the same wall face always resolves the same way.
        /// Returns the torch instance, or null if this tile didn't roll
        /// one (caller decides what "null" means for its own bookkeeping).
        public static GameObject TryPlace(Transform tileRoot, Vector2Int coord, Vector2Int outwardDir, float cellSize)
        {
            EnsureLoaded();
            if (_mesh == null)
            {
                return null;
            }

            var hash = (uint)(coord.x * 92821 + coord.y * 68917 + outwardDir.x * 2081 + outwardDir.y * 3037 + 977);
            if (hash % Sparseness != 0)
            {
                return null;
            }

            var torch = Object.Instantiate(_mesh, tileRoot, false);
            torch.name = "Torch";
            torch.transform.localPosition = new Vector3(outwardDir.x, 0f, outwardDir.y) * (cellSize * OutwardOffsetFraction)
                + Vector3.up * Height
                + PositionAdjust;
            torch.transform.localRotation = Quaternion.Euler(TiltXDegrees, 0f, 0f);
            torch.transform.localScale = Vector3.one * Scale;

            var renderer = torch.GetComponentInChildren<Renderer>();
            if (renderer != null && _material != null)
            {
                renderer.sharedMaterial = _material;
            }

            FogObscurable.Attach(torch, FogObscurableKind.Structure);
            return torch;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            _mesh = Resources.Load<GameObject>("Dungeon/Props/torch_lit");
            _material = Resources.Load<Material>("Dungeon/Props/M_Torch");
        }
    }
}

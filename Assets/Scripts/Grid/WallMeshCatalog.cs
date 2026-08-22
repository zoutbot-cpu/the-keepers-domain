using UnityEngine;

namespace KeepersDomain.Grid
{
    /// The 6 modular wall prefabs WallAutotiler's shapes map to, plus a
    /// uniform scale/rotation fudge for whatever the source asset pack's
    /// authored footprint/facing turns out to be. Loaded at runtime via
    /// Resources.Load("Dungeon/WallMeshCatalog") — DungeonGrid is built
    /// entirely procedurally (see GameBootstrap), so there's no scene
    /// object to hand-wire these references onto instead.
    [CreateAssetMenu(fileName = "WallMeshCatalog", menuName = "Keepers Domain/Wall Mesh Catalog")]
    public class WallMeshCatalog : ScriptableObject
    {
        [SerializeField] private GameObject _isolated;
        [SerializeField] private GameObject _endCap;
        [SerializeField] private GameObject _straight;
        [SerializeField] private GameObject _corner;
        [SerializeField] private GameObject _tJunction;
        [SerializeField] private GameObject _cross;

        /// Uniform scale applied to every instantiated wall prefab so its
        /// footprint matches DungeonGrid's 1-unit cell size. KayKit's
        /// dungeon modules are authored on a 2-unit grid, so this defaults
        /// to 0.5 — confirmed empirically against the imported meshes'
        /// bounds (see WallSetupTool), tweak here if that changes.
        [SerializeField] private float _prefabScale = 0.5f;

        /// Added to every WallAutotiler-computed Y rotation. All 6 pieces
        /// share one facing convention in the source pack, so a single
        /// systematic offset (in 90-degree steps) is enough to correct the
        /// whole set if WallAutotiler's assumed canonical facing doesn't
        /// match how these particular meshes were modeled.
        [SerializeField] private float _rotationOffsetDegrees;

        public float PrefabScale => _prefabScale;
        public float RotationOffsetDegrees => _rotationOffsetDegrees;

        public GameObject GetPrefab(WallShape shape)
        {
            switch (shape)
            {
                case WallShape.Isolated: return _isolated;
                case WallShape.EndCap: return _endCap;
                case WallShape.Straight: return _straight;
                case WallShape.Corner: return _corner;
                case WallShape.TJunction: return _tJunction;
                default: return _cross;
            }
        }
    }
}

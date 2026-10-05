using UnityEngine;

namespace KeepersDomain.Grid
{
    /// Subdivides a water tile's flat quad into a small grid and animates
    /// vertex height with a cheap two-wave sine sum evaluated in world
    /// space, so ripples read as one continuous surface flowing across a
    /// whole pool rather than stopping at each tile's border. Normals are
    /// derived analytically from the height function's partial
    /// derivatives, so lighting stays correct without a per-frame
    /// RecalculateNormals call.
    ///
    /// Attaches alongside the MeshFilter/MeshRenderer that DungeonGrid's
    /// Tile_Water instance already carries on its own root — see
    /// water_tile.obj: a single 1x1 quad at local y=-0.03 with UV 0..1
    /// mapped 1:1 to local x/z + 0.5. This only swaps that MeshFilter's
    /// mesh for a subdivided one built to the same footprint/UVs and
    /// takes over animating it every frame; material/shader/tinting are
    /// untouched (DungeonGrid's ApplyTint only ever touches a
    /// MaterialPropertyBlock, never mesh data).
    public class WaterWaveMesh : MonoBehaviour
    {
        private const int Resolution = 5; // vertices per side (4x4 quads)
        private const float FlatY = -0.03f;

        private const float Amplitude = 0.035f;
        private const float FreqX = 2.3f;
        private const float FreqZ = 1.7f;
        private const float SpeedX = 0.9f;
        private const float SpeedZ = 0.7f;

        // Slow enough to be cheap across a whole pool's worth of tiles —
        // water ripples read fine well under full frame rate.
        private const float UpdateInterval = 1f / 15f;

        private static int[] _sharedTriangles;
        private static Vector2[] _sharedUvs;
        private static Vector3[] _sharedFlatLocalPositions;

        private Mesh _mesh;
        private Vector3[] _vertices;
        private Vector3[] _normals;
        private float _nextUpdateTime;
        private float _phase;

        public static void Attach(GameObject terrainMeshChild)
        {
            var meshFilter = terrainMeshChild.GetComponent<MeshFilter>();
            if (meshFilter == null)
            {
                return;
            }

            EnsureSharedTopology();

            var waves = terrainMeshChild.AddComponent<WaterWaveMesh>();
            // Desyncs tiles so pools don't ripple in visible lockstep.
            waves._phase = Random.Range(0f, 1000f);

            var mesh = new Mesh { name = "WaterWaveMesh" };
            mesh.MarkDynamic();
            mesh.vertices = _sharedFlatLocalPositions;
            mesh.uv = _sharedUvs;
            mesh.triangles = _sharedTriangles;
            mesh.RecalculateBounds();
            var bounds = mesh.bounds;
            bounds.Expand(new Vector3(0f, Amplitude * 2f, 0f));
            mesh.bounds = bounds;

            waves._mesh = mesh;
            waves._vertices = (Vector3[])_sharedFlatLocalPositions.Clone();
            waves._normals = new Vector3[_sharedFlatLocalPositions.Length];
            meshFilter.mesh = mesh;
        }

        private static void EnsureSharedTopology()
        {
            if (_sharedTriangles != null)
            {
                return;
            }

            var verts = new Vector3[Resolution * Resolution];
            var uvs = new Vector2[Resolution * Resolution];
            for (int zi = 0; zi < Resolution; zi++)
            {
                for (int xi = 0; xi < Resolution; xi++)
                {
                    var u = xi / (float)(Resolution - 1);
                    var v = zi / (float)(Resolution - 1);
                    var index = zi * Resolution + xi;
                    verts[index] = new Vector3(u - 0.5f, FlatY, v - 0.5f);
                    uvs[index] = new Vector2(u, v);
                }
            }

            var triangles = new int[(Resolution - 1) * (Resolution - 1) * 6];
            var t = 0;
            for (int zi = 0; zi < Resolution - 1; zi++)
            {
                for (int xi = 0; xi < Resolution - 1; xi++)
                {
                    var i0 = zi * Resolution + xi;
                    var i1 = i0 + 1;
                    var i2 = i0 + Resolution;
                    var i3 = i2 + 1;

                    triangles[t++] = i0;
                    triangles[t++] = i2;
                    triangles[t++] = i1;
                    triangles[t++] = i1;
                    triangles[t++] = i2;
                    triangles[t++] = i3;
                }
            }

            _sharedFlatLocalPositions = verts;
            _sharedUvs = uvs;
            _sharedTriangles = triangles;
        }

        private void Update()
        {
            if (Time.time < _nextUpdateTime)
            {
                return;
            }

            _nextUpdateTime = Time.time + UpdateInterval;

            var t = Time.time + _phase;
            for (int i = 0; i < _vertices.Length; i++)
            {
                var flat = _sharedFlatLocalPositions[i];
                var world = transform.TransformPoint(flat);

                var wx = world.x * FreqX + t * SpeedX;
                var wz = world.z * FreqZ - t * SpeedZ;
                var height = (Mathf.Sin(wx) + Mathf.Sin(wz)) * 0.5f * Amplitude;

                var dHdx = Mathf.Cos(wx) * FreqX * 0.5f * Amplitude;
                var dHdz = Mathf.Cos(wz) * FreqZ * 0.5f * Amplitude;

                _vertices[i] = new Vector3(flat.x, FlatY + height, flat.z);
                _normals[i] = new Vector3(-dHdx, 1f, -dHdz).normalized;
            }

            _mesh.SetVertices(_vertices);
            _mesh.SetNormals(_normals);
        }

        private void OnDestroy()
        {
            if (_mesh != null)
            {
                Destroy(_mesh);
            }
        }
    }
}

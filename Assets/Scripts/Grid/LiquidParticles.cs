using UnityEngine;

namespace KeepersDomain.Grid
{
    /// Rising-and-popping lava bubbles, built entirely at runtime (mesh +
    /// material generated in code, particle system configured in code)
    /// so it needs no Editor authoring pass — same "ship code, verify
    /// visually next session" approach the rest of this milestone used
    /// (see FogOfWar). Attached once per lava tile by
    /// DungeonGrid.RefreshVisual whenever that tile's terrain mesh child
    /// is (re)built, as a child of that mesh so it's destroyed
    /// automatically the moment the tile stops being lava.
    public static class LiquidParticles
    {
        private const float LifetimeMin = 2.8f;
        private const float LifetimeMax = 5f;
        private const float SizeMin = 0.05f;
        private const float SizeMax = 0.12f;
        private const float RiseSpeed = 0.035f;
        // Average gap between bubbles on a given tile is ~1/this, in
        // seconds — already random (Poisson-ish via rateOverTime), just
        // dialed down from the original 0.6 (~1.7s average) for a much
        // longer, still-random wait between activations.
        private const float EmissionRatePerTile = 0.1f;
        private const float FootprintFraction = 0.6f;

        private static readonly Color BubbleColor = new(1f, 0.4f, 0.08f, 1f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private static Mesh _bubbleMesh;
        private static Material _bubbleMaterial;

        /// terrainMeshChild is the already-instantiated Tile_Lava quad
        /// instance (localScale == Vector3.one * cellSize per its own
        /// instantiation code) — this parents under it for lifecycle,
        /// but cancels that inherited scale locally so every size/shape
        /// value below is authored in plain world units.
        public static void AttachLavaBubbles(Transform terrainMeshChild, float cellSize)
        {
            _bubbleMesh ??= CreateBubbleMesh();
            _bubbleMaterial ??= CreateBubbleMaterial();

            var host = new GameObject("LavaBubbles");
            host.transform.SetParent(terrainMeshChild, false);
            host.transform.localScale = Vector3.one / Mathf.Max(cellSize, 0.0001f);

            var ps = host.AddComponent<ParticleSystem>();
            // AddComponent already started it playing (playOnAwake
            // defaults true) — randomSeed can't be changed on a running
            // system, so stop it first and explicitly Play() once
            // everything below is configured.
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.useAutoRandomSeed = false;
            ps.randomSeed = (uint)Random.Range(1, int.MaxValue);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(LifetimeMin, LifetimeMax);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(SizeMin, SizeMax);
            main.startColor = BubbleColor;
            main.gravityModifier = -RiseSpeed;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 4;
            main.scalingMode = ParticleSystemScalingMode.Local;
            // Stagger each tile's very first bubble too, not just the
            // steady-state gaps — otherwise every lava tile bubbles in
            // lockstep the moment the level loads.
            main.startDelay = new ParticleSystem.MinMaxCurve(0f, 1f / EmissionRatePerTile);

            var emission = ps.emission;
            emission.rateOverTime = EmissionRatePerTile;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(FootprintFraction, 0.01f, FootprintFraction);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.03f;
            noise.frequency = 0.4f;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            var popCurve = new AnimationCurve(
                new Keyframe(0f, 0.4f),
                new Keyframe(0.7f, 1f),
                new Keyframe(1f, 0f));
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, popCurve);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = _bubbleMesh;
            renderer.material = _bubbleMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            ps.Play();
        }

        private static Mesh CreateBubbleMesh()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(temp);
            return mesh;
        }

        private static Material CreateBubbleMaterial()
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"))
            {
                name = "M_LavaBubble_Runtime"
            };
            mat.SetColor(BaseColorId, BubbleColor);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor(EmissionColorId, BubbleColor * 2.5f);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return mat;
        }
    }
}

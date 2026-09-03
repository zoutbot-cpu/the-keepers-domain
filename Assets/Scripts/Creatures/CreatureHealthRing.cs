using UnityEngine;
using KeepersDomain.Grid;

namespace KeepersDomain.Creatures
{
    /// A flat "donut" at a creature's feet that doubles as its ownership
    /// marker and its health bar: 8 equal segments (each worth MaxHP / 8),
    /// a dark-gray track always visible underneath, and an owner-colored
    /// fill on every segment the creature's current HP still covers
    /// (ceil(HP / MaxHP * 8) segments lit). Built from primitive cubes the
    /// same way DungeonGrid.BuildHolyGroundStar / LevelDesignerSession.
    /// BuildCreatureVisual build their decorations, rather than a custom
    /// mesh.
    ///
    /// Nothing damages a non-attacking creature yet (no combat), so today
    /// this reads as "full, in the owner's color" — it's the ownership
    /// indicator now and the health bar once combat exists.
    ///
    /// The ring GameObject is deliberately NOT parented to the creature's
    /// capsule (capsule scales vary per species, and a child would inherit
    /// that scale) — it's independent, and Update() follows the creature's
    /// X/Z at a fixed floor Y so it always lies flat on the ground.
    public class CreatureHealthRing : MonoBehaviour
    {
        private const int SegmentCount = 8;

        // Sized against the Imp's 0.33 capsule — a little wider than the
        // body so it reads as a ring the creature stands in.
        private const float Radius = 0.34f;
        private const float SegmentLength = 0.2f;
        private const float SegmentRadialThickness = 0.06f;
        private const float SegmentHeight = 0.035f;

        // The track sits slightly larger and slightly lower so a dark rim
        // shows around/under every fill block, lit or not.
        private const float TrackLengthPadding = 0.05f;
        private const float TrackRadialPadding = 0.035f;
        private const float TrackDropBelowFill = 0.012f;

        private const float HeightAboveFloor = 0.02f;

        private static readonly Color TrackColor = new Color(0.18f, 0.18f, 0.2f);

        private Transform _host;
        private DungeonGrid _grid;

        // HP fraction (0..1) and owner id, read fresh every frame. A host
        // creature/Throne wires these to its own Creature; a network ghost
        // (see CreatureNetView) wires them to replicated netvars, so the
        // ring renders the same either side of the wire without knowing
        // which.
        private System.Func<float> _fillFraction01;
        private System.Func<int> _ownerId;

        // 1 for a creature; larger for a bigger host (the Throne Room passes
        // ~4 so its ring circles the 3x3 platform). Scales the ring radius
        // and every segment/track dimension that grows with it.
        private float _radiusScale = 1f;

        // The Throne's ring is only meant to show when it's hurt — a
        // creature's is always on (it doubles as the ownership marker).
        private bool _hideWhenFull;

        private GameObject _container;
        private readonly GameObject[] _fillSegments = new GameObject[SegmentCount];
        private readonly Renderer[] _fillRenderers = new Renderer[SegmentCount];

        private int _litSegments = -1;
        private Color _fillColor = new Color(0f, 0f, 0f, 0f);

        /// Adds the ring to host and wires it to creature/grid — call once
        /// from the creature agent's Initialize (or ThroneRoom's).
        public static CreatureHealthRing Attach(GameObject host, Creature creature, DungeonGrid grid,
            float radiusScale = 1f, bool hideWhenFull = false)
        {
            return Attach(host,
                () => { var m = creature.Stats.MaxHP; return m > 0f ? Mathf.Clamp01(creature.Stats.HP / m) : 0f; },
                () => creature.OwnerId,
                grid, radiusScale, hideWhenFull);
        }

        /// Delegate form — for a renderer with no Creature of its own (a
        /// network ghost reading replicated HP/owner). fillFraction01 is
        /// current HP as a 0..1 fraction of max.
        public static CreatureHealthRing Attach(GameObject host, System.Func<float> fillFraction01,
            System.Func<int> ownerId, DungeonGrid grid, float radiusScale = 1f, bool hideWhenFull = false)
        {
            var ring = host.AddComponent<CreatureHealthRing>();
            ring._host = host.transform;
            ring._fillFraction01 = fillFraction01;
            ring._ownerId = ownerId;
            ring._grid = grid;
            ring._radiusScale = radiusScale;
            ring._hideWhenFull = hideWhenFull;
            ring.BuildContainer();
            return ring;
        }

        private void BuildContainer()
        {
            _container = new GameObject("HealthRing");
            // Parented to the grid (not the variably-scaled creature
            // capsule) purely to keep the hierarchy tidy and so a
            // full-scene teardown takes it with everything else — Update
            // drives its world position every frame regardless.
            _container.transform.SetParent(_grid.transform, false);

            for (int i = 0; i < SegmentCount; i++)
            {
                var angleDegrees = i * (360f / SegmentCount);
                var rotation = Quaternion.Euler(0f, angleDegrees, 0f);
                var offset = rotation * new Vector3(0f, 0f, Radius * _radiusScale);

                var track = GameObject.CreatePrimitive(PrimitiveType.Cube);
                track.name = $"Track_{i}";
                track.transform.SetParent(_container.transform, false);
                track.transform.localPosition = offset + new Vector3(0f, -TrackDropBelowFill, 0f);
                track.transform.localRotation = rotation;
                track.transform.localScale = new Vector3(
                    (SegmentLength + TrackLengthPadding) * _radiusScale,
                    SegmentHeight,
                    (SegmentRadialThickness + TrackRadialPadding) * _radiusScale);
                track.GetComponent<Renderer>().material.color = TrackColor;
                Destroy(track.GetComponent<Collider>());

                var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fill.name = $"Fill_{i}";
                fill.transform.SetParent(_container.transform, false);
                fill.transform.localPosition = offset;
                fill.transform.localRotation = rotation;
                fill.transform.localScale = new Vector3(
                    SegmentLength * _radiusScale, SegmentHeight, SegmentRadialThickness * _radiusScale);
                Destroy(fill.GetComponent<Collider>());

                _fillSegments[i] = fill;
                _fillRenderers[i] = fill.GetComponent<Renderer>();
            }

            SyncRing();
        }

        private void Update()
        {
            if (_container == null || _host == null || _grid == null || _fillFraction01 == null)
            {
                return;
            }

            var pos = _host.position;
            _container.transform.position = new Vector3(pos.x, _grid.FloorSurfaceY + HeightAboveFloor, pos.z);

            SyncRing();
        }

        /// Only touches the segment GameObjects/materials when the lit count
        /// or owner color actually changed — same "don't churn visuals every
        /// frame" convention the rest of the prototype follows.
        private void SyncRing()
        {
            var fraction = Mathf.Clamp01(_fillFraction01());

            if (_hideWhenFull)
            {
                var shouldShow = fraction < 0.999f;
                if (_container.activeSelf != shouldShow)
                {
                    _container.SetActive(shouldShow);
                }

                if (!shouldShow)
                {
                    return;
                }
            }

            var lit = Mathf.Clamp(Mathf.CeilToInt(fraction * SegmentCount), 0, SegmentCount);
            var ownerColor = _grid.GetOwnerColor(_ownerId());

            if (lit == _litSegments && ownerColor == _fillColor)
            {
                return;
            }

            _litSegments = lit;
            _fillColor = ownerColor;

            for (int i = 0; i < SegmentCount; i++)
            {
                var isLit = i < lit;
                _fillSegments[i].SetActive(isLit);
                if (isLit)
                {
                    _fillRenderers[i].material.color = ownerColor;
                }
            }
        }

        private void OnDestroy()
        {
            if (_container != null)
            {
                Destroy(_container);
            }
        }
    }
}

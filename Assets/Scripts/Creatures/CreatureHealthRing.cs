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
        private Creature _creature;

        private GameObject _container;
        private readonly GameObject[] _fillSegments = new GameObject[SegmentCount];
        private readonly Renderer[] _fillRenderers = new Renderer[SegmentCount];

        private int _litSegments = -1;
        private Color _fillColor = new Color(0f, 0f, 0f, 0f);

        /// Adds the ring to host and wires it to creature/grid — call once
        /// from the creature agent's Initialize.
        public static CreatureHealthRing Attach(GameObject host, Creature creature, DungeonGrid grid)
        {
            var ring = host.AddComponent<CreatureHealthRing>();
            ring._host = host.transform;
            ring._creature = creature;
            ring._grid = grid;
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
                var offset = rotation * new Vector3(0f, 0f, Radius);

                var track = GameObject.CreatePrimitive(PrimitiveType.Cube);
                track.name = $"Track_{i}";
                track.transform.SetParent(_container.transform, false);
                track.transform.localPosition = offset + new Vector3(0f, -TrackDropBelowFill, 0f);
                track.transform.localRotation = rotation;
                track.transform.localScale = new Vector3(
                    SegmentLength + TrackLengthPadding,
                    SegmentHeight,
                    SegmentRadialThickness + TrackRadialPadding);
                track.GetComponent<Renderer>().material.color = TrackColor;
                Destroy(track.GetComponent<Collider>());

                var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fill.name = $"Fill_{i}";
                fill.transform.SetParent(_container.transform, false);
                fill.transform.localPosition = offset;
                fill.transform.localRotation = rotation;
                fill.transform.localScale = new Vector3(SegmentLength, SegmentHeight, SegmentRadialThickness);
                Destroy(fill.GetComponent<Collider>());

                _fillSegments[i] = fill;
                _fillRenderers[i] = fill.GetComponent<Renderer>();
            }

            SyncToCreature();
        }

        private void Update()
        {
            if (_container == null || _host == null || _grid == null || _creature == null)
            {
                return;
            }

            var pos = _host.position;
            _container.transform.position = new Vector3(pos.x, _grid.FloorSurfaceY + HeightAboveFloor, pos.z);

            SyncToCreature();
        }

        /// Only touches the segment GameObjects/materials when the lit count
        /// or owner color actually changed — same "don't churn visuals every
        /// frame" convention the rest of the prototype follows.
        private void SyncToCreature()
        {
            var maxHp = _creature.Stats.MaxHP;
            var fraction = maxHp > 0f ? Mathf.Clamp01(_creature.Stats.HP / maxHp) : 0f;
            var lit = Mathf.Clamp(Mathf.CeilToInt(fraction * SegmentCount), 0, SegmentCount);
            var ownerColor = _grid.GetOwnerColor(_creature.OwnerId);

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

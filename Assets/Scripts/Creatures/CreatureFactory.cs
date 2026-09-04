using UnityEngine;
using KeepersDomain.LevelDesigner;

namespace KeepersDomain.Creatures
{
    /// The one place the placeholder-capsule look of each species lives —
    /// colour + capsule proportions, previously copy-pasted as
    /// [SerializeField]s across all six spawners. Used by the offline
    /// spawners (shaping a CreatePrimitive capsule) and by the networked
    /// client (shaping the CreatureNetView prefab instance), so a host and
    /// a client render an identical creature.
    public static class CreatureFactory
    {
        public struct Look
        {
            public Color Color;
            public float Radius;   // capsule X/Z localScale
            public float Height;   // capsule Y localScale, and the up-offset that grounds it
        }

        public static Look LookFor(EditorCreatureKind kind)
        {
            switch (kind)
            {
                case EditorCreatureKind.Imp:
                    return new Look { Color = new Color(0.8f, 0.2f, 0.2f), Radius = 0.33f, Height = 0.33f };
                case EditorCreatureKind.Gremlin:
                    return new Look { Color = new Color(0.3f, 0.75f, 0.65f), Radius = 0.22f, Height = 0.4f };
                case EditorCreatureKind.Warlock:
                    return new Look { Color = new Color(0.35f, 0.15f, 0.5f), Radius = 0.22f, Height = 0.4f };
                case EditorCreatureKind.MazeRattler:
                    return new Look { Color = new Color(0.45f, 0.3f, 0.15f), Radius = 0.22f, Height = 0.4f };
                case EditorCreatureKind.BeanCounter:
                    return new Look { Color = new Color(0.68f, 0.72f, 0.3f), Radius = 0.22f, Height = 0.4f };
                case EditorCreatureKind.Elf:
                    return new Look { Color = new Color(0.75f, 0.85f, 0.68f), Radius = 0.18f, Height = 0.32f };
                default:
                    return new Look { Color = Color.white, Radius = 0.22f, Height = 0.4f };
            }
        }

        /// Species colour + capsule proportions only (no position) — used
        /// on both sides of the wire: the host via ShapeBody, the client on
        /// its CreatureNetView ghost (whose position is driven by
        /// NetworkTransform, not us). Strips any collider — creatures don't
        /// collide.
        public static void ApplyLook(GameObject body, EditorCreatureKind kind)
        {
            var look = LookFor(kind);
            body.transform.localScale = new Vector3(look.Radius, look.Height, look.Radius);

            var renderer = body.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = look.Color;
            }

            var collider = body.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }
        }

        /// ApplyLook plus grounding on groundPos — the default capsule is 2
        /// units tall at scale 1, so half its height is `Height`, exactly
        /// the up-offset that sits its feet on the tile. The host path.
        public static void ShapeBody(GameObject body, EditorCreatureKind kind, Vector3 groundPos)
        {
            ApplyLook(body, kind);
            body.transform.position = groundPos + Vector3.up * LookFor(kind).Height;
        }

        /// Half the capsule's height — the up-offset a body sits at above
        /// its tile. The client uses it to place the health ring / read the
        /// ground position back off a ghost.
        public static float GroundOffset(EditorCreatureKind kind) => LookFor(kind).Height;

        /// Offline path — a fresh capsule primitive, shaped and named.
        public static GameObject CreateOfflineBody(EditorCreatureKind kind, Vector3 groundPos)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = kind.ToString();
            ShapeBody(body, kind, groundPos);
            return body;
        }
    }
}

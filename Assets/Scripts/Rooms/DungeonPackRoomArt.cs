using UnityEngine;

namespace KeepersDomain.Rooms
{
    /// Shared helpers for room managers that decorate their floor/structure
    /// tiles with real dungeon_pack meshes/textures instead of primitive-
    /// colored shapes (Lair, Training Room, Library, Tavern, Slime
    /// Hatchery) — pulled out of what used to be near-identical private
    /// copies in each of those managers.
    public static class DungeonPackRoomArt
    {
        /// A real URP/Lit material with texturePath's texture baked in as
        /// its _BaseMap — built explicitly via Shader.Find rather than
        /// relying on whatever material GameObject.CreatePrimitive happens
        /// to hand back (that implicit default isn't guaranteed URP-shaded
        /// and rendered as Unity's pink/error material in an earlier pass
        /// of this same code). Same approach every DungeonPack*Setup
        /// Editor tool already uses to build its own materials. Null if
        /// the texture itself failed to load.
        public static Material BuildMaterial(string texturePath)
        {
            var texture = Resources.Load<Texture2D>(texturePath);
            if (texture == null)
            {
                return null;
            }

            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetTexture("_BaseMap", texture);
            return material;
        }

        /// The uniform scale factor that makes instance's own renderer
        /// bounds (X/Z footprint, the larger of the two) match
        /// targetFootprint — the "scale a real mesh prop up/down to fit
        /// the tile(s) it's meant to occupy" math every dungeon_pack prop
        /// placement (Lair's nest bed, Training Room's dummy, Tavern's
        /// shrine machine, Slime Hatchery's coop) already needs. Returns 1
        /// (no scaling) if instance has no renderers, or its own footprint
        /// is too close to zero to divide by safely.
        public static float ComputeUniformScaleToFootprint(GameObject instance, float targetFootprint)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return 1f;
            }

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            var footprint = Mathf.Max(bounds.size.x, bounds.size.z);
            return footprint > 0.01f ? targetFootprint / footprint : 1f;
        }
    }
}

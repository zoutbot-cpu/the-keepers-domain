using UnityEngine;

namespace KeepersDomain.Grid
{
    /// A primitive from GameObject.CreatePrimitive gets the built-in
    /// Standard "Default-Material", which a URP player build can't render —
    /// it shows magenta. Everything in this prototype builds its decoration
    /// from primitives, so this is the one place that swaps in a real URP
    /// material.
    ///
    /// The template is a .mat ASSET (Resources/Shared/M_Prim, created by
    /// Tools > Net > Setup Netcode Prefabs) — NOT
    /// `new Material(Shader.Find("Universal Render Pipeline/Lit"))`, because
    /// that leaves the shader's variants to URP's build-time stripping and
    /// still comes out magenta. A material asset drags its variants into
    /// the build.
    public static class Prims
    {
        private static Material _template;

        public static Material Template
        {
            get
            {
                if (_template == null)
                {
                    _template = Resources.Load<Material>("Shared/M_Prim");
                    if (_template == null)
                    {
                        // Editor before setup has run / worst case.
                        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                        _template = new Material(shader);
                    }
                }

                return _template;
            }
        }

        /// A fresh instance of the URP template — for code that owns and
        /// mutates its own Material (see DungeonGrid's floor/outline mats).
        public static Material NewMaterial() => new Material(Template);

        /// Replaces the renderer's material with a tinted URP instance.
        /// Drop-in for the old `x.GetComponent<Renderer>().material.color = c`.
        public static void Tint(GameObject go, Color color) =>
            Tint(go != null ? go.GetComponent<Renderer>() : null, color);

        public static void Tint(Renderer renderer, Color color)
        {
            if (renderer != null)
            {
                renderer.material = new Material(Template) { color = color };
            }
        }

        private static Shader _unlitShader;

        /// A URP Unlit material, alpha-blended, showing texture — for a flat
        /// textured decal (see DungeonGrid's Pickaxe/Shield wall icons)
        /// rather than a tinted opaque primitive. Built with Shader.Find
        /// rather than a saved .mat asset like Template: unlike Prims.Tint's
        /// prefab-baking use (NetPrefabSetup), this is only ever
        /// instantiated at runtime during an actual session, and URP/Unlit
        /// is already in Always Included Shaders (see ProjectSettings/
        /// GraphicsSettings.asset — added for DungeonGrid's own selection
        /// outline material), so its variants already ship in the build.
        public static Material NewUnlitTransparentMaterial(Texture2D texture)
        {
            if (_unlitShader == null)
            {
                _unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
            }

            var material = new Material(_unlitShader) { mainTexture = texture };

            // URP's Unlit shader defaults to opaque -- flip it to
            // alpha-blended transparent so the icon PNG's own transparent
            // background actually shows through instead of a solid square.
            material.SetFloat("_Surface", 1f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            // Double-sided — a flat decal like the wall-face icons doesn't
            // have a "wrong" side to view it from once the camera orbits.
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            return material;
        }
    }
}

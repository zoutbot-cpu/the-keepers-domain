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
    }
}

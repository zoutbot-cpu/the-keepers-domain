using System.Collections.Generic;
using UnityEngine;

namespace KeepersDomain.Grid
{
    public enum FogObscurableKind
    {
        /// A moving creature body — shown only where a minion has live vision
        /// right now (FogView.Visible).
        Creature,

        /// A fixed landmark prop (Throne, Portal) — shown once its tile has
        /// been seen at all (Explored or Visible), so it stays on the "map"
        /// even without current vision.
        Structure
    }

    /// Marks a non-grid object that must be hidden while the tile under it
    /// has no vision — FogOfWar toggles every one of these each recompute
    /// (see FogOfWar.RefreshObscurables). Completely inert when no FogOfWar
    /// exists (the networked client, the Level Designer): nothing ever
    /// iterates the list, so the renderers are simply left on.
    ///
    /// The grid's own tile visuals and its per-tile decoration children are
    /// handled directly inside DungeonGrid.RefreshVisual instead — this is
    /// only for objects that live outside the grid hierarchy.
    public class FogObscurable : MonoBehaviour
    {
        public static readonly List<FogObscurable> All = new List<FogObscurable>();

        public FogObscurableKind Kind { get; private set; }

        private Renderer[] _renderers;
        private bool _shown = true;

        /// Adds the component to go and caches its child renderers. Call
        /// after go's visual children have been built.
        public static FogObscurable Attach(GameObject go, FogObscurableKind kind)
        {
            var existing = go.GetComponent<FogObscurable>();
            if (existing != null)
            {
                return existing;
            }

            var obscurable = go.AddComponent<FogObscurable>();
            obscurable.Kind = kind;
            obscurable.Refresh();
            return obscurable;
        }

        /// Re-caches child renderers — for an object that builds more visual
        /// children after Attach ran.
        public void Refresh()
        {
            _renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
        }

        public void SetShown(bool shown)
        {
            if (_shown == shown || _renderers == null)
            {
                return;
            }

            _shown = shown;
            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] != null)
                {
                    _renderers[i].enabled = shown;
                }
            }
        }

        private void OnEnable()
        {
            if (!All.Contains(this))
            {
                All.Add(this);
            }
        }

        private void OnDisable()
        {
            All.Remove(this);
        }
    }
}

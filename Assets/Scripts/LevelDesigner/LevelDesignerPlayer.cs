using UnityEngine;

namespace KeepersDomain.LevelDesigner
{
    /// One dungeon-owning player slot configured in the level designer's
    /// Player Settings menu — authored level data (see
    /// LevelDesignerSession), not a live gameplay entity, since no
    /// AI/economy system runs in the editor to back one yet.
    public class LevelDesignerPlayer
    {
        public bool IsAI;
        public int ColorIndex;
        public int StartingGold;
        public int StartingMana;

        public Color Color => LevelDesignerColors.Palette[ColorIndex];
    }

    /// 8 basic player colors, picked so no two are easily confused for
    /// each other and none is black. Two players sharing the same
    /// ColorIndex means they're a team sharing one dungeon — not
    /// implemented yet (no shared-dungeon/team logic exists anywhere in
    /// this prototype), just recorded here as level data for that to be
    /// built on top of later.
    public static class LevelDesignerColors
    {
        public static readonly Color[] Palette =
        {
            new Color(0.85f, 0.15f, 0.15f), // Red
            new Color(0.15f, 0.4f, 0.9f),   // Blue
            new Color(0.95f, 0.85f, 0.1f),  // Yellow
            new Color(0.15f, 0.75f, 0.25f), // Green
            new Color(0.95f, 0.55f, 0.1f),  // Orange
            new Color(0.6f, 0.2f, 0.75f),   // Purple
            new Color(0.15f, 0.8f, 0.8f),   // Cyan
            new Color(0.95f, 0.4f, 0.7f)    // Pink
        };

        public static readonly string[] Names =
        {
            "Red", "Blue", "Yellow", "Green", "Orange", "Purple", "Cyan", "Pink"
        };
    }
}

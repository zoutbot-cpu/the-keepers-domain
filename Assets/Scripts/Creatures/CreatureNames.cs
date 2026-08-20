using UnityEngine;

namespace KeepersDomain.Creatures
{
    /// One 50-name pool per creature race, picked from at spawn time (see
    /// each agent's Awake) and kept for the creature's whole lifetime —
    /// purely flavor, no gameplay effect. Imp names lean short and
    /// goblin-ish, Gremlin names lean short and sneaky/feral, Warlock names
    /// lean long and mystical — except one, per request.
    public static class CreatureNames
    {
        public static readonly string[] ImpNames =
        {
            "Snig", "Grubb", "Fizzle", "Puddle", "Snot", "Grimble", "Wick", "Nab", "Squib", "Cinder",
            "Grit", "Pockle", "Blister", "Snivel", "Muck", "Fidget", "Gibber", "Scab", "Twitch", "Cog",
            "Sputter", "Grot", "Nibble", "Sooty", "Pinch", "Wretch", "Skab", "Bramble", "Frizzle", "Gnash",
            "Warble", "Scuttle", "Tallow", "Rink", "Cackle", "Grovel", "Bung", "Crook", "Wexle", "Fumble",
            "Prickle", "Squint", "Gargle", "Nub", "Slag", "Wisp", "Krik", "Dross", "Yammer", "Splinter"
        };

        public static readonly string[] GremlinNames =
        {
            "Skarn", "Vex", "Rattle", "Nix", "Grix", "Skitter", "Fang", "Snap", "Thistle", "Grime",
            "Vole", "Skulk", "Rasp", "Quill", "Snare", "Grackle", "Blight", "Wisk", "Talon", "Scrag",
            "Nettle", "Brix", "Sneer", "Gully", "Ferret", "Prowl", "Snitch", "Marrow", "Hollow", "Croak",
            "Weevil", "Skree", "Lurch", "Bristle", "Grumble", "Shard", "Vermin", "Snick", "Gnaw", "Prattle",
            "Slink", "Cur", "Yowl", "Thorne", "Mange", "Snarl", "Grizzle", "Rook", "Vixen", "Scowl"
        };

        // "Tim" is deliberately in here, mixed in among the grandiose ones.
        public static readonly string[] WarlockNames =
        {
            "Malachar", "Vexlorn", "Sable", "Nocturne", "Ravenscar", "Mordane", "Thessaly", "Grimwald", "Obsidian", "Ashgrave",
            "Nyx", "Vesper", "Caliban", "Morrigan", "Zephyrian", "Umbros", "Malvorn", "Ebonhart", "Thistlewick", "Grimshade",
            "Wraithmoor", "Sepulcher", "Duskarion", "Ferrowyn", "Blackmere", "Ashenveil", "Corvaine", "Ninhursag", "Skarath", "Vellum",
            "Thornwick", "Malastrix", "Grimhollow", "Nightshade", "Ravensworth", "Cindravel", "Marrowgale", "Hexley", "Sorrowmourn", "Blightwell",
            "Ashkarion", "Voidmere", "Grimalkin", "Nocturnis", "Tim", "Wyrmsbane", "Duskweaver", "Malachite", "Thornebrook", "Ravencroft"
        };

        public static string GetRandom(string[] pool)
        {
            return pool[Random.Range(0, pool.Length)];
        }
    }
}

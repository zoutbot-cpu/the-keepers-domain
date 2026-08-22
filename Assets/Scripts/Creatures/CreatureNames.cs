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

        public static readonly string[] MazeRattlerNames =
        {
            "Scritch", "Gnaw", "Rustle", "Whisker", "Burrow", "Skitters", "Chitter", "Squeak", "Ratchet", "Gristle",
            "Tunnel", "Nibbler", "Scrabble", "Rindle", "Wick", "Twitchtail", "Molar", "Scrape", "Rot", "Fester",
            "Pellet", "Skulker", "Grubber", "Rasper", "Snickle", "Warren", "Musk", "Clatter", "Grmisk", "Vermeel",
            "Dank", "Scuttler", "Rindtooth", "Chew", "Mange", "Squirm", "Ratling", "Snuffle", "Gristly", "Cobweb",
            "Scree", "Nether", "Pockmark", "Ravel", "Sinew", "Gnasher", "Wretchling", "Coil", "Skab", "Ferric"
        };

        // Preachy, bureaucratic, vegetable-pun names for the Bean Counter —
        // half zealot-sermonizer, half heartless clipboard-bureaucrat, per
        // the brief's own "torment with lectures about veganism" flavor.
        public static readonly string[] BeanCounterNames =
        {
            "Kale", "Bramwell", "Sprout", "Gourd", "Lentil", "Chickpea", "Radish", "Fennel", "Pious", "Marrow",
            "Turnip", "Zealous", "Gristlebane", "Cassia", "Fig", "Wormwood", "Sanctimony", "Bramble", "Endive", "Parsnip",
            "Legume", "Ledger", "Tofu", "Sable", "Bindweed", "Quorn", "Auditwick", "Chard", "Cress", "Puritan",
            "Vetch", "Tally", "Bran", "Absolvo", "Rutabaga", "Preachum", "Millet", "Grievance", "Sorrel", "Kohl",
            "Docket", "Penance", "Yeoman", "Sundry", "Verdigris", "Bushel", "Thistlewick", "Pulse", "Compost", "Amaranth"
        };

        // Short, whimsical names for the Elf — a "weak and worthless"
        // transformation outcome, not a proud recruited race, so the tone
        // leans deflated/silly rather than grand.
        public static readonly string[] ElfNames =
        {
            "Pip", "Wisty", "Fen", "Dandle", "Rue", "Twig", "Marl", "Sable", "Pim", "Fenwick",
            "Lark", "Moth", "Dew", "Bracken", "Wren", "Nib", "Sorrel", "Hollis", "Vell", "Tansy",
            "Birch", "Elowen", "Fable", "Ash", "Marigold", "Peaseblossom", "Quill", "Reed", "Wisp", "Thistle",
            "Wick", "Yarrow", "Bellis", "Clove", "Dill", "Elder", "Flax", "Gorse", "Heath", "Ivy",
            "Juniper", "Larkin", "Meadow", "Nettle", "Oaken", "Petal", "Quince", "Rowan", "Sedge", "Timothy"
        };

        public static string GetRandom(string[] pool)
        {
            return pool[Random.Range(0, pool.Length)];
        }
    }
}

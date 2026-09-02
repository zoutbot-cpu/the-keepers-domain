using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KeepersDomain.EditorTools
{
    /// One-time setup for the bespoke dungeon_pack's decorative props —
    /// landmark structures (Assets/Art/DungeonPack/Props/...) as well as
    /// per-room decoration meshes that live alongside their own room's
    /// other art instead (Assets/Art/DungeonPack/Lair/NestBed,
    /// .../TrainingRoom/TrainingDummy, .../Library/BookcaseModule,
    /// .../Tavern/BaconBeaconMachine, .../Tavern/InnBar,
    /// .../SlimeHatchery/ChickenCoop, .../Jail/WallInside,
    /// .../Jail/FenceHalf, .../Jail/Gate, .../Jail/StairsWood,
    /// .../Bridge/EdgePiece, .../Bridge/MiddlePiece,
    /// .../Bridge/Corner, .../Bridge/TJunction, .../Bridge/FourWay,
    /// .../Treasury/GoldLevel1-5). Almost all
    /// of these ship as flat per-slot Kd colors with no diffuse textures
    /// at all (checked directly against each .mtl), so this builds one
    /// solid-color URP material per slot by default instead of wiring up
    /// a texture — DiffuseName opts a slot into a real textured material
    /// instead (same _BaseMap-only build DungeonPackWallSetup uses for
    /// its own wall blocks), needed for Jail's retaining wall, the one
    /// mesh in this whole set that ships a real diffuse map. Wraps each
    /// source mesh into a prefab and drops it under Resources so the
    /// owning script can load it with no scene wiring — see ThroneRoom,
    /// LairManager, TrainingRoomManager, LibraryManager, TavernManager,
    /// SlimeHatcheryManager, JailManager, TreasuryManager, BridgeManager.
    public static class DungeonPackPropSetup
    {
        private class MaterialSpec
        {
            public string SourceMaterialName; // matches Unity's auto-named material for that .mtl slot (materialName: 0)
            public Color Color;

            // Null for every flat-colored slot (the overwhelming majority
            // of this set) — set only for a slot whose .mtl carries a real
            // map_Kd (just Jail's wall_inside today). When set, Color is
            // ignored and the slot's material gets this texture as its
            // _BaseMap instead of a flat _BaseColor, loaded from the same
            // SourceDir the mesh itself lives in.
            public string DiffuseName;
        }

        private class PropVariant
        {
            public string SourceDir;
            public string ObjName;
            public string PrefabPath;
            public string MaterialFolder;
            public MaterialSpec[] Materials;

            // Every dungeon_pack .obj so far ships with 0 "vn" lines and
            // needs normals recalculated on import (see FixNormalImport) —
            // but Portal.obj (hand-authored separately, its .mtl header
            // says "Blender 5.2.0 LTS", unlike the rest of the pack) does
            // carry real authored normals, so forcing Calculate would
            // discard them for no reason. Default false keeps every
            // existing entry's behavior unchanged.
            public bool HasAuthoredNormals;
        }

        // Kd values copied directly from treasury_gold_1.mtl — byte-for-
        // byte identical across all 5 gold_level_N.mtl files (a shared
        // jewel/gold/sack/chest palette: purple/blue/green/red gemstones,
        // two gold shades, dark wood/cloth/metal), so one shared spec set
        // covers every TreasuryManager gold-pile tier instead of
        // repeating it 5 times.
        private static readonly MaterialSpec[] TreasuryGoldMaterials =
        {
            new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.54901961f, 0.23529412f, 0.66666667f) },
            new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.13725490f, 0.31372549f, 0.74509804f) },
            new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.11764706f, 0.58823529f, 0.35294118f) },
            new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.74509804f, 0.11764706f, 0.15686275f) },
            new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.96078431f, 0.82352941f, 0.43137255f) },
            new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.87058824f, 0.71372549f, 0.27450980f) },
            new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.24313725f, 0.15686275f, 0.08627451f) },
            new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(0.27450980f, 0.27450980f, 0.29803922f) },
            new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.37647059f, 0.24313725f, 0.13333333f) },
        };

        private static readonly PropVariant[] Variants =
        {
            new PropVariant
            {
                // Kd values copied directly from throne_centerpiece.mtl —
                // dark stone body + red/gold accent colors, no texture.
                SourceDir = "Assets/Art/DungeonPack/Props/Throne",
                ObjName = "throne_centerpiece",
                PrefabPath = "Assets/Resources/Dungeon/Prop_Throne.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Props/Throne/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.22745098f, 0.22745098f, 0.25882353f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.15686275f, 0.15686275f, 0.18039216f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.72549020f, 0.10196078f, 0.12549020f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.88627451f, 0.73725490f, 0.37647059f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.50196078f, 0.06274510f, 0.07843137f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.76862745f, 0.61176471f, 0.23529412f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.86274510f, 0.27450980f, 0.07843137f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(1.00000000f, 0.82352941f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.28235294f, 0.28235294f, 0.32156863f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(0.03921569f, 0.03921569f, 0.03921569f) },
                },
            },
            new PropVariant
            {
                // The user's own new asset ("Portal.blend") replacing
                // portal_stairway — same 11-color palette (checked byte-
                // for-byte against portal_stairway.mtl, just reordered),
                // but ships real authored normals unlike the rest of the
                // pack (see HasAuthoredNormals). Re-exported once already
                // to fix a version that floated above the ground (its own
                // Y bounds now start near 0, pivot-at-base like the rest
                // of the pack) — re-copy from Downloads/dungeon_pack/
                // props/Portal.obj if it needs updating again.
                SourceDir = "Assets/Art/DungeonPack/Props/Portal",
                ObjName = "Portal",
                PrefabPath = "Assets/Resources/Dungeon/Prop_Portal.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Props/Portal/Materials",
                HasAuthoredNormals = true,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.227451f, 0.227451f, 0.258824f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.156863f, 0.156863f, 0.180392f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.725490f, 0.101961f, 0.125490f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.886275f, 0.737255f, 0.376471f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.501961f, 0.062745f, 0.078431f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.768627f, 0.611765f, 0.235294f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.862745f, 0.274510f, 0.078431f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(1.000000f, 0.823529f, 0.352941f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.588235f, 0.235294f, 0.823529f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(0.054902f, 0.031373f, 0.086275f) },
                    new MaterialSpec { SourceMaterialName = "material_10", Color = new Color(0.705882f, 0.352941f, 0.901961f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from nest_bed.mtl — a wood-
                // frame/tan bed with a dark red blanket, no texture. Placed
                // by LairManager on a claimed Lair tile, on top of the
                // room's own carpet floor (see CarpetTiles) — see
                // BuildClaimedVisual.
                SourceDir = "Assets/Art/DungeonPack/Lair/NestBed",
                ObjName = "nest_bed",
                PrefabPath = "Assets/Resources/Dungeon/Prop_NestBed.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Lair/NestBed/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.58823529f, 0.43921569f, 0.21176471f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.42352941f, 0.30588235f, 0.13333333f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.54901961f, 0.13333333f, 0.16470588f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.40784314f, 0.08627451f, 0.11764706f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from training_dummy.mtl — a
                // wooden post/crossbar frame with a pale straw torso and a
                // red target mark, no texture. Placed by TrainingRoomManager
                // at each computed dummy position — see BuildDummyVisual.
                SourceDir = "Assets/Art/DungeonPack/TrainingRoom/TrainingDummy",
                ObjName = "training_dummy",
                PrefabPath = "Assets/Resources/Dungeon/Prop_TrainingDummy.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/TrainingRoom/TrainingDummy/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.25882353f, 0.16470588f, 0.09411765f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.42352941f, 0.27450980f, 0.14901961f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.69019608f, 0.58039216f, 0.37647059f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.54901961f, 0.44705882f, 0.27450980f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.29019608f, 0.19607843f, 0.10196078f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.87058824f, 0.82352941f, 0.73725490f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.65882353f, 0.11764706f, 0.10980392f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from bookcase_module.mtl — a
                // dark-wood shelf frame packed with many individually
                // colored book spines (17 slots), no texture. A single
                // "module" (X/Z footprint well under one tile, see
                // LibraryManager's own BuildBookcaseModule note) meant to be
                // placed once per bookcase-row tile rather than stretched
                // across the row, unlike the old primitive box it replaces.
                SourceDir = "Assets/Art/DungeonPack/Library/BookcaseModule",
                ObjName = "bookcase_module",
                PrefabPath = "Assets/Resources/Dungeon/Prop_BookcaseModule.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Library/BookcaseModule/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.21176471f, 0.13333333f, 0.07843137f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.17254902f, 0.10980392f, 0.06274510f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.43921569f, 0.30588235f, 0.18039216f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.33725490f, 0.21960784f, 0.12549020f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.58823529f, 0.47058824f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.87058824f, 0.82352941f, 0.70588235f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.27450980f, 0.39215686f, 0.19607843f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(0.23529412f, 0.47058824f, 0.27450980f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.15686275f, 0.35294118f, 0.50980392f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(0.17647059f, 0.17647059f, 0.21568627f) },
                    new MaterialSpec { SourceMaterialName = "material_10", Color = new Color(0.47058824f, 0.15686275f, 0.31372549f) },
                    new MaterialSpec { SourceMaterialName = "material_11", Color = new Color(0.54901961f, 0.15686275f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_12", Color = new Color(0.35294118f, 0.23529412f, 0.50980392f) },
                    new MaterialSpec { SourceMaterialName = "material_13", Color = new Color(0.19607843f, 0.27450980f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_14", Color = new Color(0.66666667f, 0.35294118f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_15", Color = new Color(0.50980392f, 0.27450980f, 0.19607843f) },
                    new MaterialSpec { SourceMaterialName = "material_16", Color = new Color(0.62745098f, 0.58823529f, 0.35294118f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from bacon_beacon.mtl — a
                // grey/brown machine body with bright pipe/gauge accents
                // (green, orange, gold), no texture. Near-square footprint
                // (~1.93 x 1.91 unscaled) matching TavernManager's 2x2
                // shrine slot almost exactly — replaces the old primitive
                // dais+tubes there, see BuildShrineVisual.
                SourceDir = "Assets/Art/DungeonPack/Tavern/BaconBeaconMachine",
                ObjName = "bacon_beacon",
                PrefabPath = "Assets/Resources/Dungeon/Prop_BaconBeaconMachine.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Tavern/BaconBeaconMachine/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.22745098f, 0.22745098f, 0.25098039f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.58823529f, 0.36078431f, 0.20392157f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.37647059f, 0.38431373f, 0.41568627f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.58823529f, 0.82352941f, 0.74509804f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.35294118f, 0.74509804f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.54901961f, 0.55686275f, 0.58823529f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.50196078f, 0.23529412f, 0.17254902f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(0.36078431f, 0.15686275f, 0.11764706f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.90196078f, 0.43137255f, 0.11764706f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(1.00000000f, 0.82352941f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_10", Color = new Color(0.76862745f, 0.35294118f, 0.35294118f) },
                    new MaterialSpec { SourceMaterialName = "material_11", Color = new Color(0.90980392f, 0.82352941f, 0.74509804f) },
                    new MaterialSpec { SourceMaterialName = "material_12", Color = new Color(0.43137255f, 0.29019608f, 0.15686275f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from inn_bar.mtl — mostly
                // wood-tone (browns/tans) with a couple of grey/stone
                // accents, no texture. A long, thin bar-counter shape
                // (~2.69 x 0.93 unscaled — nothing like the near-square
                // shrine slot above), placed once per Tavern room along its
                // south edge rather than tied to any existing tile slot —
                // see TavernManager.BuildInnBar. Purely decorative, no
                // gameplay effect.
                SourceDir = "Assets/Art/DungeonPack/Tavern/InnBar",
                ObjName = "inn_bar",
                PrefabPath = "Assets/Resources/Dungeon/Prop_InnBar.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Tavern/InnBar/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.43921569f, 0.29019608f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.65882353f, 0.47843137f, 0.27450980f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.25882353f, 0.16470588f, 0.09411765f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.58823529f, 0.47058824f, 0.23529412f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.58823529f, 0.41568627f, 0.23529412f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.27450980f, 0.50980392f, 0.31372549f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.87058824f, 0.81568627f, 0.69019608f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(0.43137255f, 0.29019608f, 0.13333333f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.66666667f, 0.76470588f, 0.74509804f) },
                    new MaterialSpec { SourceMaterialName = "material_9", Color = new Color(0.47058824f, 0.30588235f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_10", Color = new Color(0.27450980f, 0.27450980f, 0.29019608f) },
                    new MaterialSpec { SourceMaterialName = "material_11", Color = new Color(0.43137255f, 0.42352941f, 0.43921569f) },
                    new MaterialSpec { SourceMaterialName = "material_12", Color = new Color(0.92156863f, 0.88235294f, 0.78431373f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from chicken_coop.mtl — a
                // wood-plank coop with a dark roof, no texture. Roughly a
                // single-tile footprint already (~1.08 x 1.06 unscaled),
                // matching SlimeHatcheryManager's single-tile structure
                // slot — replaces the old primitive box+roof there, see
                // BuildCoopVisual. Re-exported once already (a user
                // correction in Blender, re-copied from Downloads/
                // dungeon_pack/slime_hatchery/chicken_coop/) — that
                // re-export carries real authored normals unlike the rest
                // of the pack (see HasAuthoredNormals, same "Blender
                // 5.2.0 LTS" .mtl header Portal.obj has), so re-copy with
                // this flag intact if it's updated again.
                SourceDir = "Assets/Art/DungeonPack/SlimeHatchery/ChickenCoop",
                ObjName = "chicken_coop",
                PrefabPath = "Assets/Resources/Dungeon/Prop_ChickenCoop.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/SlimeHatchery/ChickenCoop/Materials",
                HasAuthoredNormals = true,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.28235294f, 0.18039216f, 0.10196078f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.46274510f, 0.31372549f, 0.17254902f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.09411765f, 0.06274510f, 0.03921569f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.23529412f, 0.24313725f, 0.22745098f) },
                    new MaterialSpec { SourceMaterialName = "material_4", Color = new Color(0.15686275f, 0.16470588f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_5", Color = new Color(0.37647059f, 0.12549020f, 0.10980392f) },
                    new MaterialSpec { SourceMaterialName = "material_6", Color = new Color(0.50196078f, 0.18039216f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_7", Color = new Color(0.58823529f, 0.42352941f, 0.24313725f) },
                    new MaterialSpec { SourceMaterialName = "material_8", Color = new Color(0.77647059f, 0.65882353f, 0.36078431f) },
                },
            },
            new PropVariant
            {
                // Jail's sunken-pit retaining wall (1w x 2h, pivot at the
                // TOP so Y=0 sits at ground level and the mesh extends
                // down 2 units to the pit floor — matches JailManager's
                // own RimWallDepth exactly, no scale correction needed).
                // The pack's one textured prop (see MaterialSpec.
                // DiffuseName) — a single grimy stone slot, no flat Kd.
                // Replaces BuildRimWallVisual's plain dark box, see
                // JailManager.
                SourceDir = "Assets/Art/DungeonPack/Jail/WallInside",
                ObjName = "jail_wall_inside",
                PrefabPath = "Assets/Resources/Dungeon/Prop_JailWallInside.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Jail/WallInside/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", DiffuseName = "jail_wall_inside_diffuse" },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from jail_fence_half.mtl —
                // three wood-brown shades, no texture. A perimeter rail
                // (~0.95w x 1h, pivot at the bottom/ground level), placed
                // along every pit rim edge except the gate one — replaces
                // BuildFenceRailVisual's plain gray box, see JailManager.
                SourceDir = "Assets/Art/DungeonPack/Jail/FenceHalf",
                ObjName = "jail_fence_half",
                PrefabPath = "Assets/Resources/Dungeon/Prop_JailFenceHalf.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Jail/FenceHalf/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.22745098f, 0.14901961f, 0.08627451f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.36862745f, 0.24313725f, 0.13333333f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.47843137f, 0.32941176f, 0.18823529f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from jail_gate.mtl — dark
                // iron-gray bars plus one wood-brown frame accent, no
                // texture. The barred entrance topper (1w x 2h, pivot at
                // the bottom/ground level) — replaces BuildGatePostsVisual's
                // two-post fallback, see JailManager.
                SourceDir = "Assets/Art/DungeonPack/Jail/Gate",
                ObjName = "jail_gate",
                PrefabPath = "Assets/Resources/Dungeon/Prop_JailGate.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Jail/Gate/Materials",
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.14117647f, 0.14117647f, 0.15686275f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.25882353f, 0.25882353f, 0.28235294f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.38431373f, 0.38431373f, 0.40784314f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.43137255f, 0.25882353f, 0.14117647f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from jail_stairs_wood.mtl —
                // three wood-brown shades, no texture. Descends from
                // ground level (pivot near Y=0) down through the full
                // 2-unit pit drop over a ~1.6 unit run — replaces
                // BuildStaircaseVisual's 3-cube fallback, see JailManager.
                // Re-exported once already (a user correction in Blender,
                // re-copied from Downloads/dungeon_pack/jail/stairs_wood/)
                // — that re-export carries real authored normals unlike
                // the rest of the pack (see HasAuthoredNormals, same
                // "Blender 5.2.0 LTS" .mtl header Portal.obj has), so
                // re-copy with this flag intact if it's updated again.
                SourceDir = "Assets/Art/DungeonPack/Jail/StairsWood",
                ObjName = "jail_stairs_wood",
                PrefabPath = "Assets/Resources/Dungeon/Prop_JailStairsWood.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Jail/StairsWood/Materials",
                HasAuthoredNormals = true,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.36862745f, 0.24313725f, 0.13333333f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.22745098f, 0.14901961f, 0.08627451f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.47843137f, 0.32941176f, 0.18823529f) },
                },
            },
            new PropVariant
            {
                // Kd values copied directly from bridge_edge.mtl — a
                // 4-shade wood-plank palette (deck / rail / dark frame /
                // light trim), no texture. Ships real authored normals
                // (its .mtl header is "Blender 5.2.0 LTS", same as
                // Portal / chicken_coop / jail_stairs — see
                // HasAuthoredNormals). The bridge's land-side end: a
                // landing flange (local -Z, overhanging to z≈-0.61 onto
                // the adjacent land tile) + anchor stakes on one side,
                // hanging trestle legs on the other. Deck sits at local
                // y=0 — BridgeManager places it at FloorSurfaceY and
                // rotates local -Z to face the claimed land tile it
                // touches. See BRIDGE_README.txt / BridgeManager.
                SourceDir = "Assets/Art/DungeonPack/Bridge/EdgePiece",
                ObjName = "bridge_edge",
                PrefabPath = "Assets/Resources/Dungeon/Prop_BridgeEdge.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Bridge/EdgePiece/Materials",
                HasAuthoredNormals = true,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.39215686f, 0.26666667f, 0.14901961f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.50980392f, 0.36078431f, 0.21176471f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.22745098f, 0.14901961f, 0.08627451f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.58823529f, 0.47058824f, 0.27450980f) },
                },
            },
            new PropVariant
            {
                // Same 4-shade wood palette as the edge piece (byte-for-
                // byte identical Kd values — the source .mtl's own
                // "material_N.001" dedup suffixes are stripped back to
                // "material_N" on copy so both pieces share one slot
                // naming). Both ends are the edge piece's "span side"
                // profile (deck + hanging trestle legs); chained between
                // the two edge pieces to cross wider gaps. BridgeManager
                // places it on a bridge tile with no claimed land along
                // its run axis, rotated to that axis. See BRIDGE_README.txt.
                SourceDir = "Assets/Art/DungeonPack/Bridge/MiddlePiece",
                ObjName = "bridge_middle",
                PrefabPath = "Assets/Resources/Dungeon/Prop_BridgeMiddle.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Bridge/MiddlePiece/Materials",
                HasAuthoredNormals = true,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.39215686f, 0.26666667f, 0.14901961f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.50980392f, 0.36078431f, 0.21176471f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.22745098f, 0.14901961f, 0.08627451f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.58823529f, 0.47058824f, 0.27450980f) },
                },
            },
            new PropVariant
            {
                // Same wood palette as the other bridge pieces. Deck + rope
                // rails bending 90 degrees — the raw mesh is open on -X / +Z
                // and railed shut on +X / -Z, so (after Unity's OBJ import
                // negates X) BridgeManager treats it as open on +X / +Z and
                // rotates it to whichever right-angle pair of sides carries
                // the two connecting bridge arms. See BridgeManager.
                SourceDir = "Assets/Art/DungeonPack/Bridge/Corner",
                ObjName = "bridge_corner",
                PrefabPath = "Assets/Resources/Dungeon/Prop_BridgeCorner.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Bridge/Corner/Materials",
                HasAuthoredNormals = true,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.39215686f, 0.26666667f, 0.14901961f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.50980392f, 0.36078431f, 0.21176471f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.22745098f, 0.14901961f, 0.08627451f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.58823529f, 0.47058824f, 0.27450980f) },
                },
            },
            new PropVariant
            {
                // Same wood palette as the other bridge pieces. Deck +
                // rope rails connecting three sides — default mesh is open
                // on +Z / -Z / +X and railed shut on -X, so BridgeManager
                // rotates its closed (local -X) side to face whichever
                // cardinal direction has no bridge/land connection. Placed
                // where three bridge arms meet. See BridgeManager.
                SourceDir = "Assets/Art/DungeonPack/Bridge/TJunction",
                ObjName = "bridge_tjunction",
                PrefabPath = "Assets/Resources/Dungeon/Prop_BridgeTJunction.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Bridge/TJunction/Materials",
                HasAuthoredNormals = true,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.39215686f, 0.26666667f, 0.14901961f) },
                    new MaterialSpec { SourceMaterialName = "material_1", Color = new Color(0.50980392f, 0.36078431f, 0.21176471f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.22745098f, 0.14901961f, 0.08627451f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.58823529f, 0.47058824f, 0.27450980f) },
                },
            },
            new PropVariant
            {
                // Same wood palette (only 3 of the 4 slots are used — no
                // "material_1"). A flat railless deck plate open on all
                // four sides; fully symmetric, so BridgeManager places it
                // at yaw 0. Placed where four bridge arms meet. See
                // BridgeManager.
                SourceDir = "Assets/Art/DungeonPack/Bridge/FourWay",
                ObjName = "bridge_fourway",
                PrefabPath = "Assets/Resources/Dungeon/Prop_BridgeFourWay.prefab",
                MaterialFolder = "Assets/Art/DungeonPack/Bridge/FourWay/Materials",
                HasAuthoredNormals = true,
                Materials = new[]
                {
                    new MaterialSpec { SourceMaterialName = "material_0", Color = new Color(0.39215686f, 0.26666667f, 0.14901961f) },
                    new MaterialSpec { SourceMaterialName = "material_2", Color = new Color(0.22745098f, 0.14901961f, 0.08627451f) },
                    new MaterialSpec { SourceMaterialName = "material_3", Color = new Color(0.58823529f, 0.47058824f, 0.27450980f) },
                },
            },
            // Treasury's 5 gold-pile tiers (see TREASURY_README.txt) —
            // TreasuryManager.GetGoldTier picks which one sits on a given
            // tile from its own stored gold amount. All 5 are already
            // sized to a single floor tile and pivoted at y=0 (per the
            // pack's own readme), so no scale correction is needed at
            // placement — see TreasuryManager.RefreshGoldPileVisual.
            GoldLevel(1),
            GoldLevel(2),
            GoldLevel(3),
            GoldLevel(4),
            GoldLevel(5),
        };

        private static PropVariant GoldLevel(int level)
        {
            return new PropVariant
            {
                SourceDir = $"Assets/Art/DungeonPack/Treasury/GoldLevel{level}",
                ObjName = $"treasury_gold_{level}",
                PrefabPath = $"Assets/Resources/Dungeon/Prop_TreasuryGold{level}.prefab",
                MaterialFolder = $"Assets/Art/DungeonPack/Treasury/GoldLevel{level}/Materials",
                Materials = TreasuryGoldMaterials,
            };
        }

        [MenuItem("Tools/DungeonPack/Setup Props")]
        public static void Run()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            foreach (var variant in Variants)
            {
                FixNormalImport(variant);
                var materials = BuildMaterials(variant);
                BuildPrefab(variant, materials);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[DungeonPackPropSetup] Done.");
        }

        /// Same issue every dungeon_pack .obj has (0 "vn" lines) — see
        /// DungeonPackWallSetup's own note. Without this, the mesh renders
        /// solid black under a Lit shader. Skipped for variants that ship
        /// their own real normals — see HasAuthoredNormals.
        private static void FixNormalImport(PropVariant variant)
        {
            if (variant.HasAuthoredNormals)
            {
                return;
            }

            var path = $"{variant.SourceDir}/{variant.ObjName}.obj";
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[DungeonPackPropSetup] Could not get ModelImporter for {path}");
                return;
            }

            if (importer.importNormals != ModelImporterNormals.Calculate)
            {
                importer.importNormals = ModelImporterNormals.Calculate;
                importer.SaveAndReimport();
            }
        }

        private static Dictionary<string, Material> BuildMaterials(PropVariant variant)
        {
            var result = new Dictionary<string, Material>();
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[DungeonPackPropSetup] Could not find URP/Lit shader — is URP installed/active?");
            }

            Directory.CreateDirectory(variant.MaterialFolder);
            foreach (var spec in variant.Materials)
            {
                var path = $"{variant.MaterialFolder}/M_{variant.ObjName}_{spec.SourceMaterialName}.mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    material = new Material(shader);
                    AssetDatabase.CreateAsset(material, path);
                }
                else
                {
                    material.shader = shader;
                }

                if (spec.DiffuseName != null)
                {
                    // Textured slot (see MaterialSpec.DiffuseName) — same
                    // _BaseMap-only build DungeonPackWallSetup uses for its
                    // own wall blocks.
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{variant.SourceDir}/{spec.DiffuseName}.png");
                    if (texture == null)
                    {
                        Debug.LogError($"[DungeonPackPropSetup] Could not load texture at {variant.SourceDir}/{spec.DiffuseName}.png");
                    }

                    material.SetTexture("_BaseMap", texture);
                    material.SetColor("_BaseColor", Color.white);
                }
                else
                {
                    material.SetColor("_BaseColor", spec.Color);
                }

                material.SetFloat("_Smoothness", 0.15f);
                EditorUtility.SetDirty(material);
                result[spec.SourceMaterialName] = material;
            }

            return result;
        }

        private static void BuildPrefab(PropVariant variant, Dictionary<string, Material> materialsByName)
        {
            var sourcePath = $"{variant.SourceDir}/{variant.ObjName}.obj";
            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (sourceAsset == null)
            {
                Debug.LogError($"[DungeonPackPropSetup] Could not load source mesh at {sourcePath}");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourceAsset);
            var renderers = instance.GetComponentsInChildren<MeshRenderer>();
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var sourceName = materials[i] != null ? materials[i].name : null;
                    if (sourceName != null && materialsByName.TryGetValue(sourceName, out var replacement))
                    {
                        materials[i] = replacement;
                    }
                    else
                    {
                        Debug.LogWarning($"[DungeonPackPropSetup] {variant.ObjName}: no material mapped for slot '{sourceName}' on renderer '{renderer.name}'");
                    }
                }

                renderer.sharedMaterials = materials;
            }

            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }

                Debug.Log($"[DungeonPackPropSetup] {variant.ObjName}.obj bounds — size: {bounds.size}, min Y: {bounds.min.y:F3}, renderers: {renderers.Length}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(variant.PrefabPath));
            PrefabUtility.SaveAsPrefabAsset(instance, variant.PrefabPath);
            Object.DestroyImmediate(instance);
        }
    }
}

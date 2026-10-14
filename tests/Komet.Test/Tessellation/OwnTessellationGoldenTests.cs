using Komet.Core;
using Komet.Tessellation;
using Vintagestory.GameContent;

namespace Komet.Test.Tessellation;

// Komet's tesselators (OwnTessellation) against the engine's through NowProcessChunk on random worlds: the Cube slot's
// BleedingCubeTesselator, the layers' and Transparent's CubeTesselator, the TopsoilTesselator, the CrossTesselator, the
// JsonTesselator and the JsonAndSnowLayerTesselator, each world tessellated once with the game's slots and once, on fresh pools, with
// them swapped. Every part's meshes, the topsoil overlay shorts and the bounds (TessParallelTests.Digest) must be identical, with
// smooth shadows on (FaceLight's corners) and off, alternates on a cube, a layer, the grass, a cross and a JSON block, a randomly
// turned cross, a random draw offset, a colour map and snow above the topsoil. The JSON blocks get random models (faces with and
// without facing, on the boundary and inside, DisableRandoms and Liquid faces, wind bits): one with alternates, a lod 2 model and
// normal wind, one with snow under it, a lod 0 model where not surrounded, leaves wind and its own pass; and two whose overrides only
// rewrite the flags (TessSafety's FlagWriters), a BlockWithLeavesMotion with leaves wind and alternates and a BlockPlant with normal
// wind, each run meshing its own copy of the models (the overrides leave their flags in the shared mesh) under sunlight that differs
// by world.
[NonParallelizable]
public sealed class OwnTessellationGoldenTests
{
    private const int Worlds = 16, Soil = ChunkRig.Count, Grass = Soil + 1, SnowCube = Soil + 2, Layer = Soil + 3, Json = Soil + 4,
        JsonSnow = Soil + 5, Leaves = Soil + 6, Plant = Soil + 7, Cross = Soil + 8, Turned = Soil + 9, Extra = 10;
    private const int Positions = 8, Slots = 40, Deep = 40;
    private static readonly int[] OneAtlas = [0];

    [TearDown]
    public void Reset() => (OwnTessellation.Enabled, OwnTessellation.Installed, Counting.Hud) = (true, false, false);

    [Test]
    public void CubesTopsoilAndJsonBlocksMatchTheEngine()
    {
        var r = new Random(20261007);
        using var harmony = new TestHarmony("komet-test-owntessellation");
        FaceLight.Install(harmony);
        OwnTessellation.Install(harmony);
        Assert.That((FaceLight.Installed, OwnTessellation.Installed, OwnTessellation.JsonInstalled),
            Is.EqualTo((true, true, true)));
        Counting.Hud = true;
        var (vertices, before, json) = (0, OwnTessellation.Blocks, OwnTessellation.JsonBlocks);
        for (var world = 0; world < Worlds; world++)
        {
            using var rig = new ChunkRig(Extra);
            Writers(rig);
            for (var c = 0; c < 27; c++)
            {
                var (cx, cy, cz) = (c % 3, c / 9, c / 3 % 3);
                rig.Put(cx, cy, cz, (x, y, z) => Pick(r, cx * 32 + x, cy * 32 + y, cz * 32 + z)).Lighting
                    .FillWithSunlight((ushort)((c * 7 + world * 5) % 25));
            }

            var (ao, seed) = (world % 2 == 0, r.Next());
            var engine = Run(Prepare(rig, ao, Shapes(new Random(seed), rig.Blocks)));
            var ours = Prepare(rig, ao, Shapes(new Random(seed), rig.Blocks)); // the overrides wrote into the first run's models
            Assert.That(OwnTessellation.Swap(ours.Tesselator), Is.EqualTo(13),
                "slots 1-8, 10-12, 14 and 17");
            Assert.That(Run(rig), Is.EqualTo(engine), $"world {world}");
            vertices += engine.Vertices;
        }

        Assert.Multiple(() =>
        {
            Assert.That(vertices, Is.GreaterThan(Worlds * 1000), "cubes and topsoil were drawn");
            Assert.That(OwnTessellation.Blocks - before, Is.GreaterThan(Worlds * 500), "by Komet's tesselators");
            Assert.That(OwnTessellation.JsonBlocks - json, Is.GreaterThan(Worlds * 100), "JSON blocks among them");
        });
    }

    private static TessParallelTests.Digest Run(ChunkRig rig)
    {
        var tess = rig.Tess(1, 1, 1);
        _ = rig.Tesselator.NowProcessChunk(1, 1, 1, tess, false);
        return TessParallelTests.Digest.Of(tess);
    }

    // Ground at y 40..47 (chunk row 1): topsoil under snow, a layer, flags writers or air, below it mixed cubes, layers, JSON blocks and
    // caves; leaves from y 40 on, whose override looks down seven cells and leaves the halo below it (to the world's accessor)
    private static int Pick(Random r, int x, int y, int z)
    {
        var ground = 40 + TessMix.Hash(x, 0, z) % 8;
        if (y > ground)
            return y != ground + 1 ? ChunkRig.Air : (TessMix.Hash(x, 1, z) % 9) switch
            {
                0 => SnowCube,
                1 => Layer,
                2 => JsonSnow,
                3 => Leaves,
                4 => Plant,
                5 => Cross,
                6 => Turned,
                _ => ChunkRig.Air
            };
        if (y == ground) return r.Next(2) == 0 ? Soil : Grass;
        return r.Next(14) switch
        {
            0 => ChunkRig.Air, 1 => ChunkRig.Glass, 2 => ChunkRig.Slab, 3 => ChunkRig.Merge, 4 => ChunkRig.Granite, 5 => Layer,
            6 => Json, 7 => JsonSnow, 8 when y >= Deep => Leaves, 9 => Plant, 10 => Cross, _ => ChunkRig.Stone
        };
    }

    // TessParallelTests' Mesher and Manager with the game's slots on fresh pools, 7 texture ids a block, 8 distinct atlas positions
    private static ChunkRig Prepare(ChunkRig rig, bool ao, ShapeTesselatorManager shapes)
    {
        var (game, blocks) = (rig.Game, rig.Blocks);
        foreach (var id in (int[])[Soil, Grass])
            (blocks[id].DrawType, blocks[id].RenderPass, blocks[id].Textures) = (EnumDrawType.TopSoil,
                EnumChunkRenderPass.TopSoil,
                new Dictionary<string, CompositeTexture> { ["snowed"] = new() { Baked = new() { TextureSubId = 7 } } });
        (blocks[ChunkRig.Glass].DrawType, blocks[SnowCube].BlockMaterial) = (EnumDrawType.Transparent, EnumBlockMaterial.Snow);
        (blocks[Layer].DrawType, blocks[Layer].SideOpaque, blocks[Layer].SideAo) =
            (EnumDrawType.BlockLayer_3, new SmallBoolArray(1 << 5), new SmallBoolArray(0));
        foreach (var id in (int[])[Cross, Turned])
            (blocks[id].DrawType, blocks[id].RenderPass, blocks[id].AllSidesOpaque) =
                (EnumDrawType.Cross, EnumChunkRenderPass.OpaqueNoCull, false);
        (blocks[Turned].RandomizeRotations, blocks[Turned].RandomizeAxes, blocks[Turned].DoNotRenderAtLod2) =
            (true, EnumRandomizeAxes.XYZ, true);
        foreach (var id in (int[])[ChunkRig.Granite, Grass, Layer, Cross])
            (blocks[id].HasAlternates, blocks[id].FastTextureVariants) = (true, Variants());
        (blocks[ChunkRig.Merge].RandomDrawOffset, blocks[Grass].ShapeUsesColormap) = (1, true);
        JsonBlocks(blocks);
        foreach (var b in blocks)
            (b.VertexFlags, b.EmitSideAo) = (new VertexFlags(b.BlockId), b.AllSidesOpaque ? (byte)63 : (byte)0);
        (blocks[Json].VertexFlags.WindMode, blocks[JsonSnow].VertexFlags.WindMode) =
            (EnumWindBitMode.NormalWind, EnumWindBitMode.Leaves);
        (blocks[Leaves].VertexFlags.WindMode, blocks[Plant].VertexFlags.WindMode) =
            (EnumWindBitMode.Leaves, EnumWindBitMode.NormalWind);
        (blocks[Cross].VertexFlags.WindMode, blocks[Turned].VertexFlags.WindMode) =
            (EnumWindBitMode.NormalWind, EnumWindBitMode.WeakWind);
        game.FastBlockTextureSubidsByBlockAndFace =
            [.. blocks.Select(b => Enumerable.Range(b.BlockId, 7).Select(f => f % 7).ToArray())];
        ChunkRig.Set(game.BlockAtlasManager, "TextureAtlasPositionsByTextureSubId", Enumerable.Range(0, Positions)
            .Select(i => new TextureAtlasPosition { x1 = i / 8f, x2 = (i + 1) / 8f, y1 = i / 16f, y2 = 1 - i / 16f })
            .ToArray());
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        ChunkRig.Set(platform, "uptimeStopWatch", Stopwatch.StartNew());
        (game.Platform, rig.Map.MapChunkSize) = (platform, ChunkRig.Size);
        var tesselators = new IBlockTesselator[Slots];
        for (var layer = 1; layer <= 7; layer++) tesselators[layer] = new CubeTesselator(0.125f * layer);
        tesselators[(int)EnumDrawType.Cube] = new BleedingCubeTesselator(1f);
        tesselators[(int)EnumDrawType.Transparent] = new CubeTesselator(1f);
        tesselators[(int)EnumDrawType.TopSoil] = new TopsoilTesselator();
        tesselators[(int)EnumDrawType.Cross] = new CrossTesselator();
        tesselators[(int)EnumDrawType.JSON] = new JsonTesselator();
        tesselators[(int)EnumDrawType.JSONAndSnowLayer] = new JsonAndSnowLayerTesselator();
        ChunkRig.Set(rig.Tesselator, "blockTesselators", tesselators);
        ((TCTCache)ChunkRig.Get(rig.Tesselator, "vars")).shapes = shapes;
        var (api, render) = ((ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI)),
            AccessTools.Field(typeof(ClientCoreAPI), "renderapi"));
        render.SetValue(api, RuntimeHelpers.GetUninitializedObject(render.FieldType)); // AddJsonModelDataToMesh reads useSSBOs
        game.api = api;
        ChunkRig.Set(rig.Tesselator, "AoAndSmoothShadows", ao);
        ChunkRig.Set(rig.Tesselator, "regionSize", 16 * ChunkRig.Size); // the colour map's climate lookup
        _ = AccessTools.Method(typeof(ChunkTesselator), "UpdateForAtlasses").Invoke(rig.Tesselator, [OneAtlas]);
        return rig;
    }

    // The flags writers in place of two of the rig's cubes, as see-through JSON blocks of their own material
    private static void Writers(ChunkRig rig)
    {
        Block[] writers = [new BlockWithLeavesMotion(), new BlockPlant()];
        for (var i = 0; i < writers.Length; i++)
        {
            var (id, block) = (Leaves + i, writers[i]);
            (block.BlockId, block.Code, block.DrawType, block.FaceCullMode, block.AllSidesOpaque) =
                (id, new AssetLocation("komet", "writer" + i), EnumDrawType.JSON, EnumFaceCullMode.Default, false);
            block.BlockMaterial = i == 0 ? EnumBlockMaterial.Leaves : EnumBlockMaterial.Plant;
            (rig.Blocks[id], rig.Game.Blocks[id]) = (block, block);
        }
    }

    // The JSON blocks: one opaque with a colour map, frost, alternates by x, y and z; one with snow under it, see-through, in the
    // OpaqueNoCull pass, offset at random, collapsing into its material and drawn at lod 2
    private static void JsonBlocks(Block[] blocks)
    {
        var (json, snow) = (blocks[Json], blocks[JsonSnow]);
        (json.DrawType, json.ShapeUsesColormap, json.Frostable, json.ExtraColorBits, json.RandomizeAxes) =
            (EnumDrawType.JSON, true, true, 5, EnumRandomizeAxes.XYZ);
        (snow.DrawType, snow.RenderPass, snow.RandomDrawOffset, snow.FaceCullMode, snow.DoNotRenderAtLod2) =
            (EnumDrawType.JSONAndSnowLayer, EnumChunkRenderPass.OpaqueNoCull, 1, EnumFaceCullMode.CollapseMaterial, true);
        (snow.AllSidesOpaque, snow.Lod0Shape) = (false, new CompositeShape());
    }

    // The models: the default and three lod 1 alternates of the first JSON block and its lod 2 model, the second one's default and
    // lod 0, the flags writers' defaults and two lod 1 alternates of the leaves
    private static ShapeTesselatorManager Shapes(Random r, Block[] blocks)
    {
        var shapes = (ShapeTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ShapeTesselatorManager));
        (shapes.blockModelDatas, shapes.altblockModelDatasLod0, shapes.altblockModelDatasLod1, shapes.altblockModelDatasLod2) =
            (new MeshData[blocks.Length], new MeshData[blocks.Length][], new MeshData[blocks.Length][],
                new MeshData[blocks.Length][]);
        (shapes.blockModelDatas[Json], shapes.blockModelDatas[JsonSnow]) = (Model(r, 14, true), Model(r, 10, false));
        shapes.altblockModelDatasLod1[Json] = [Model(r, 14, true), Model(r, 9, true), Model(r, 20, true)];
        (blocks[Json].Lod2Mesh, blocks[JsonSnow].Lod0Mesh) = (Model(r, 6, true), Model(r, 8, false));
        (shapes.blockModelDatas[Leaves], shapes.blockModelDatas[Plant]) = (Model(r, 12, true), Model(r, 8, false));
        shapes.altblockModelDatasLod1[Leaves] = [Model(r, 12, true), Model(r, 7, false)];
        return shapes;
    }

    // Quads facing none or one of the six sides, every third one inside the block, the rest on its side (and culled there where the
    // side is hidden, unless a coordinate leaves the block); some DisableRandoms or Liquid faces where the model has passes
    private static MeshData Model(Random r, int faces, bool passes)
    {
        float[] steps = [0f, 0.25f, 0.5f, 1f, 1.25f];
        var mesh = new MeshData(4 * faces, 6 * faces);
        for (var l = 0; l < faces; l++)
        {
            var facing = l % 7 - 1;
            var axis = facing < 0 ? 1 : JsonMesh.FaceCoord[facing];
            var plane = facing is 1 or 2 or 4 ? 1f : 0f;
            if (l % 3 == 0) plane = 0.5f;
            for (var k = 0; k < 4; k++)
            {
                var p = Enumerable.Range(0, 3).Select(c => c == axis ? plane : steps[r.Next(steps.Length)]).ToArray();
                mesh.AddVertexWithFlags(p[0], p[1], p[2], r.NextSingle(), r.NextSingle(), 0, (r.Next(4) << 25) | r.Next(256));
            }

            mesh.AddIndices(4 * l, 4 * l + 1, 4 * l + 2, 4 * l, 4 * l + 2, 4 * l + 3);
            mesh.AddXyzFace((byte)(facing + 1));
            mesh.AddTextureId(0);
            mesh.AddColorMapIndex((byte)r.Next(4), (byte)r.Next(4), r.Next(2) == 0);
            if (passes) mesh.AddRenderPass((l % 5) switch { 0 => 1024, 4 => 4, _ => 0 });
        }

        return mesh;
    }

    // Two or three alternates a face, one of texture id 0 (BleedingCubeTesselator then takes the face's own), none for the down face
    private static BakedCompositeTexture[]?[] Variants() =>
    [
        .. Enumerable.Range(0, 6).Select(f => f == 5
            ? null
            : Enumerable.Range(0, 2 + f % 2).Select(k => new BakedCompositeTexture { TextureSubId = (f + 3 * k) % Positions })
                .ToArray())
    ];
}

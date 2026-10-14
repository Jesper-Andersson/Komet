using Komet.World;
using Newtonsoft.Json.Linq;
using Vintagestory.GameContent;

namespace Komet.Test.World;

// Golden: CookingRecipe.Matches and CookingRecipeIngredient.GetMatchingStack answer every input exactly as the engine's own bodies did
// before the rewrite - match, servings and the matched stack - with liquids, cooked stacks, wildcards and ignored attributes, also
// after IgnoredStackAttributes is replaced; and a match no longer allocates its copies
[NonParallelizable]
public sealed class CookingMatchTests
{
    private const int Inputs = 500;
    private static readonly string[] Ignored = GlobalConstants.IgnoredStackAttributes;
    private readonly ICoreAPI _api = Answers.Of<ICoreAPI>(new() { ["get_World"] = _ => null });
    private readonly Dictionary<string, Item> _items = [];

    [TearDown]
    public void Restore() => (CookingMatch.Enabled, GlobalConstants.IgnoredStackAttributes) = (true, Ignored);

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-cookingmatch");
        CookingMatch.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(CookingMatch.Matched, Is.True, "Matches or GetMatchingStack is not the body verified");
            Assert.That(CookingMatch.Rewritten, Is.True);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void EveryAnswerIsTheEnginesOwn(bool enabled)
    {
        var (recipes, inputs) = (Recipes(), Stacks(new Random(7), Inputs));
        var expected = Replies(recipes, inputs);
        GlobalConstants.IgnoredStackAttributes = [.. Ignored, "custom"];
        var widened = Replies(recipes, inputs);
        GlobalConstants.IgnoredStackAttributes = Ignored;
        using var harmony = new TestHarmony("komet-test-cookingmatch");
        CookingMatch.Install(harmony, new QuietLogger());
        CookingMatch.Enabled = enabled;
        Assert.That(CookingMatch.Rewritten, Is.True);
        var actual = Replies(recipes, inputs);
        GlobalConstants.IgnoredStackAttributes = [.. Ignored, "custom"];
        var actualWidened = Replies(recipes, inputs);
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actualWidened, Is.EqualTo(widened), "a replaced ignore list is followed");
            Assert.That(expected.Count(a => a.StartsWith("match", StringComparison.Ordinal)), Is.GreaterThan(20),
                "the inputs reach matches too");
        });
    }

    [Test]
    public void AMatchNoLongerCopies()
    {
        var (recipes, inputs) = (Recipes(), Stacks(new Random(11), 50));
        var engine = Allocated(recipes, inputs);
        using var harmony = new TestHarmony("komet-test-cookingmatch");
        CookingMatch.Install(harmony, new QuietLogger());
        _ = Allocated(recipes, inputs); // the thread's lists
        var komet = Allocated(recipes, inputs);
        var kept = typeof(CookingMatch).GetField("_stacks", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        Assert.Multiple(() =>
        {
            Assert.That(komet, Is.LessThan(engine / 4), $"engine {engine} bytes, Komet {komet}");
            Assert.That(kept, Is.Empty, "the thread's list keeps the stacks, and with them their world"); // the main thread outlives it
        });
    }

    private static long Allocated(CookingRecipe[] recipes, ItemStack?[][] inputs)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var input in inputs)
        {
            foreach (var recipe in recipes)
            {
                var servings = 0;
                _ = recipe.Matches(input, ref servings);
            }
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static List<string> Replies(CookingRecipe[] recipes, ItemStack?[][] inputs)
    {
        var answers = new List<string>();
        foreach (var input in inputs)
        {
            foreach (var recipe in recipes)
            {
                var servings = -7;
                var match = recipe.Matches(input, ref servings);
                answers.Add($"{(match ? "match" : "no")} {servings}");
                foreach (var ingredient in recipe.Ingredients!)
                    answers.Add(string.Join(",",
                        input.Select(s => Array.IndexOf(ingredient.ValidStacks, ingredient.GetMatchingStack(s)))));
            }
        }

        return answers;
    }

    private CookingRecipe[] Recipes() =>
    [
        new()
        {
            Code = "soup",
            Ingredients =
            [
                Ingredient("water", 1, 1, 1f, Exact("waterportion", 100)),
                Ingredient("veg", 1, 2, 0, Wild("vegetable-*")),
                Ingredient("meat", 0, 1, 0, Cooked("redmeat-raw", "redmeat-cooked"))
            ]
        },
        new()
        {
            Code = "porridge",
            Ingredients =
            [
                Ingredient("grain", 1, 2, 0, Wild("grain-*"), Exact("flour", 2)),
                Ingredient("fruit", 0, 2, 0, Wild("fruit-*"))
            ]
        },
        new()
        {
            Code = "stew",
            Ingredients =
            [
                Ingredient("meat", 2, 3, 0, Cooked("redmeat-raw", "redmeat-cooked")),
                Ingredient("veg", 0, 2, 0, Wild("vegetable-*"))
            ]
        }
    ];

    private static CookingRecipeIngredient Ingredient(string code, int min, int max, float litres,
        params CookingRecipeStack[] stacks) =>
        new() { Code = code, MinQuantity = min, MaxQuantity = max, PortionSizeLitres = litres, ValidStacks = stacks };

    private CookingRecipeStack Exact(string code, int size) => new()
    {
        Code = new AssetLocation("game", code), Type = EnumItemClass.Item, StackSize = size,
        ResolvedItemstack = new ItemStack(Item(code), size)
    };

    private static CookingRecipeStack Wild(string code) =>
        new() { Code = new AssetLocation("game", code), Type = EnumItemClass.Item, StackSize = 1 };

    private CookingRecipeStack Cooked(string raw, string cooked)
    {
        var stack = Exact(raw, 1);
        stack.CookedStack = new JsonItemStack
        {
            Code = new AssetLocation("game", cooked), Type = EnumItemClass.Item,
            ResolvedItemstack = new ItemStack(Item(cooked))
        };
        return stack;
    }

    // Pot contents of 4 to 6 slots: some empty, a stack size that often fits a portion, and now and then an attribute - ignored by
    // default, ignored only after the list is widened, or never
    private ItemStack?[][] Stacks(Random r, int count)
    {
        string[] codes =
        [
            "waterportion", "vegetable-carrot", "vegetable-onion", "redmeat-raw", "redmeat-cooked", "grain-spelt",
            "flour", "fruit-cherry", "stone-granite"
        ];
        var inputs = new ItemStack?[count][];
        for (var i = 0; i < count; i++)
        {
            if (i % 2 == 0)
            {
                inputs[i] = Pot(r);
                continue;
            }

            inputs[i] = new ItemStack?[r.Next(4, 7)];
            for (var s = 0; s < inputs[i].Length; s++)
            {
                if (r.Next(10) < 4) continue;
                var code = codes[r.Next(codes.Length)];
                var portion = code == "flour" ? 2 : 1;
                var size = code == "waterportion" ? 100 * r.Next(1, 4) : r.Next(1, 4) * portion;
                var stack = new ItemStack(Item(code), size);
                var mark = r.Next(8);
                if (mark == 0) stack.Attributes.SetFloat("temperature", 20);
                if (mark == 1) stack.Attributes.SetString("custom", "x");
                if (mark == 2) stack.Attributes.SetString("other", "y");
                inputs[i][s] = stack;
            }
        }

        return inputs;
    }

    // A pot filled for one of the recipes, a serving count apart, now and then one stack a portion off
    private ItemStack?[] Pot(Random r)
    {
        var q = r.Next(1, 4);
        string[][] recipes =
        [
            ["waterportion", "vegetable-carrot", "vegetable-onion", "redmeat-cooked"],
            ["grain-spelt", "fruit-cherry", "fruit-cherry"], ["redmeat-raw", "redmeat-raw", "vegetable-onion"]
        ];
        var chosen = recipes[r.Next(recipes.Length)].Where((_, k) => k < 2 || r.Next(2) == 0).ToList();
        var pot = new ItemStack?[r.Next(Math.Max(4, chosen.Count), 7)];
        for (var k = 0; k < chosen.Count; k++)
        {
            var size = chosen[k] == "waterportion" ? 100 * q : q;
            pot[k] = new ItemStack(Item(chosen[k]), r.Next(8) == 0 ? size + 1 : size);
        }

        return [.. pot.OrderBy(_ => r.Next())];
    }

    private Item Item(string code)
    {
        if (_items.TryGetValue(code, out var item)) return item;
        item = new Item(_items.Count + 1) { Code = new AssetLocation("game", code) };
        if (code == "waterportion")
            item.Attributes = new JsonObject(JToken.Parse("{ waterTightContainerProps: { itemsPerLitre: 100 } }"));
        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(item, _api);
        _items[code] = item;
        return item;
    }
}

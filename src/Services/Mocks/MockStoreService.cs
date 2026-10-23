namespace Resurgence.Services.Mocks;

public sealed class MockStoreService : IStoreService
{
    const int PageSize = 12;

    static StoreCategory C(string key, string name, params StoreCategory[] children) => new(key, name, children);

    static readonly StoreCategory[] Categories =
    [
        C("sales", "Sales"), C("all", "All Items", C("all", "All Items")), C("bundles", "Bundles", C("bundles", "Bundles")),
        C("0", "Weapons"), C("10", "Ammo"),
        C("5", "Sights", C("50", "Rifle Sights"), C("51", "Handgun Sights"), C("52", "Shotgun Sights"), C("53", "Bow & Crossbow Sights"), C("54", "Muzzleloader Sights")),
        C("59", "Dogs"), C("15", "Lures"), C("18", "Clothing"), C("27", "Upgrades"), C("46", "Services"), C("114", "Trophy Shot Poses"), C("24", "Equipment"), C("29", "Large Equipment"),
        C("57", "Event Items", C("570", "Event A"), C("571", "Event B"), C("572", "Event C"), C("573", "Event D")),
        C("87", "Trophy Lodges"), C("108", "Vehicles"),
    ];

    // the leaf category and the base name of each generated item, in the order they cycle
    static readonly (string Key, string Name)[] Leaves =
    [
        ("573", "Heated Tent"), ("0", "Rifle"), ("10", "Rifle Ammo"), ("50", "Rifle Scope"), ("51", "Handgun Scope"), ("59", "Dog"), ("15", "Lure"), ("18", "Jacket"),
        ("27", "Upgrade"), ("46", "Service"), ("114", "Pose"), ("24", "Binoculars"), ("29", "Blind"), ("570", "Event Cap"), ("87", "Lodge"), ("108", "Vehicle"),
    ];

    static readonly string[] Variants = ["Standard", "Engraved", "Black", "Silver", "Camouflage"];

    // 46 items, nearly all on sale: four pages of twelve
    static readonly StoreItem[] Items = Enumerable.Range(0, 46).Select(i =>
    {
        var (key, name) = Leaves[i % Leaves.Length];
        var ems = 2 + i * 7 % 60;
        var sale = i % 11 != 10;
        return new StoreItem(1227 - i, $"{name} {(char)('A' + i / Leaves.Length)}", Variants[i % Variants.Length], key, sale ? ems : ems + 25, sale ? ems + 25 : null, (sale ? ems : ems + 25) * 40, i % 9 == 4 ? 3 : 0, i % 7 != 5, key == "573" ? 7 : 0,
            "Two-man item suitable for extremely cold conditions. Not recommended for use in warm reserves however.\n\nCharacteristics:\n* Establish a new permanent spawn point\n* Access your inventory\n* Up to 10 can be erected in each reserve at the same time\n* Can be dismantled and moved");
    }).ToArray();

    static readonly StoreBundle[] Bundles =
    [
        new(0, "Bait - Refill Pack", "Buy three bait, get one free!", 90, true, [new(137, "Bait", 4, 0, 120)]),
    ];

    public Task<IReadOnlyList<StoreCategory>> CategoriesAsync() => Task.FromResult<IReadOnlyList<StoreCategory>>(Categories);

    public Task<Page<StoreItem>> ItemsAsync(string view, string search, int page)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var inView = Categories.FirstOrDefault(c => c.Key == view)?.Children.Select(c => c.Key).Append(view).ToArray() ?? [view];
        var hits = Items.Where(i => (view == "all" || (view == "sales" ? i.WasEms is not null : inView.Contains(i.Category)))
            && words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Variant.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        var pages = Math.Max(1, (hits.Count + PageSize - 1) / PageSize);
        var n = Math.Clamp(page, 1, pages);
        return Task.FromResult(new Page<StoreItem>(hits.Skip((n - 1) * PageSize).Take(PageSize).ToList(), n, pages));
    }

    // no bundles in the mock
    public Task<IReadOnlyList<StoreBundle>> BundlesAsync() => Task.FromResult<IReadOnlyList<StoreBundle>>([]);

    public Task<StoreItem?> ItemAsync(int id) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

    public Task<StoreBundle?> BundleAsync(int id) => Task.FromResult(Bundles.FirstOrDefault(b => b.Id == id));

    // no balance changes in the mock
    public Task<Result> BuyAsync(int id, bool bundle, bool gms) => Task.FromResult(Result.Success);
}

namespace Resurgence.Services.Mocks;

/// <summary>Buying changes the shared wallet state. Mock "store:fail" makes every purchase fail, "store:nosales" leaves no item on sale.</summary>
public sealed class MockStoreService(MockWalletState state, string? mock = null) : IStoreService
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

    static readonly string[] Species = ["Whitetail Deer", "Red Fox", "Mallard", "Moose", "Canada Goose"];

    // 46 items, nearly all on sale: four pages of twelve. Every 18th has an own limit of 3 (it starts at 3, so it is reached), the services are owned for days,
    // one of them is disabled by the services, and banners (on the items that are not on sale), the ammunition amounts, attachments and permitted ammunition are spread over the rest.
    static readonly StoreItem[] Items = Enumerable.Range(0, 46).Select(i =>
    {
        var (key, name) = Leaves[i % Leaves.Length];
        var ems = 2 + i * 7 % 60;
        var sale = i % 11 != 10;
        return new StoreItem(1227 - i, $"{name} {(char)('A' + i / Leaves.Length)}", Variants[i % Variants.Length], key, sale ? ems : ems + 25, sale ? ems + 25 : null, (sale ? ems : ems + 25) * 40, i % 9 == 4 ? 3 : 0, i % 7 != 5, key switch { "573" => 7, "0" => 5, "10" => 1, _ => 0 },
            "Two-man item suitable for extremely cold conditions. Not recommended for use in warm reserves however.\n\nCharacteristics:\n* Establish a new permanent spawn point\n* Access your inventory\n* Up to 10 can be erected in each reserve at the same time\n* Can be dismantled and moved",
            DaysLeft: i switch { 9 => 12, 25 => 0.5, 41 => 0, _ => null }, OwnLimit: i % 18 == 4 ? 3 : 0, DisabledByServices: i == 41,
            Banner: i switch { 3 or 43 => "premium", 10 => "limited", 21 => "new", 32 => "community", _ => null }, MultiBuy: key == "10");
    }).ToArray();

    static readonly StoreItem[] Sights = Items.Where(i => i.Category is "50" or "51").Take(3).ToArray();
    static readonly StoreItem[] Ammo = Items.Where(i => i.Category == "10").Take(3).ToArray();

    // weapons list the sights and ammunition that fit; weapons and ammunition show for which species each ammunition is permitted
    static readonly EthicalMatrix Ethics = new(Ammo.Select(a => a.Name).ToList(), Species.Select((s, r) => new EthicalRow(s, Ammo.Select((_, c) => (r + c) % 3 != 0).ToList())).ToList());

    static StoreItem Extras(StoreItem i) => i.Category switch
    {
        "0" => i with { Fits = [new("Sights", Sights.Select(s => new StoreFit(s.Id, s.Name)).ToList()), new("Ammo", Ammo.Select(a => new StoreFit(a.Id, a.Name)).ToList())], Ethics = Ethics },
        "10" => i with { Ethics = Ethics },
        _ => i,
    };

    static readonly StoreBundle[] Bundles =
    [
        new(0, "Bait - Refill Pack", "Buy three bait, get one free!", 90, true, [new(137, "Bait", 4, 0, 120)]),
        new(1, "Hunter Starter Pack", "A rifle, ammunition and a scope to start with.\nOne rifle per hunter.", 80, true,
            [new(1226, "Rifle A", 1, 0, 120, "Engraved", 1), new(1225, "Rifle Ammo A", 2, 0, 40, "Black"), new(1225, "Rifle Ammo A", 3, 0, 60, "Black"), new(1224, "Rifle Scope A", 1, 0, 50, "Silver", 2)]),
    ];

    // the items and bundles as the account sees them now: what it bought is owned, a limit may be reached
    StoreItem Live(StoreItem i)
    {
        var bought = state.Bought(i.Id);
        return Extras(i) with
        {
            Owned = i.Owned + bought,
            DaysLeft = i.DaysLeft is { } d ? d + 30 * bought : null,
            WasEms = mock == "store:nosales" ? null : i.WasEms,
        };
    }

    StoreBundle Live(StoreBundle b)
    {
        var lines = b.Lines.Select(l => l with { Owned = l.Owned + state.Bought(l.ItemId) }).ToList();
        return b with { Lines = lines, Buyable = b.Buyable && !lines.Any(l => l.OwnLimit > 0 && l.Owned >= l.OwnLimit) };
    }

    public Task<IReadOnlyList<StoreCategory>> CategoriesAsync() => Task.FromResult<IReadOnlyList<StoreCategory>>(Categories);

    public Task<Page<StoreItem>> ItemsAsync(string view, string search, int page)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var inView = Categories.FirstOrDefault(c => c.Key == view)?.Children.Select(c => c.Key).Append(view).ToArray() ?? [view];
        var hits = Items.Select(Live).Where(i => (view == "all" || (view == "sales" ? i.WasEms is not null : inView.Contains(i.Category)))
            && words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Variant.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        var pages = Math.Max(1, (hits.Count + PageSize - 1) / PageSize);
        var n = Math.Clamp(page, 1, pages);
        return Task.FromResult(new Page<StoreItem>(hits.Skip((n - 1) * PageSize).Take(PageSize).ToList(), n, pages));
    }

    public Task<IReadOnlyList<StoreBundle>> BundlesAsync() => Task.FromResult<IReadOnlyList<StoreBundle>>(Bundles.Select(Live).ToList());

    public Task<bool> HasSalesAsync() => Task.FromResult(mock != "store:nosales");

    public Task<StoreItem?> ItemAsync(int id) => Task.FromResult(Items.Where(i => i.Id == id).Select(Live).FirstOrDefault());

    public Task<StoreBundle?> BundleAsync(int id) => Task.FromResult(Bundles.Where(b => b.Id == id).Select(Live).FirstOrDefault());

    // the request takes a moment, so the pending state of the buy dialog can be seen
    public async Task<Result> BuyAsync(int id, bool bundle, bool gms, int count = 1)
    {
        await Task.Delay(400);
        if (mock == "store:fail" || count < 1) return Result.Fail("Store.Oops");
        if (bundle)
        {
            if (Bundles.Where(b => b.Id == id).Select(Live).FirstOrDefault() is not { Buyable: true } b || gms || !state.Spend(false, b.Price, b.Name)) return Result.Fail("Store.Oops");
            foreach (var l in b.Lines) state.Add(l.ItemId, l.Count);
            return Result.Success;
        }
        if (Items.Where(i => i.Id == id).Select(Live).FirstOrDefault() is not { Buyable: true, DisabledByServices: false, LimitReached: false } item || (count > 1 && !item.MultiBuy)) return Result.Fail("Store.Oops");
        if (!state.Spend(gms, (gms ? item.Gms : item.Ems) * count, count > 1 ? $"{count}x {item.Name}" : item.Name)) return Result.Fail("Store.Oops");
        state.Add(id, count);
        return Result.Success;
    }
}

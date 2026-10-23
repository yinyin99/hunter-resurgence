using System.Globalization;

namespace Resurgence.Services.Mocks;

/// <summary>Mock "account:bans" gives the account one banned player.</summary>
public sealed class MockAccountService(string? mock = null) : IAccountService
{
    string email = "hunter@example.com", avatar = "avatar-05";
    (string Language, string Country, string TimeZone) locale = ("en", "", Choices.TimeZones.FirstOrDefault() ?? "UTC");
    string header = "recent";
    readonly List<BannedPlayer> bans = mock == "account:bans" ? [new(1, Names.Handle(11))] : [];

    // the six-digit mock code of every address change
    const string Code = "123456";

    static readonly LocaleChoices Choices = Locales();

    static LocaleChoices Locales()
    {
        // the countries of the platform, with the four most played first as in the old list
        string[] first = ["Germany", "Russia", "United Kingdom", "United States"];
        var countries = CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c => new RegionInfo(c.Name).EnglishName).Distinct().Order().Where(n => !first.Contains(n));
        var zones = TimeZoneInfo.GetSystemTimeZones().Select(z => TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var id) ? id : null).OfType<string>().Distinct().Order();
        return new([.. first, .. countries], [.. zones]);
    }

    static IReadOnlyList<HeaderImage> Images(string group, int count, int unlocked) => Enumerable.Range(1, count).Select(i => new HeaderImage($"{group}-{i}", i <= unlocked)).ToList();

    public Task<Account> AccountAsync() => Task.FromResult(new Account(email, avatar, locale.Language, locale.Country, locale.TimeZone));

    // 54 pictures and one locked one
    public Task<IReadOnlyList<AvatarChoice>> AvatarsAsync() =>
        Task.FromResult<IReadOnlyList<AvatarChoice>>([.. Enumerable.Range(1, 54).Select(i => new AvatarChoice($"avatar-{i:00}", $"Avatar {i}", false)), new("avatar-locked", "Locked Avatar", true)]);

    public Task<Result> SetAvatarAsync(string id) { avatar = id; return Task.FromResult(Result.Success); }

    public Task<LocaleChoices> LocaleChoicesAsync() => Task.FromResult(Choices);

    public Task<Result> SaveLocaleAsync(string language, string country, string timeZone) { locale = (language, country, timeZone); return Task.FromResult(Result.Success); }

    // taken@... is in use; the code is always 123456
    public Task<Result> RequestEmailChangeAsync(string address) =>
        Task.FromResult(!address.Contains('@') ? Result.Fail("Validation.Email.Invalid") : address.StartsWith("taken@", StringComparison.OrdinalIgnoreCase) ? Result.Fail("Set.EmailTaken") : Result.Success);

    public Task<Result> ConfirmEmailChangeAsync(string address, string code)
    {
        if (code != Code) return Task.FromResult(Result.Fail("Validation.Code.Invalid"));
        email = address;
        return Task.FromResult(Result.Success);
    }

    public Task<IReadOnlyList<BannedPlayer>> BansAsync() => Task.FromResult<IReadOnlyList<BannedPlayer>>(bans.ToList());

    public Task<Result> UnbanAsync(int id) { bans.RemoveAll(b => b.Id == id); return Task.FromResult(Result.Success); }

    // six pictures per group, the first two of each unlocked
    public Task<Headers> HeadersAsync() =>
        Task.FromResult(new Headers(header, [new("Animals", Images("animals", 6, 2)), new("Weapons", Images("weapons", 6, 2)), new("Exploration", Images("exploration", 6, 2)), new("Events", Images("events", 2, 2)), new("Daily Missions", [])]));

    public Task<Result> SaveHeaderAsync(string selected) { header = selected; return Task.FromResult(Result.Success); }

    public Task<Result> DisconnectSteamAsync() => Task.FromResult(Result.Success);
}

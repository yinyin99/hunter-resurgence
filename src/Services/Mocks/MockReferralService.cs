namespace Resurgence.Services.Mocks;

/// <summary>
/// A fictional referral page kept per circuit (the website) or per run (the launcher). Development states: <c>referral:none</c> (no recruit yet), <c>referral:entered</c> (a code was entered already),
/// <c>referral:rewards</c> (every reward is set: gm$, em$ and items). Without a state the rewards are not set ("to be announced"), the recruits are at various progress and a code can be entered.
/// The codes of <see cref="EnterAsync"/>: <c>ZZZZZZZZ</c> is unknown, <c>LOOPLOOP</c> a loop, <c>OLDOLD22</c> expired, <c>BANBAN22</c> banned, one's own code is refused; any other is accepted.
/// </summary>
public sealed class MockReferralService(string? mock = null) : IReferralService
{
    const string Own = "K7M2QX9P";
    static readonly ReferralQualification Qualification = new(2, 10);
    static readonly ReferralReward Welcome = new(0, 250, [new(384, "20 GA Semi-Automatic Shotgun", 1)]);
    const long Day = 86400;

    string? entered = mock == "referral:entered" ? "BEAR4TWC" : null;
    string? recruiter = mock == "referral:entered" ? "Moss_Walker" : null;

    static ReferralTier[] Tiers(bool set, int reached) => [.. new (int Recruits, ReferralReward? Reward)[]
    {
        (1, new(2500, 0, null)), (3, new(0, 100, null)), (5, new(0, 0, [new(384, "20 GA Semi-Automatic Shotgun", 1)])), (10, new(10000, 250, [new(3, "3-9x40mm Rifle Scope", 1)])), (20, new(25000, 500, [new(6, ".300 Bolt-Action Rifle", 1), new(11, ".300 Nosler AccuBond Ammunition", 4)])),
    }.Select(t => new ReferralTier(t.Recruits, set ? t.Reward : null, t.Recruits <= reached))];

    public Task<Referral?> GetAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var none = mock == "referral:none";
        var set = mock == "referral:rewards";
        ReferralRecruit[] recruits = none ? [] :
        [
            new("Ranger_Test", 1.75, 8, false, null, false, false),
            new("Elk_Hunter", 1.25, 4, false, null, true, false),
            new("Duck_Caller", 0.5, 9, false, null, false, false),
            new("Fresh_Boots", 0.1, 0, false, null, false, false),
            new("Moss_Walker", 3.5, 25, true, now - 2 * Day, true, set),
            new("Old_Bear", 2.25, 12, true, now - 9 * Day, false, set),
        ];
        var qualified = recruits.Count(r => r.Qualified);
        var canEnter = entered is null;
        var discord = recruits.Count(r => r.DiscordLinked);
        return Task.FromResult<Referral?>(new(Own, $"https://example.com/join/{Own}", canEnter, canEnter ? now + 2 * Day : null, entered, recruiter, Qualification, recruits, qualified, qualified, Tiers(set, qualified),
            new(discord, [new(1, set ? new(1000, 100, null) : null, discord >= 1), new(3, set ? new(0, 250, [new(384, "20 GA Semi-Automatic Shotgun", 1)]) : null, discord >= 3)]), set ? Welcome : null));
    }

    public Task<Result<ReferralEntered>> EnterAsync(string code)
    {
        var c = string.Concat(code.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        if (entered is not null) return Task.FromResult(Result<ReferralEntered>.Fail("Referral.Error.already"));
        var error = c switch { Own => "own", "LOOPLOOP" => "loop", "OLDOLD22" => "expired", "BANBAN22" => "banned", "ZZZZZZZZ" or "" => "unknown", _ => null };
        if (error is not null) return Task.FromResult(Result<ReferralEntered>.Fail("Referral.Error." + error));
        (entered, recruiter) = (c, "Moss_Walker");
        return Task.FromResult(Result<ReferralEntered>.Success(new("Moss_Walker", mock == "referral:rewards" ? Welcome : null)));
    }
}

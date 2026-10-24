namespace Resurgence.Services;

/// <param name="State">active, available, completed or locked</param>
public sealed record Mission(int Id, string Name, string State, string Description, IReadOnlyList<string> Objectives, string Reward, string Reserve, string Species, string Weapon);

/// <param name="Missions">The missions of the pack, shown when its row is opened.</param>
public sealed record MissionPack(int Id, string Name, int Done, int Total, bool Favourite, string Description, IReadOnlyList<Mission> Missions);

public sealed record RegularMissions(IReadOnlyList<Mission> Missions, IReadOnlyList<MissionPack> Packs, int Completed, int Total, int PacksDone, int PacksTotal, bool Auto, bool OnlyFavourite, bool HideCompletedFavourite);

public sealed record DailyDay(int Number, int Stars, int Gms);

/// <param name="Frozen">The streak is broken: the mission is inactive until the player activates it.</param>
public sealed record DailyMission(string Title, string Description, IReadOnlyList<string> Objectives, string Reserve, IReadOnlyList<string> Rewards, bool Frozen);

/// <summary>The item that continues a broken streak and how many of it the player owns.</summary>
public sealed record ResumeItem(int Id, string Name, int Owned);

/// <param name="Today">Number of today's card in the seven-day calendar.</param>
/// <param name="Missions">The mission of each day, in the order of <paramref name="Days"/>.</param>
/// <param name="EndsIn">Seconds until the day ends; the page is read again at 0.</param>
/// <param name="Resume">Null when the player has no resume item to offer (the frozen streak then shows only ACTIVATE).</param>
public sealed record DailyCalendar(int Today, IReadOnlyList<DailyDay> Days, IReadOnlyList<DailyMission> Missions, int EndsIn, ResumeItem? Resume);

public interface IMissionsService
{
    Task<IReadOnlyList<Mission>> ActiveAsync();
    Task<DailyCalendar> DailyAsync();
    Task<RegularMissions> RegularAsync();
    /// <summary>Activates the mission, every available mission (id 0), or the frozen daily streak (id -1). Fake in the mock.</summary>
    Task<Result> ActivateAsync(int id);
    Task<Result> SetFavouriteAsync(int pack, bool favourite);
    /// <param name="setting">auto or onlyfavourite</param>
    Task<Result> SetAutomaticAsync(string setting, bool on);
    Task<Result> SetHideCompletedFavouriteAsync(bool hide);
    /// <summary>Uses the resume item to continue the broken streak; fails when the player owns none.</summary>
    Task<Result> ResumeStreakAsync();
}

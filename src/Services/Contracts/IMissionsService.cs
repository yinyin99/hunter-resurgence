namespace Resurgence.Services;

/// <param name="State">active, available, completed or locked</param>
public sealed record Mission(int Id, string Name, string State, string Description, IReadOnlyList<string> Objectives, string Reward, string Reserve, string Species, string Weapon);

public sealed record MissionPack(int Id, string Name, int Done, int Total, bool Favourite);

public sealed record RegularMissions(IReadOnlyList<Mission> Missions, IReadOnlyList<MissionPack> Packs, int Completed, int Total, int PacksDone, int PacksTotal, bool Auto, bool OnlyFavourite);

public sealed record DailyDay(int Number, int Stars, int Gms);

/// <param name="Frozen">The streak is broken: the mission is inactive until the player activates it.</param>
public sealed record DailyMission(string Title, string Description, IReadOnlyList<string> Objectives, string Reserve, IReadOnlyList<string> Rewards, bool Frozen);

/// <param name="Today">Number of today's card in the seven-day calendar.</param>
public sealed record DailyCalendar(int Today, IReadOnlyList<DailyDay> Days, DailyMission Selected);

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
}

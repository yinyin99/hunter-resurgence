namespace Resurgence.Services;

public sealed record Mission(int Id, string Name, int Progress, int Goal);

public interface IMissionsService
{
    Task<IReadOnlyList<Mission>> DailyAsync();
    Task<IReadOnlyList<Mission>> ActiveAsync();
}

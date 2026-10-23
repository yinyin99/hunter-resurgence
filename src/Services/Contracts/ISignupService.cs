namespace Resurgence.Services;

public sealed record Choice(string Id, string Name);

public interface ISignupService
{
    Task<bool> UsernameAvailableAsync(string name);
    Task<IReadOnlyList<string>> AvatarsAsync();
    Task<IReadOnlyList<Choice>> FirstWeaponsAsync();
    Task<IReadOnlyList<Choice>> LicensesAsync();
    Task<Result> ChangeEmailAsync(string email);
}

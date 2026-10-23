namespace Resurgence.Services;

public interface IAccountService
{
    /// <param name="tab">the account settings tab: avatar, language, email, profile, ...</param>
    Task<Result> SaveAsync(string tab, IReadOnlyDictionary<string, string> values);
}

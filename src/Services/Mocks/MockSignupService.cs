namespace Resurgence.Services.Mocks;

public sealed class MockSignupService : ISignupService
{
    public Task<bool> UsernameAvailableAsync(string name) => Task.FromResult(!name.Equals("taken", StringComparison.OrdinalIgnoreCase));

    public Task<IReadOnlyList<string>> AvatarsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(Enumerable.Range(1, 8).Select(i => $"avatar-{i:00}").ToList());

    public Task<IReadOnlyList<Choice>> FirstWeaponsAsync() =>
        Task.FromResult<IReadOnlyList<Choice>>([new("w1", "Rifle Model A"), new("w2", "Shotgun Model B"), new("w3", "Bow Model C")]);

    public Task<IReadOnlyList<Choice>> LicensesAsync() =>
        Task.FromResult<IReadOnlyList<Choice>>([new("l1", "Licence One"), new("l2", "Licence Two"), new("l3", "Licence Three")]);

    public Task<Result> ChangeEmailAsync(string email) =>
        Task.FromResult(email.Contains('@') ? Result.Success : Result.Fail("Validation.Email.Invalid"));
}

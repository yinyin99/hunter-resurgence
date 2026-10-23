namespace Resurgence.Services.Mocks;

public sealed class MockHelpService : IHelpService
{
    public Task<IReadOnlyList<HelpTopic>> TopicsAsync() =>
        Task.FromResult<IReadOnlyList<HelpTopic>>([new("support", "Support"), new("guides", "Guides"), new("tutorials", "Video tutorials")]);
}

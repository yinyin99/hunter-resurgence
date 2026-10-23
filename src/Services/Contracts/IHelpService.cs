namespace Resurgence.Services;

public sealed record HelpTopic(string Id, string Title);

public interface IHelpService
{
    Task<IReadOnlyList<HelpTopic>> TopicsAsync();
}

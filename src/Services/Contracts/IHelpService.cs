namespace Resurgence.Services;

/// <summary>A community guide; the address comes from the settings (link_guide_n, in list order).</summary>
public sealed record Guide(string Title);
public sealed record Video(string Id, string Title, string Author);
public sealed record TutorialSection(int Id, string Title, string Description, IReadOnlyList<Video> Videos);
/// <summary>A local community website; the address comes from the settings (link_community_n, in list order).</summary>
public sealed record Community(string Country, string Name);

public interface IHelpService
{
    Task<IReadOnlyList<Guide>> GuidesAsync();
    Task<IReadOnlyList<TutorialSection>> TutorialsAsync();
    Task<IReadOnlyList<Community>> CommunitiesAsync();
}

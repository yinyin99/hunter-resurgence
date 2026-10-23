namespace Resurgence.Services.Mocks;

public sealed class MockHelpService : IHelpService
{
    static string H(int n) => Names.Handle(n);

    static readonly Guide[] Guides =
    [
        new($"Hunting Guide by {H(3)}"), new($"Animal Location Maps by {H(10)}"), new($"Simple rules for a successful hunt by {H(21)}"), new($"Scents and callers guide by {H(21)}"),
        new($"Antler Scoring Guide by {H(21)}"), new($"Pheasant Shooting by {H(34)}"), new($"Pheasant Tutorial by {H(10)}"), new($"Bear Baiting by {H(34)}"),
        new($"Roe Deer Hunting by {H(3)}"), new($"Long Range Shooting by {H(10)}"), new($"Coyote Hunting Guide by {H(45)}"),
    ];

    // the first section's text is built by the page: it links to the playable tutorials in the launcher
    static readonly TutorialSection[] Sections =
    [
        new(1, "Basics", "", [new("v1", "Beginner’s Guide", H(10))]),
        new(2, "Mule Deer hunting", "Learn all there is to know about hunting Mule Deer in these tutorials.", [new("v2", "Mule Deer Basics", H(5)), new("v3", "Mule Deer Tracking", H(6)), new("v4", "Mule Deer Shots", H(7))]),
        new(3, "Bear hunting", "Learn more about the unique gameplay involved in hunting and baiting bears.", [new("v5", "Baiting Bears", H(7)), new("v6", "Bear Hunting Tips", H(8)), new("v7", "Bear Hunting Walkthrough", H(9))]),
        new(4, "Deployables", "There are many different kinds of deployable equipment that you can place in the world of {game}. Learn more about what they are and how to use them in these tutorials.", [new("v8", "Tents and Stands", H(11)), new("v9", "Camp Setup", H(12)), new("v10", "Towers", H(13)), new("v11", "Targets", H(14))]),
        new(5, $"Tips, Tricks & Tactics by {H(10)}", "A long time player and community member has put together a great set of tutorials for you to learn more about hunting the different species.",
            [new("v12", "Duck Hunting", H(10)), new("v13", "Feral Hog", H(10)), new("v14", "Where to find Foxes", H(10)), new("v15", "Bowhunting Turkeys", H(10)), new("v16", "Pheasant Long Shots", H(10)), new("v17", "Chasing Black Bears", H(10)), new("v18", "Canada Goose Extended", H(10)), new("v19", "Cottontail Rabbit", H(10))]),
    ];

    static readonly Community[] Communities = [new("Germany", "Community Site DE"), new("Italy", "Community Site IT"), new("Poland", "Community Site PL"), new("Russia", "Community Site RU")];

    public Task<IReadOnlyList<Guide>> GuidesAsync() => Task.FromResult<IReadOnlyList<Guide>>(Guides);
    public Task<IReadOnlyList<TutorialSection>> TutorialsAsync() => Task.FromResult<IReadOnlyList<TutorialSection>>(Sections);
    public Task<IReadOnlyList<Community>> CommunitiesAsync() => Task.FromResult<IReadOnlyList<Community>>(Communities);
}

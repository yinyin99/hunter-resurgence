namespace Resurgence.Services.Mocks;

/// <summary>
/// Conversations kept per circuit (the website) or per run (the launcher). Mock "messages:unread" starts with one unread conversation, "messages:incoming" lets a message
/// arrive five seconds after the start. A hunter named like "nobody" does not exist, "Hunter01" is oneself and a name starting with "error" makes the send fail.
/// </summary>
public sealed class MockMessagesService(string? mock = null) : IMessagesService
{
    static readonly Message[] Seed = [new(false, "Most recent message.", TimeSpan.FromMinutes(1)), new(true, "A reply in the same thread.", TimeSpan.FromHours(1)), new(false, "Placeholder text used only to show the thread layout.", TimeSpan.FromDays(1))];

    readonly List<Conversation> conversations =
        [new("Moss_Walker", "Hey there. I heard you had a...", new(2026, 1, 3), mock == "messages:unread"), new("Ranger_Test", "See you there", new(2026, 1, 1), false)];
    readonly Dictionary<string, List<Message>> threads = [];
    readonly DateTime started = DateTime.UtcNow;
    bool onlyFriends, arrived;

    // a hunter who is not a conversation yet shows the same sample thread as the first two
    List<Message> Thread(string handle) => threads.TryGetValue(handle, out var t) ? t : threads[handle] = [.. Seed];

    void Arrive()
    {
        if (mock != "messages:incoming" || arrived || DateTime.UtcNow - started < TimeSpan.FromSeconds(5)) return;
        arrived = true;
        Thread("Ranger_Test").Insert(0, new(false, "Are you coming tonight?", TimeSpan.Zero));
        Replace("Ranger_Test", c => c with { Last = "Are you coming tonight?", When = DateTime.Now, Unread = true });
    }

    void Replace(string handle, Func<Conversation, Conversation> change)
    {
        var i = conversations.FindIndex(c => c.Handle == handle);
        if (i >= 0) conversations[i] = change(conversations[i]);
    }

    public Task<IReadOnlyList<Conversation>> ConversationsAsync()
    {
        Arrive();
        return Task.FromResult<IReadOnlyList<Conversation>>(conversations.OrderByDescending(c => c.When).ToList());
    }

    public Task<IReadOnlyList<Message>> ThreadAsync(string handle)
    {
        Arrive();
        return Task.FromResult<IReadOnlyList<Message>>(Thread(handle).ToList());
    }

    public Task MarkReadAsync(string handle)
    {
        Replace(handle, c => c with { Unread = false });
        return Task.CompletedTask;
    }

    public Task<Result> SendAsync(string handle, string text)
    {
        if (handle.StartsWith("nobody", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(Result.Fail("Msg.NoUser"));
        if (handle == "Hunter01") return Task.FromResult(Result.Fail("Msg.Self"));
        if (handle.StartsWith("error", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(Result.Fail("Msg.Failed"));
        // the sample thread of a new hunter is not part of a conversation that starts here
        if (!conversations.Any(c => c.Handle == handle)) { threads[handle] = []; conversations.Add(new(handle, text, DateTime.Now, false)); }
        else Replace(handle, c => c with { Last = text, When = DateTime.Now });
        Thread(handle).Insert(0, new(true, text, TimeSpan.Zero));
        return Task.FromResult(Result.Success);
    }

    public Task<bool> OnlyFriendsAsync() => Task.FromResult(onlyFriends);

    public Task SetOnlyFriendsAsync(bool on) { onlyFriends = on; return Task.CompletedTask; }
}

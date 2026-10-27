using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>Message threads (group messages) and the campaign replies of the profile group, on the fake host. Ids and texts are made up.</summary>
public class MessagesTests
{
    static readonly string Tok = new('a', 40);
    const string List = """[{"user_id":101,"read":1},{"user_id":102,"read":0},{"user_id":103,"read":1}]""";
    const string ListPath = "/v1/Chat/conversations", ThreadPath = "/v1/Chat/conversation";

    static string Thread(int total, int messages) => $$"""{"total":{{total}},"messages":[{{string.Join(",", Enumerable.Range(1, messages).Select(i => $$"""{"text":"hello {{i}}"}"""))}}]}""";

    static Fake Host(string list = List, Func<int, string>? thread = null)
    {
        var f = new Fake(Tok);
        f.Raw[ListPath] = Encoding.UTF8.GetBytes(list);
        f.Custom = (path, form) =>
        {
            if (path != ThreadPath) return null;
            var id = int.Parse(System.Text.RegularExpressions.Regex.Match(form, @"user_id=(\d+)").Groups[1].Value);
            return new HttpResponseMessage { Content = new StringContent(thread?.Invoke(id) ?? Thread(2, 2)) };
        };
        return f;
    }

    static async Task<Engine> Run(Fake f, TempDir t, string[] groups, bool resume = true)
    {
        var e = Hosts.Make(f, Path.Combine(t.Path, "claim.key"));
        await e.Run(t.Path, "", () => Tok, new HashSet<string>(groups), resume, false, TestContext.Current.CancellationToken);
        return e;
    }

    static string Dir(TempDir t) => Path.Combine(t.Path, "backup-7");

    static int[] Order(Fake f) => [.. f.Forms.Zip(f.Seen).Where(p => p.Second == ThreadPath).Select(p => int.Parse(System.Text.RegularExpressions.Regex.Match(p.First, @"user_id=(\d+)").Groups[1].Value))];

    [Fact]
    public async Task ListIsSavedFirstAndUnchangedThenThreadsReadFirst()
    {
        using var t = new TempDir("backup-test-msg-");
        var f = Host();
        await Run(f, t, ["messages"]);
        Assert.Equal(List, File.ReadAllText(Path.Combine(Dir(t), "Chat_conversations.json")));
        Assert.Equal(ListPath, f.Seen.First(p => p.StartsWith("/v1/Chat/")));
        Assert.Equal([101, 103, 102], Order(f));
        foreach (var id in new[] { 101, 102, 103 })
            Assert.Equal(Thread(2, 2), File.ReadAllText(Path.Combine(Dir(t), $"Chat_conversation__user_id={id}.json")));
        Assert.True(f.Forms.Where((_, i) => f.Seen[i] == ThreadPath).All(x => x.Contains(Tok)), "threads carry the token");
    }

    [Fact]
    public async Task ResumeSkipsSavedThreadsAndRetriesAFailedOne()
    {
        using var t = new TempDir("backup-test-msg-");
        var f1 = Host();
        var inner = f1.Custom;
        f1.Custom = (p, form) => p == ThreadPath && form.Contains("user_id=103") ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"errorCode\":9}") } : inner!(p, form);
        var e1 = await Run(f1, t, ["messages"]);
        Assert.True(e1.Failed == 1 && !File.Exists(Path.Combine(Dir(t), "Chat_conversation__user_id=103.json")));
        Assert.Equal(1, e1.MessagesOpened); // 102 was opened

        var f2 = Host();
        var e2 = await Run(f2, t, ["messages"]);
        Assert.Equal([103], Order(f2));
        Assert.Equal(0, e2.MessagesOpened);
        Assert.Equal(List, File.ReadAllText(Path.Combine(Dir(t), "Chat_conversations.json")));
        Assert.DoesNotContain(ListPath, f2.Seen); // the list is read from the saved file
    }

    [Fact]
    public async Task OpenedUnreadCountAndItsText()
    {
        using var t = new TempDir("backup-test-msg-");
        var e = await Run(Host(), t, ["messages"]);
        Assert.Equal(1, e.MessagesOpened);
        var text = string.Format(CultureInfo.InvariantCulture, Loc.Rm.GetString("Msg_MessagesOpened", CultureInfo.InvariantCulture)!, e.MessagesOpened);
        Assert.Contains("1 unread conversations were opened", text);

        using var t2 = new TempDir("backup-test-msg-");
        Assert.Equal(0, (await Run(Host("""[{"user_id":101,"read":1}]"""), t2, ["messages"])).MessagesOpened);
        using var t3 = new TempDir("backup-test-msg-");
        var off = Host();
        Assert.Equal(0, (await Run(off, t3, ["profile"])).MessagesOpened);
        Assert.DoesNotContain(ListPath, off.Seen);
    }

    [Fact]
    public async Task IndexFlagsPossiblyCappedThreads()
    {
        using var t = new TempDir("backup-test-msg-");
        await Run(Host(thread: id => id == 102 ? Thread(3, 2) : Thread(2, 2)), t, ["messages"]);
        var idx = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir(t), "messages-index.json")))!;
        Assert.Equal([102], idx["checks"]!["possibly_capped"]!.AsArray().Select(x => (int)x!));
        var row = idx["threads"]!.AsArray().Single(x => (int)x!["user_id"]! == 102)!;
        Assert.Equal((3, 2, 0, "Chat_conversation__user_id=102.json"), ((int)row["total"]!, (int)row["messages"]!, (int)row["read"]!, (string)row["file"]!));
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir(t), "manifest.json")))!["files"]!.AsArray().Single(x => (string)x!["path"]! == "messages-index.json")!;
        Assert.True((bool)m["derived"]!);
    }

    /// <summary>The API sends many numbers as strings: a string total is read, not a crash.</summary>
    [Fact]
    public async Task StringTotalIsReadNotACrash()
    {
        using var t = new TempDir("backup-test-msg-");
        var e = await Run(Host(thread: id => id == 102 ? Thread(3, 2).Replace("\"total\":3", "\"total\":\"3\"") : Thread(2, 2).Replace("\"total\":2", "\"total\":\"2\"")), t, ["messages"]);
        Assert.Equal(0, e.Failed);
        var idx = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir(t), "messages-index.json")))!;
        Assert.Equal([102], idx["checks"]!["possibly_capped"]!.AsArray().Select(x => (int)x!));
        Assert.Equal(2, idx["threads"]!.AsArray().Count(x => (int)x!["total"]! == 2));
    }

    [Fact]
    public async Task IndexOfEqualCountsIsNotFlagged()
    {
        using var t = new TempDir("backup-test-msg-");
        await Run(Host(), t, ["messages"]);
        var idx = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir(t), "messages-index.json")))!;
        Assert.Empty(idx["checks"]!["possibly_capped"]!.AsArray());
    }

    [Fact]
    public async Task EmptyListMakesNoThreadRequestAndNoNote()
    {
        using var t = new TempDir("backup-test-msg-");
        var f = Host("[]");
        var e = await Run(f, t, ["messages"]);
        Assert.DoesNotContain(ThreadPath, f.Seen);
        Assert.Equal(0, e.MessagesOpened);
    }

    [Fact]
    public async Task CampaignRepliesAreSavedOnceEachNullIncluded()
    {
        using var t = new TempDir("backup-test-camp-");
        const string obj = """{"event":{"id":5},"score":{"giftable_points":3,"gifted_points":1},"leaderboard":[]}""";
        var f = new Fake(Tok);
        f.Raw["/v1/Campaign/get_active_campaign"] = Encoding.UTF8.GetBytes("null");
        f.Raw["/v1/Community_Campaign/get_active_community_campaign"] = Encoding.UTF8.GetBytes(obj);
        var e = await Run(f, t, ["profile"]);
        Assert.Equal(1, f.Seen.Count(p => p == "/v1/Campaign/get_active_campaign"));
        Assert.Equal(1, f.Seen.Count(p => p == "/v1/Community_Campaign/get_active_community_campaign"));
        Assert.Equal(0, e.Failed);
        Assert.Equal("null", File.ReadAllText(Path.Combine(Dir(t), "Campaign_get_active_campaign__user_id=7__lang=en.json")));
        Assert.Equal(obj, File.ReadAllText(Path.Combine(Dir(t), "Community_Campaign_get_active_community_campaign__user_id=7__lang=en.json")));
        Assert.DoesNotContain(f.Forms.Where((_, i) => f.Seen[i].Contains("Campaign")), x => x.Contains(Tok));
    }
}

namespace Resurgence.Tests.WebTests;

public class LogoutPageTests
{
    /// <summary>With the community server's session the page must not sign out when it is opened (a link on another site would): only the button's click does. A source check: the repo has no component renderer.</summary>
    [Fact]
    public void TestOpeningTheServerLogoutPageDoesNotSignOut()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "hunter-resurgence.slnx"))) dir = Path.GetDirectoryName(dir)!;
        var page = File.ReadAllText(Path.Combine(dir, "src", "Ui", "Pages", "Browser", "Logout.razor"));
        Assert.Contains("Sessions.SignInUrl(\"/\") is null ? Sessions.SignOutAsync() : Task.CompletedTask", page);
        Assert.Contains("@onclick=\"SignOut\"", page);
        Assert.Equal(2, page.Split("Sessions.SignOutAsync()").Length - 1);  // the mock's load and the click, nowhere else
    }
}

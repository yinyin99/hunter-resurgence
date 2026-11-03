using System.Text;
using Resurgence.Server;

namespace Resurgence.Server.Tests;

public class ReqTests
{
    static Req Make(string query = "", string body = "", params (string, string)[] headers) =>
        new("POST", "api.test", "/p", query, k => headers.FirstOrDefault(h => h.Item1 == k).Item2, Encoding.ASCII.GetBytes(body), false);

    static readonly (string, string) Form = ("Content-Type", "application/x-www-form-urlencoded");

    [Fact]
    public void TheSessionHeaderComesBeforeTheBearerAndTheFormToken() =>
        Assert.Equal(["session", "bearer", "form"],
            Make("", "oauth_access_token=form", Form, ("X-Hun-Session", "session"), ("Authorization", "Bearer bearer")).Tokens());

    [Theory]
    [InlineData("Bearer ")]
    [InlineData("Bearer")]
    public void ABearerWithoutATokenIsNone(string auth) => Assert.Empty(Make("", "", ("Authorization", auth)).Tokens());

    [Fact]
    public void AnEmptyFormTokenIsSkipped() => Assert.Empty(Make("", "oauth_access_token=", Form).Tokens());

    [Fact]
    public void AFormBodyCountsOnlyWithItsContentType() => Assert.Empty(Make("", "oauth_access_token=x").Tokens());

    [Fact]
    public void TheSameKeyInQueryAndBodyGivesBothTokens() =>
        Assert.Equal(["q", "b"], Make("oauth_access_token=q", "oauth_access_token=b", Form).Tokens());
}

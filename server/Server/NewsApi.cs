// The news on the website's host: the public feed and the pictures of its posts. The website's server reads them over SERVER_URL, the launcher on the public domain, so the reverse proxy routes /news here (docs/deploy.md).
// And on localhost only, the admin routes of the same posts (`admin news` of the command line does the same). Online only.
using System.Text.Json.Nodes;
using static Resurgence.Server.Replay;

namespace Resurgence.Server;

public static class NewsApi
{
    static Reply Json(int status, JsonNode body, string cache) =>
        new(status, [("Content-Type", "application/json"), ("Cache-Control", cache), ("X-Content-Type-Options", "nosniff")], Py.DumpBytes(body), "news");

    public static void MapNews(this WebApplication app, Game game)
    {
        var state = game.State!;
        if (!state.Online || app.Services.GetService<News>() is not { } news) return;
        var group = app.MapGroup("/news").RequireHost(game.Web).WithGroupName("game").WithTags("News (web_host)");
        group.MapGet("", (string? lang) => Json(200, news.Feed(lang, (long)state.Clock()), "public, max-age=60"))
            .WithName("NewsFeed").WithSummary("The news posts that show now, in the reader's language")
            .WithDescription("`{posts: [{id, published, pinned, title, body, link, linkText, picture}]}`, pinned posts first, then newest first. `published` is Unix seconds; `title`, `body` (Markdown: paragraphs, bold, italic, links, lists) and `linkText` are in the language `lang` names (`fr`, `pt-BR`), "
                             + "each in English when the post has no text in that language; `link` is an https address or `link:<key>` (a link of the project's own, which the website and the launcher have in settings.json), null for none; `picture` is the path of the post's picture, `slot:<name>` for an asset slot of the website and the launcher, null for the default slide picture. "
                             + "A post shows from its start time and until its end time, if it has one; a hidden post is not listed. Sent with `Cache-Control: public, max-age=60`, so a new post shows within a minute.")
            .Produces(200, contentType: "application/json");
        group.MapGet("/{id:long}/picture", (long id) => news.Picture(id, (long)state.Clock()) is { } picture
                ? new Reply(200, [("Content-Type", picture.Type), ("Cache-Control", "public, max-age=31536000, immutable"), ("X-Content-Type-Options", "nosniff"), ("Content-Security-Policy", "sandbox")], picture.Bytes, "news")
                : Json(404, new JsonObject { ["error"] = "no picture" }, "no-store"))
            .WithName("NewsPicture").WithSummary("The picture of a post that shows now")
            .WithDescription("The file as the operator stored it (JPEG, PNG or WebP), at the path `GET /news` gives; the link's `v` changes with the picture, so it is cached for a year. 404 for a post without a picture, or one that does not show now.")
            .Produces(200).Produces(404, contentType: "application/json");
    }

    static IResult Problem(NewsException e) => TypedResults.Problem(e.Message, statusCode: e.Status, title: e.Status == 404 ? "Unknown post" : "Bad news value");

    static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (NewsException e) { return Problem(e); }
    }

    public static void MapNewsAdmin(this RouteGroupBuilder admin, News news, JsonObject cfg)
    {
        admin.MapGet("/news", () => TypedResults.Ok(news.List()))
            .WithName("GetNews").WithSummary("Every news post, newest first, with its texts")
            .WithDescription("`[{id, published, until, link, picture, pinned, hidden, updated, texts: {<language>: {title, body, linkText}}}]`: times in Unix seconds, `picture` the extension of the stored file (null: the default slide picture), hidden and scheduled posts too.");

        admin.MapPost("/news", async Task<IResult> (NewsPatch post, bool? discord) =>
            {
                try
                {
                    var made = news.Get(news.Add(post))!;
                    if (discord != true) return TypedResults.Ok(made);
                    return await NewsCommand.Announce(made, cfg, (long)Store.Now()) is { } why
                        ? TypedResults.Problem($"post {made.Id} is saved, but not posted to Discord: {why}", statusCode: 502, title: "Not posted to Discord")
                        : TypedResults.Ok(made);
                }
                catch (NewsException e) { return Problem(e); }
            })
            .WithName("AddNews").WithSummary("Publish a post: its English text and its settings")
            .WithDescription("`title` and `body` (Markdown) are the English text; `link` (an https address or `link:<key>`) needs its `linkText`; `at` is when it starts to show (an ISO time, default now), `until` when it stops, `pinned` shows it above the newer posts. "
                             + "`discord=true` also posts it to the announcements channel (setting `news_webhook`) when it shows now. A picture is added with `PUT /admin/news/<id>/picture`. The post's other languages: `PUT /admin/news/<id>/lang/<code>`. Returns the post.")
            .ProducesProblem(400).ProducesProblem(502);

        admin.MapPatch("/news/{id:long}", (long id, NewsPatch change) => Run(() =>
            {
                news.Edit(id, change);
                return TypedResults.Ok(news.Get(id));
            }))
            .WithName("EditNews").WithSummary("Change a post's English text or settings")
            .WithDescription("Only the fields sent change; an empty `link`, `linkText` or `until` takes it away. `hidden` true takes the post off, false brings it back. Returns the post.")
            .ProducesProblem(400).ProducesProblem(404);

        admin.MapPut("/news/{id:long}/lang/{code}", (long id, string code, NewsText text) => Run(() =>
            {
                news.SetText(id, code, text.Title, text.Body, text.LinkText);
                return TypedResults.Ok(news.Get(id));
            }))
            .WithName("SetNewsText").WithSummary("Add or replace a post's text in a language (`fr`, `pt-BR`)")
            .WithDescription("`title` and `body` are both needed; without a `linkText` the English one is shown. Returns the post.")
            .ProducesProblem(400).ProducesProblem(404);

        admin.MapPut("/news/{id:long}/picture", async Task<IResult> (long id, HttpRequest request) =>
            {
                using var bytes = new MemoryStream();
                await request.Body.CopyToAsync(bytes);
                return Run(() =>
                {
                    news.SetPicture(id, bytes.ToArray());
                    return TypedResults.Ok(news.Get(id));
                });
            })
            .WithName("SetNewsPicture").WithSummary("Store a post's picture")
            .WithDescription($"The request body is the picture, whatever its `Content-Type`: a JPEG, PNG or WebP file of at most {News.MaxPictureBytes >> 20} MB, checked by its first bytes and stored as it is, never resized. Returns the post.")
            .ProducesProblem(400).ProducesProblem(404);

        admin.MapPut("/news/{id:long}/picture-slot/{slot}", (long id, string slot) => Run(() =>
            {
                news.SetPictureSlot(id, slot);
                return TypedResults.Ok(news.Get(id));
            }))
            .WithName("SetNewsPictureSlot").WithSummary("Give a post the picture of an asset slot of the website and the launcher (`competition-hero-170039`)")
            .WithDescription("The operator's file for that slot (docs/images.md), drawn as the feed drew its pictures before; a stored picture is taken away. Returns the post.")
            .ProducesProblem(400).ProducesProblem(404);

        admin.MapDelete("/news/{id:long}/picture", (long id) => Run(() =>
            {
                news.ClearPicture(id);
                return TypedResults.Ok(news.Get(id));
            }))
            .WithName("ClearNewsPicture").WithSummary("Take a post's picture away: it shows the default slide picture")
            .ProducesProblem(404);
    }
}

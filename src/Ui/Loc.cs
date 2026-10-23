using Microsoft.AspNetCore.Components;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System.Text.RegularExpressions;

namespace Resurgence.Ui;

/// <summary>The IStringLocalizer pages inject: the resx text with the game-name tokens expanded.</summary>
public sealed class Loc(IStringLocalizerFactory factory, Branding branding) : IStringLocalizer<Strings>
{
    readonly IStringLocalizer inner = factory.Create(typeof(Strings));

    public LocalizedString this[string name] => Expand(inner[name]);

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            // tokens first: {game} is not a valid string.Format hole
            var s = inner[name];
            return s.ResourceNotFound ? s : new(name, string.Format(CultureInfo.CurrentCulture, branding.Expand(s.Value), arguments), false);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => inner.GetAllStrings(includeParentCultures).Select(Expand);

    LocalizedString Expand(LocalizedString s) => new(s.Name, branding.Expand(s.Value), s.ResourceNotFound, s.SearchedLocation);
}

public static class UiLocalization
{
    /// <summary>Localisation with the game name and links of the settings.json at <paramref name="settingsPath"/>.</summary>
    public static IServiceCollection AddUiLocalization(this IServiceCollection services, string settingsPath) => services
        .AddLocalization()
        .AddSingleton(Branding.Load(settingsPath))
        .AddSingleton<IStringLocalizer<Strings>, Loc>();
}

public static class LocHtml
{
    /// <summary>A text with markup holes: the text is HTML-encoded, then {0}, {1}, ... become the given tags (a translator moves the holes, never writes markup).</summary>
    public static MarkupString Html(this IStringLocalizer l, string key, params string[] tags) =>
        new(string.Format(CultureInfo.CurrentCulture, System.Net.WebUtility.HtmlEncode(l[key].Value), tags));

    /// <summary>A plain text whose {0}, {1}, ... holes are strings or components (a link that falls back to plain text, a handle); the holes may move in a translation.</summary>
    public static RenderFragment Frag(this IStringLocalizer l, string key, params object[] holes) => b =>
    {
        var text = l[key].Value;
        var at = 0;
        foreach (var m in Regex.Matches(text, @"\{(\d+)\}").Cast<Match>())
        {
            b.AddContent(0, text[at..m.Index]);
            var hole = holes[int.Parse(m.Groups[1].Value)];
            if (hole is RenderFragment f) b.AddContent(1, f); else b.AddContent(2, hole.ToString());
            at = m.Index + m.Length;
        }
        b.AddContent(3, text[at..]);
    };
}

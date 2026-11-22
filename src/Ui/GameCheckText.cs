using Microsoft.Extensions.Localization;
using Resurgence.Services;

namespace Resurgence.Ui;

/// <summary>How the pages show an <see cref="InstallCheck"/> (docs/website.md#the-game-check): its text, the first problem, the label of its fix button.</summary>
public static class GameCheckText
{
    /// <summary>The text of a check's key with its values in the order of the {0}, {1} holes; the two spare values keep a text whose values did not come from the check from failing.</summary>
    public static string Text(this IStringLocalizer l, string key, IReadOnlyDictionary<string, string>? args) =>
        l[key, [.. args?.Values ?? Array.Empty<string>(), "", ""]].Value;

    public static string Text(this IStringLocalizer l, InstallCheck c) => l.Text(c.Text, c.Args);

    /// <summary>The result that tops the dashboard line: the first blocking one, else the first warning, else null when all is well.</summary>
    public static InstallCheck? FirstProblem(IReadOnlyList<InstallCheck> checks) =>
        checks.FirstOrDefault(c => c.Level == "block") ?? checks.FirstOrDefault(c => c.Level == "warn");

    /// <summary>The fix button's text key, or null when the fix has no button (close-game: the player closes the game).</summary>
    public static string? FixLabel(string? fix) => fix switch
    {
        "choose-folder" => "GameFolder.Choose",
        "open-folder" => "GameFolder.Open",
        "open-steam" or "verify-files" => "Check.OpenSteam",
        "setup" => "Kit.SetUp",
        "fix-display" => "Check.FixDisplay",
        _ => null,
    };
}

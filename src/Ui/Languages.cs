namespace Resurgence.Ui;

/// <summary>The interface languages: one list for the launcher, the website and the pickers. Code is what is stored (launcher.json, ?lang=) and is also the culture of its Strings.&lt;code&gt;.resx.</summary>
public static class Languages
{
    public sealed record Language(string Code, string Name, string Flag);

    public static readonly Language[] All =
    [
        new("en", "English", "en"), new("fr", "Français", "fr"), new("de", "Deutsch", "de"),
        new("es", "Español", "es"), new("pt-BR", "Português (Brasil)", "br"), new("ru", "Русский", "ru"),
        new("it", "Italiano", "it"), new("nl", "Nederlands", "nl"), new("pl", "Polski", "pl"), new("pt-PT", "Português (Portugal)", "pt"), new("sv", "Svenska", "se"), new("da", "Dansk", "dk"), new("nb", "Norsk (bokmål)", "no"),
        new("fi", "Suomi", "fi"), new("cs", "Čeština", "cz"), new("hu", "Magyar", "hu"), new("ro", "Română", "ro"), new("sl", "Slovenščina", "si"), new("tr", "Türkçe", "tr"),
    ];

    public static Language? Find(string? code) => All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The language of a culture or language tag ("de-AT" is German, bare "pt" is Brazilian Portuguese, "no"/"nn" are Norwegian bokmål); null when none fits.</summary>
    public static Language? Match(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        var primary = tag.Split('-', '_')[0];
        if (primary is "no" or "nn") primary = "nb";
        return Find(tag) ?? All.FirstOrDefault(l => l.Code.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase));
    }
}

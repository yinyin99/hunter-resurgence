using Microsoft.AspNetCore.Components;
using Resurgence.Ui;

namespace Resurgence.Web;

/// <summary>The language travels as ?lang=, which the server turns into the culture cookie; the page is reloaded.</summary>
public sealed class WebLanguageSwitcher(NavigationManager nav) : ILanguageSwitcher
{
    public void Set(string code) => nav.NavigateTo(nav.GetUriWithQueryParameter("lang", code), forceLoad: true);
}

namespace Resurgence.Ui;

/// <summary>Switches the interface language; each host stores the choice its own way and reloads the page.</summary>
public interface ILanguageSwitcher
{
    void Set(string code);
}

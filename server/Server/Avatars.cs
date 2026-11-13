// Player avatars: the picture of an account is one of the game's avatars (`avatar_url` is `<base>/256/<id>.png`, `face` the id) or the one imported from its backup
// (`photos/<account>/avatar.jpg`, served at the photo link). What a page is told of it, and the player's own choice.
using System.Text.Json.Nodes;
using Resurgence.Restore;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The picture imported from a backup, in the account's photo folder: a stable name, so the link does not depend on where the picture came from.</summary>
    public const string ImportedAvatar = "avatar.jpg";

    /// <summary>The ids of the game's avatars, in the order of the recorded list (empty without one).</summary>
    public List<long> AvatarIds => [.. avatars.OfType<JsonObject>().Select(a => a["id"]).Where(n => Py.IsNum(n)).Select(n => Py.L(n))];

    bool IsListedAvatar(string? url) => new ItemCatalogue(items, avatars).AvatarId(url) is not null;

    string ListedAvatarUrl(long id) => $"{avatarBase}/256/{id}.png";

    bool HasImportedAvatar(long account) => File.Exists(Path.Combine(AccountPhotos(account), ImportedAvatar));

    /// <summary>The picture of the zip's avatar when it is the player's own (an address that is no avatar of the game's list) and the zip holds it; null otherwise.</summary>
    internal MediaFile? ImportedAvatarOf(Restored read) =>
        read.Model.Profile.AvatarUrl is { } url && !IsListedAvatar(url) && Importer.MediaPath("images", url) is { } path
            ? read.Media.FirstOrDefault(m => m.Path == path) : null;

    /// <summary>The `avatar_url` an account has after a restore: the zip's address for an avatar of the game's list, the imported picture's link when the zip holds it, else the default avatar.</summary>
    internal string RestoredAvatarUrl(Restored read, long account) =>
        read.Model.Profile.AvatarUrl is { } url && IsListedAvatar(url) ? url
        : ImportedAvatarOf(read) is not null ? PhotoUrl(account, ImportedAvatar) : ListedAvatarUrl(DefaultAvatar);

    /// <summary>The avatar of an account as a page draws it: a URL on `site` (the website's) for the imported picture, the game's address for an avatar of the list; null for anything else
    /// (an address that is none of the list, an imported picture that is gone), where the page shows its default.</summary>
    public string? AvatarUrl(long account, string site)
    {
        if (Store.AvatarUrl(account) is not { Length: > 0 } url) return null;
        if (url.EndsWith($"{PHOTO_PREFIX}{account}/{ImportedAvatar}", StringComparison.Ordinal))
            return HasImportedAvatar(account) ? PhotoUrl(account, ImportedAvatar, site) : null;
        return IsListedAvatar(url) ? url : null;
    }

    /// <summary>`GET /account/avatars`: `{current, ids, imported}`: the player's avatar (<see cref="AvatarUrl"/>), the ids of the game's avatars and the imported picture's link (null without one).</summary>
    public JsonObject AvatarChoices(long account, string site) => new()
    {
        ["current"] = AvatarUrl(account, site), ["ids"] = new JsonArray([.. AvatarIds.Select(id => (JsonNode)id)]),
        ["imported"] = HasImportedAvatar(account) ? PhotoUrl(account, ImportedAvatar, site) : null,
    };

    /// <summary>`POST /account/avatar`: the player's avatar becomes the game's avatar `id`, or the imported picture for null. False, and nothing changed, for an id that is not in the list
    /// or for no imported picture. It writes what a restore does: `avatar_url`, `face` (the default avatar for the imported picture), `avatar` and `avatar_id` where the account has them.</summary>
    public bool SetAvatar(long account, long? id)
    {
        if (id is null ? !HasImportedAvatar(account) : !AvatarIds.Contains(id.Value)) return false;
        Store.SetAvatar(account, id is { } listed ? ListedAvatarUrl(listed) : PhotoUrl(account, ImportedAvatar), id ?? DefaultAvatar);
        return true;
    }

    /// <summary>Once, at start: an account whose `avatar_url` is the address of a picture the player uploaded on the official site, and whose restored copy is in its photo folder,
    /// gets that picture as its imported avatar. Its other values stay, so the game and the website show the picture as after a restore.</summary>
    public void MigrateAvatars()
    {
        foreach (var (account, url) in Store.AvatarUrls())
            if (!IsListedAvatar(url) && Importer.MediaPath("images", url) is { } path
                && Path.Combine(AccountPhotos(account), Importer.CleanName(path)) is var file && File.Exists(file))
            {
                File.Copy(file, Path.Combine(AccountPhotos(account), ImportedAvatar), overwrite: true);
                Store.SetAvatar(account, PhotoUrl(account, ImportedAvatar), null);
            }
    }
}

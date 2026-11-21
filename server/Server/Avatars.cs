// Player avatars: the picture of an account is one of the game's avatars (`avatar_url` is this server's `<game host>/media/avatar/256/<id>.png`, `face` the 3D head of the same number: <see cref="FaceOf"/>) or the one imported from its backup
// (`photos/<account>/avatar.jpg`, served at the photo link). What a page is told of it, and the player's own choice.
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Resurgence.Restore;

namespace Resurgence.Server;

public sealed partial class State
{
    /// <summary>The picture imported from a backup, in the account's photo folder: a stable name, so the link does not depend on where the picture came from.</summary>
    public const string ImportedAvatar = "avatar.jpg";

    /// <summary>The ids of the game's avatars, in the order of the recorded list (empty without one).</summary>
    public List<long> AvatarIds => [.. avatars.OfType<JsonObject>().Select(a => a["id"]).Where(n => Py.IsNum(n)).Select(n => Py.L(n))];

    /// <summary>The avatars of the list: id, the catalogue item that unlocks it (null: free) and name.</summary>
    List<(long Id, long? Item, string? Name)> AvatarEntries => [.. avatars.OfType<JsonObject>().Where(a => Py.IsNum(a["id"]))
        .Select(a => (Py.L(a["id"]), Py.IsNum(a["unlock_item_id"]) ? Py.L(a["unlock_item_id"]) : (long?)null, a["name"] is JsonValue n && n.TryGetValue<string>(out var s) ? s : null))];

    /// <summary>The ids of the catalogue items the account owns.</summary>
    HashSet<long> OwnedItemIds(long account)
    {
        using var con = Con(account);
        return [.. con.Rows("SELECT DISTINCT item_id FROM items WHERE account_id = $a").Select(r => (long)r[0]!)];
    }

    /// <summary>The game's `gender` of a listed avatar (`m` is 1, `f` is 0); null for an avatar the list lacks or gives none.</summary>
    long? AvatarGender(long id) => avatars.OfType<JsonObject>().FirstOrDefault(a => a["id"] is JsonValue v && v.TryGetValue<long>(out var n) && n == id)?["gender"] is JsonValue g
        && g.TryGetValue<string>(out var s) ? s switch { "m" => 1, "f" => 0, _ => null } : null;

    bool IsListedAvatar(string? url) => new ItemCatalogue(items, avatars).AvatarId(url) is not null;

    /// <summary>The game's pictures next to the avatars (the notice icon, the notice sender's avatar) and the stock avatars: on this server's own game host, never the original's.</summary>
    string MediaUrl(string path) => $"{baseUrl}/media/avatar/{path}";

    /// <summary>The id of the notice sender's picture: not an avatar of the list, but served beside them (`/media/avatar/256/10000000.png`).</summary>
    public const long SenderAvatar = 10000000;

    string ListedAvatarUrl(long id) => MediaUrl($"256/{id}.png");

    bool HasImportedAvatar(long account) => File.Exists(Path.Combine(AccountPhotos(account), ImportedAvatar));

    /// <summary>How the name of the backup's copy of the player's own picture ends (<see cref="Importer.MediaPath"/>). An uploaded picture has two addresses on different hosts
    /// (the profile's and the friends lists'), and the backup tool saved it under one of them: for such an address only its path from `/uploads/` on counts.</summary>
    static string? ImportedAvatarEnd(string url) => Importer.MediaPath("images", url) is { } path
        ? path.IndexOf("_uploads_", StringComparison.Ordinal) is var i and >= 0 ? path[i..] : path : null;

    /// <summary>The picture of the zip's avatar when it is the player's own (an address that is no avatar of the game's list) and the zip holds it; null otherwise.</summary>
    internal MediaFile? ImportedAvatarOf(Restored read) =>
        read.Model.Profile.AvatarUrl is { } url && !IsListedAvatar(url) && ImportedAvatarEnd(url) is { } end
            ? read.Media.FirstOrDefault(m => m.Path.StartsWith("images/", StringComparison.Ordinal) && m.Path.EndsWith(end, StringComparison.Ordinal)) : null;

    /// <summary>The numeric user id an uploaded-avatar link names (`.../uploads/thumb/242x242/<uid>.jpg?<hash>`); null for any other address.</summary>
    static string? UploadUid(string url) => Regex.Match(url.Split('?')[0], @"/uploads/(?:.*/)?(\d+)\.[A-Za-z]+$") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Where the player's uploaded avatar still is: the avatar host (`avatar_uploads`, else the game's avatar list's own base) serves it by user id, whatever host the saved link named.</summary>
    /// <summary>The settings' `avatar_uploads`: the base the uploaded avatars are fetched from; null: the avatar list's own base.</summary>
    public string? AvatarUploads { get; init; }

    string AvatarFetchUrl(string uid) => $"{AvatarUploads ?? avatarBase}/uploads/thumb/242x242/{uid}.jpg";

    /// <summary>The address to fetch the player's own avatar from when the zip has none of it: the saved link is an uploaded-avatar one (its uid), or the save has no avatar at all (the zip's user id);
    /// null when the zip holds the picture, or the save names a stock avatar or anything else (the player chose it).</summary>
    internal string? UploadedAvatarLink(Restored read)
    {
        if (ImportedAvatarOf(read) is not null) return null;
        var url = read.Model.Profile.AvatarUrl;
        var uid = url is null ? read.Model.Identity.Uid : !IsListedAvatar(url) ? UploadUid(url) : null;
        return uid is null ? null : AvatarFetchUrl(uid);
    }

    /// <summary>Was the player's own avatar among the pictures fetched for the restore?</summary>
    internal bool AvatarFetched(Restored read, PictureHashes hashes) =>
        UploadedAvatarLink(read) is { } link && hashes.Entries.ContainsKey(PictureLinks.EntryName(link));

    /// <summary>The `avatar_url` an account has after a restore: the zip's address for an avatar of the game's list, the imported picture's link when the zip holds it or it was fetched (`fetched`), else the default avatar.</summary>
    internal string RestoredAvatarUrl(Restored read, long account, bool fetched = false) =>
        new ItemCatalogue(items, avatars).AvatarId(read.Model.Profile.AvatarUrl) is { } id ? ListedAvatarUrl(id)
        : fetched || ImportedAvatarOf(read) is not null ? PhotoUrl(account, ImportedAvatar) : ListedAvatarUrl(DefaultAvatar);

    /// <summary>The avatar of an account as a page draws it: a URL on `site` (the website's) for the imported picture, the website's copy `<site>/images/avatars/256/<id>.png` for an avatar of the list
    /// (the game host is the game's: a `game` reply, the in-game board pages, keeps it); null for anything else (an address that is none of the list, an imported picture that is gone), where the page shows its default.</summary>
    public string? AvatarUrl(long account, string site, bool game = false)
    {
        if (Store.AvatarUrl(account) is not { Length: > 0 } url) return null;
        if (url.EndsWith($"{PHOTO_PREFIX}{account}/{ImportedAvatar}", StringComparison.Ordinal))
            return HasImportedAvatar(account) ? PhotoUrl(account, ImportedAvatar, site) : null;
        return new ItemCatalogue(items, avatars).AvatarId(url) is { } id ? game ? ListedAvatarUrl(id) : $"{site}/images/avatars/256/{id}.png" : null;
    }

    /// <summary>The list's avatars the account can choose (key true: the free ones and those whose item it owns) and the others (key false: locked).</summary>
    ILookup<bool, (long Id, long? Item, string? Name)> AvatarsByUnlock(long account)
    {
        var owned = OwnedItemIds(account);
        return AvatarEntries.ToLookup(e => e.Item is not { } item || owned.Contains(item));
    }

    /// <summary>`GET /account/avatars`: `{current, ids, locked, imported}`: the player's avatar (<see cref="AvatarUrl"/>), the ids of the game's avatars it can choose (the free ones, and a store avatar once the account owns its item),
    /// the others as `[{id, item, name}]` (`item` the store item that unlocks it) and the imported picture's link (null without one).</summary>
    public JsonObject AvatarChoices(long account, string site)
    {
        var parts = AvatarsByUnlock(account);
        return new()
        {
            ["current"] = AvatarUrl(account, site), ["ids"] = new JsonArray([.. parts[true].Select(e => (JsonNode)e.Id)]),
            ["locked"] = new JsonArray([.. parts[false].Select(e => (JsonNode)new JsonObject { ["id"] = e.Id, ["item"] = e.Item, ["name"] = e.Name })]),
            ["imported"] = HasImportedAvatar(account) ? PhotoUrl(account, ImportedAvatar, site) : null,
        };
    }

    /// <summary>`Avatar/getAvatars`: the list with each avatar's `unlocked` true for a free one and for a store avatar whose item the account owns (null: nobody signed in, every store avatar is locked); null without a list.</summary>
    public byte[]? AvatarListReply(long? account)
    {
        if (avatars.Count == 0) return null;
        var owned = account is { } a ? OwnedItemIds(a) : [];
        var list = (JsonArray)avatars.DeepClone();
        foreach (var entry in list.OfType<JsonObject>())
            entry["unlocked"] = !Py.IsNum(entry["unlock_item_id"]) || owned.Contains(Py.L(entry["unlock_item_id"]));
        return Py.DumpBytes(list);
    }

    /// <summary>`POST /account/avatar`: the player's avatar becomes the game's avatar `id`, or the imported picture for null. False, and nothing changed, for an id that is not in the list, a store avatar whose item the account does not own
    /// or for no imported picture. A listed avatar also sets `gender` from its list entry; the imported picture keeps it. It writes what a restore does: `avatar_url`, `face` (a 3D face: the picture's head, or the default one of the gender for the imported picture), `avatar` and `avatar_id` where the account has them.</summary>
    public bool SetAvatar(long account, long? id)
    {
        if (id is null ? !HasImportedAvatar(account) : !AvatarsByUnlock(account)[true].Any(e => e.Id == id.Value)) return false;
        var url = id is { } listed ? ListedAvatarUrl(listed) : PhotoUrl(account, ImportedAvatar);
        var (face, picture, gender) = AvatarFace(id, GenderOf(account));
        Store.SetAvatar(account, url, face, gender, picture);
        return true;
    }

    long GenderOf(long account) => Store.AccountJson(account)["gender"] is { } g && Py.IsNum(g) ? Py.L(g) : 0;

    /// <summary>The 3D face of a listed picture: the first series' head with the same number (`1002007` is `1000007`), the same head and gender.</summary>
    public static long FaceOf(long picture) => DefaultAvatar + picture % 1000;

    /// <summary>The default 3D face of a body: the first head of the list with that gender (the male default when the list has none).</summary>
    long DefaultFace(long gender) => AvatarIds.Where(i => i is >= DefaultAvatar and < DefaultAvatar + 18 && AvatarGender(i) == gender).Select(i => (long?)i).FirstOrDefault() ?? DefaultAvatar;

    /// <summary>What an avatar writes: the `face` (always a 3D face), the picture id for `avatar`/`avatar_id` and the `gender`. A listed picture gives its head, its id and its gender (`gender` when the list has none);
    /// null (the imported picture, the default) keeps the body's `gender` and gets that gender's default face, also as its picture id.</summary>
    internal (long Face, long Picture, long Gender) AvatarFace(long? listed, long gender) =>
        listed is { } id ? (FaceOf(id), id, AvatarGender(id) ?? gender) : (DefaultFace(gender), DefaultFace(gender), gender);

    /// <summary>The listed picture the zip's avatar is, null for the player's own or none.</summary>
    internal long? ListedAvatarOf(Restored read) => new ItemCatalogue(items, avatars).AvatarId(read.Model.Profile.AvatarUrl);

    /// <summary>Once, at start: an account whose `avatar_url` is the address of a picture the player uploaded on the official site, and whose restored copy is in its photo folder,
    /// gets that picture as its imported avatar. Its other values stay, so the game and the website show the picture as after a restore.</summary>
    public void MigrateAvatars()
    {
        foreach (var (account, url) in Store.AvatarUrls())
            if (!IsListedAvatar(url) && ImportedAvatarEnd(url) is { } end && Directory.Exists(AccountPhotos(account))
                && Directory.EnumerateFiles(AccountPhotos(account), "images_*").FirstOrDefault(f => Path.GetFileName(f).EndsWith(Importer.CleanName(end), StringComparison.Ordinal)) is { } file)
            {
                File.Copy(file, Path.Combine(AccountPhotos(account), ImportedAvatar), overwrite: true);
                Store.SetAvatar(account, PhotoUrl(account, ImportedAvatar), null);
            }
    }

    /// <summary>Once, at start: an account whose `avatar_url` is an avatar of the list under another address (the original's avatar host, which the game no longer reaches) gets this server's address
    /// (<see cref="ListedAvatarUrl"/>) in its `avatar_url` and its restored profile's `avatar`. Its other values stay. Idempotent: a second run finds none.</summary>
    public void MigrateListedAvatarUrls()
    {
        foreach (var (account, url) in Store.AvatarUrls())
            if (new ItemCatalogue(items, avatars).AvatarId(url) is { } id && ListedAvatarUrl(id) is var ours && url != ours)
                Store.SetAvatar(account, ours, null);
    }

    /// <summary>Once, at start: an account that never restored anything and whose `gender` disagrees with its listed avatar gets the avatar's gender. Idempotent: a second run finds none.</summary>
    public void MigrateAvatarGenders()
    {
        foreach (var (account, url) in Store.AvatarUrls())
            if (new ItemCatalogue(items, avatars).AvatarId(url) is { } id && AvatarGender(id) is { } gender)
                Store.FixGenderUnlessRestored(account, gender);
    }

    /// <summary>`admin faces`: every account's `face` is a 3D face of its body's gender. An account whose `avatar_url` is a listed picture takes that picture's head (<see cref="FaceOf"/>) and gender (`face` when the address is none: the old
    /// default face of an account with its own picture was a server fill-in, never a choice); any other account (its own picture, the default avatar every account starts with: no choice) keeps its gender and keeps its face when that is a 3D face of this gender (the store's head too),
    /// else gets the default face of the gender. `apply` false: nothing changes. Returns the counts line. Idempotent: the writes keep face and gender in step, so a second run finds none.</summary>
    public string FixFaces(bool apply)
    {
        var heads = new HashSet<long>(Enumerable.Range(0, 18).Select(i => DefaultAvatar + i)) { 1000024 };  // the game's 3D faces: the 18 heads and the store's Laura
        int genders = 0, faces = 0;
        foreach (var (id, url, face, gender) in Store.FacesAndGenders())
        {
            if (face is not { } f) continue;
            var g = gender ?? 0;
            long? listed = new ItemCatalogue(items, avatars).AvatarId(url) ?? (url is null && AvatarIds.Contains(f) ? f : (long?)null);
            if (listed == DefaultAvatar) listed = null;  // the address every account starts with (and a restore without a picture writes) is no choice: the gender stays
            var (newFace, newGender) = listed is { } picture ? (FaceOf(picture), AvatarGender(picture) ?? g)
                : heads.Contains(f) && (AvatarGender(f) ?? g) == g ? (f, g) : (DefaultFace(g), g);
            if (newGender != g) genders++;
            if (newFace != f) faces++;
            if (apply && (newGender != g || newFace != f)) Store.SetFaceAndGender(id, newFace, newGender);
        }
        return $"{(apply ? "fixed" : "would fix")}: {genders} body(ies) of another gender than their face, {faces} face(s) that are no 3D face of their body";
    }

    /// <summary>The avatar links the account's restored data holds: `avatar` of its profile and of the saved Me/me and Public_user/getByHostname replies. Empty: the data names no avatar at all.</summary>
    List<string> SavedAvatarLinks(long account)
    {
        using var con = Con(account);
        return [.. con.Rows("SELECT body FROM profile WHERE account_id = $a UNION ALL SELECT body FROM extras WHERE account_id = $a AND (path = 'Me_me.json' OR path LIKE 'Public!_user!_getByHostname%' ESCAPE '!')")
            .Select(r => JsonNode.Parse((string)r[0]) as JsonObject).Select(o => o?["avatar"]).Where(Py.IsStr).Select(n => (string)n!).Where(u => u.Length > 0).Distinct()];
    }

    /// <summary>`admin avatars`: for each account (or `only`) whose avatar is the default or a dead upload link, find the uploaded avatar its restored data names (the default avatar, which a restore writes, is no choice and never names one; with no other avatar in it, the archived
    /// user id of its claim), download it (<see cref="PictureLinks"/>) and set it as after a restore. A stored picture and a stock avatar other than the default are never touched.
    /// One line per account: id, handle, result. `dry`: nothing is fetched or changed; the line says what would be tried.</summary>
    public async Task<List<string>> SweepAvatars(PictureLinks links, long? only, bool dry)
    {
        var lines = new List<string>();
        foreach (var (account, url) in Store.AvatarUrls().Where(a => only is null || a.Id == only))
        {
            if (new ItemCatalogue(items, avatars).AvatarId(url) is { } stock ? stock != DefaultAvatar : !url.Contains("/uploads/", StringComparison.Ordinal)) continue;
            var saved = SavedAvatarLinks(account);
            if (UploadUid(url) is not null) saved.Add(url);
            saved.RemoveAll(u => new ItemCatalogue(items, avatars).AvatarId(u) == DefaultAvatar);  // the stock avatar every account starts with (and a restore writes) is no choice
            string? uid = null;
            if (saved.Any(IsListedAvatar)) { }  // the player chose another stock avatar: no fetch
            else if (saved.Select(UploadUid).FirstOrDefault(u => u is not null) is { } named) uid = named;
            else if (saved.Count == 0) uid = Store.ArchivedUid(account)?.ToString();
            var handle = (string)Store.AccountJson(account)["handle"]!;
            string result;
            if (uid is null) result = "no uploaded avatar";
            else if (dry) result = $"would fetch the avatar of user {uid}";
            else if (await links.Download(AvatarFetchUrl(uid)) is not { } bytes) result = "fetch failed";
            else
            {
                Directory.CreateDirectory(AccountPhotos(account));
                File.WriteAllBytes(Path.Combine(AccountPhotos(account), ImportedAvatar), bytes.ToArray());
                Store.SetAvatar(account, PhotoUrl(account, ImportedAvatar), DefaultFace(GenderOf(account)), null);
                result = "set";
            }
            lines.Add($"{account} {handle} {result}");
        }
        return lines;
    }
}

using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The account's `face` is always a 3D head (`1000000 + id mod 1000`) of the body's gender; the picture id stays in `avatar`/`avatar_id`. Invented list: 1000007 and its pictures are male, 1000009 and its pictures female.</summary>
public class AvatarFaceTests : StateTestBase
{
    public AvatarFaceTests() => state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES,
        avatars: (JsonArray)JsonNode.Parse("""[{"id": 1000000, "url": "https://avatar.test/256/1000000.png", "gender": "m"}, {"id": 1000002, "url": "https://avatar.test/256/1000002.png", "gender": "m"}, {"id": 1000007, "url": "https://avatar.test/256/1000007.png", "gender": "m"}, {"id": 1000009, "url": "https://avatar.test/256/1000009.png", "gender": "f"}, {"id": 1001007, "url": "https://avatar.test/256/1001007.png", "gender": "m"}, {"id": 1002007, "url": "https://avatar.test/256/1002007.png", "gender": "m"}, {"id": 1002009, "url": "https://avatar.test/256/1002009.png", "gender": "f"}]""")!);

    long Of(long account, string key) => (long)state.Store.AccountJson(account)[key]!;

    void Set(long account, long face, long gender, string? url = null)
    {
        using var con = state.Store.Open();
        con.Exec("UPDATE accounts SET account = json_set(account, '$.face', ?, '$.gender', ?, '$.avatar_url', ?) WHERE id = ?", face, gender, url ?? $"{BASE}/media/avatar/256/{face}.png", account);
    }

    [Fact]
    public void AListedPictureGivesItsHeadAndGenderAndKeepsThePicture()
    {
        state.CreateAccount(50, "a");
        Assert.True(state.SetAvatar(50, 1002007));
        Assert.Equal((1000007L, 1L), (Of(50, "face"), Of(50, "gender")));
        Assert.True(state.SetAvatar(50, 1002009));
        Assert.Equal((1000009L, 0L), (Of(50, "face"), Of(50, "gender")));
    }

    [Fact]
    public void AnImportedPictureGetsTheDefaultFaceOfTheBodysGender()
    {
        state.CreateAccount(50, "a");
        ImportPicture(50);
        Assert.True(state.SetAvatar(50, 1002009));
        Assert.True(state.SetAvatar(50, null));
        Assert.Equal((1000009L, 0L), (Of(50, "face"), Of(50, "gender")));  // the woman keeps her body and gets a female head
    }

    void ImportPicture(long account) =>
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Db)!, "photos", account.ToString())).FullName, State.ImportedAvatar), [1]);

    [Fact]
    public void TheOwnPictureKeepsTheChosenHeadAndGenderAndAListedChoiceStillSetsThem()
    {
        state.CreateAccount(50, "a");
        ImportPicture(50);
        Set(50, 1000002, 1);
        Assert.True(state.SetAvatar(50, null));
        Assert.Equal((1000002L, 1L), (Of(50, "face"), Of(50, "gender")));  // only the picture changes
        Assert.Equal($"{BASE}/photos/50/{State.ImportedAvatar}", (string)state.Store.AccountJson(50)["avatar_url"]!);
        Set(50, 1002007, 1);  // a picture id is no 3D head
        Assert.True(state.SetAvatar(50, null));
        Assert.Equal((1000000L, 1L), (Of(50, "face"), Of(50, "gender")));  // the default head of the gender
        Assert.True(state.SetAvatar(50, 1000009));
        Assert.Equal((1000009L, 0L), (Of(50, "face"), Of(50, "gender")));  // a listed choice sets both
    }

    [Fact]
    public void TheChoicesTellTheHeadTheGameUsesAndWhetherThePictureIsTheOwn()
    {
        state.CreateAccount(50, "a");
        ImportPicture(50);
        Set(50, 1000002, 1);
        var listed = state.AvatarChoices(50, "https://web.test");
        Assert.Equal((1000002L, false), ((long)listed["face"]!, (bool)listed["own"]!));
        Assert.True(state.SetAvatar(50, null));
        var own = state.AvatarChoices(50, "https://web.test");
        Assert.Equal((1000002L, true), ((long)own["face"]!, (bool)own["own"]!));
    }

    [Fact]
    public void TheFixTakesTheFacesGenderAndMakesEveryFaceA3DFaceAndIsIdempotent()
    {
        foreach (var id in new long[] { 50, 51, 52, 53, 54, 55, 56, 57 }) state.CreateAccount(id, $"p{id}");
        Set(50, 1000007, 0);  // body and face disagree
        Set(51, 1002007, 1);  // a picture, no 3D face
        Set(52, 1000024, 0);  // the store's head stays
        Set(53, 4242, 0);     // no listed avatar: its gender stays, a default face of it
        Set(55, 1000000, 0, $"{BASE}/photos/55/avatar.jpg");  // its own picture: the male face was the server's fill-in, the woman stays a woman
        Set(54, 1000009, 0);  // fine
        Set(57, 1000000, 0, $"{BASE}/media/avatar/256/1000000.png");  // a restored profile's default address with the male face: the face's gender
        Set(56, 1000009, 0, $"{BASE}/media/avatar/256/1000000.png");  // a restore without a picture: the default address, a female face: stable
        Assert.Equal("would fix: 2 body(ies) of another gender than their face, 3 face(s) that are no 3D face of their body", state.FixFaces(apply: false));
        Assert.Equal(0L, Of(50, "gender"));
        state.FixFaces(apply: true);
        Assert.Equal([(1000007L, 1L), (1000007L, 1L), (1000024L, 0L), (1000009L, 0L), (1000009L, 0L), (1000009L, 0L), (1000009L, 0L), (1000000L, 1L)], new long[] { 50, 51, 52, 53, 54, 55, 56, 57 }.Select(i => (Of(i, "face"), Of(i, "gender"))));
        Assert.Equal("fixed: 0 body(ies) of another gender than their face, 0 face(s) that are no 3D face of their body", state.FixFaces(apply: true));
    }
}

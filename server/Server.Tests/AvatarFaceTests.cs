using System.Text.Json.Nodes;
using Resurgence.Server;
using static Resurgence.Server.Tests.Fixtures;

namespace Resurgence.Server.Tests;

/// <summary>The account's `face` is always a 3D head (`1000000 + id mod 1000`) of the body's gender; the picture id stays in `avatar`/`avatar_id`. Invented list: 1000007 and its pictures are male, 1000009 and its pictures female.</summary>
public class AvatarFaceTests : StateTestBase
{
    public AvatarFaceTests() => state = new State(Db, Py.DumpBytes(Fixtures.CONNECT), BASE, ICON, APP, LOCALE, TEXTURES,
        avatars: (JsonArray)JsonNode.Parse("""[{"id": 1000000, "gender": "m"}, {"id": 1000007, "gender": "m"}, {"id": 1000009, "gender": "f"}, {"id": 1001007, "gender": "m"}, {"id": 1002007, "gender": "m"}, {"id": 1002009, "gender": "f"}]""")!);

    long Of(long account, string key) => (long)state.Store.AccountJson(account)[key]!;

    void Set(long account, long face, long gender)
    {
        using var con = state.Store.Open();
        con.Exec("UPDATE accounts SET account = json_set(account, '$.face', ?, '$.gender', ?) WHERE id = ?", face, gender, account);
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
        var photos = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Db)!, "photos", "50")).FullName;
        File.WriteAllBytes(Path.Combine(photos, State.ImportedAvatar), [1]);
        Assert.True(state.SetAvatar(50, 1002009));
        Assert.True(state.SetAvatar(50, null));
        Assert.Equal((1000009L, 0L), (Of(50, "face"), Of(50, "gender")));  // the woman keeps her body and gets a female head
    }

    [Fact]
    public void TheFixTakesTheFacesGenderAndMakesEveryFaceA3DFaceAndIsIdempotent()
    {
        foreach (var id in new long[] { 50, 51, 52, 53, 54 }) state.CreateAccount(id, $"p{id}");
        Set(50, 1000007, 0);  // body and face disagree
        Set(51, 1002007, 1);  // a picture, no 3D face
        Set(52, 1000024, 0);  // the store's head stays
        Set(53, 4242, 0);     // no listed avatar: its gender stays, a default face of it
        Set(54, 1000009, 0);  // fine
        Assert.Equal("would fix: 1 body(ies) of another gender than their face, 2 face(s) that are no 3D face", state.FixFaces(apply: false));
        Assert.Equal(0L, Of(50, "gender"));
        state.FixFaces(apply: true);
        Assert.Equal([(1000007L, 1L), (1000007L, 1L), (1000024L, 0L), (1000009L, 0L), (1000009L, 0L)], new long[] { 50, 51, 52, 53, 54 }.Select(i => (Of(i, "face"), Of(i, "gender"))));
        Assert.Equal("fixed: 0 body(ies) of another gender than their face, 0 face(s) that are no 3D face", state.FixFaces(apply: true));
    }
}

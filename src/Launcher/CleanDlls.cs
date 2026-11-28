using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Resurgence.Launcher;

/// <summary>The DLLs next to the game's exe in a clean install of the build the community server supports (<see cref="Build"/>, the server's <c>game_build</c>): name, size and SHA-256 (in groups of eight hexadecimal digits: the repository's push check takes a longer run for a credential). A DLL in the folder that is not here, or differs
/// (docs/launcher.md#the-install-check, check 10), is foreign: a graphics wrapper, a mod, a replaced or an old file. Data: a new supported build is a new table, with <c>game_build</c> changed together.</summary>
public static class CleanDlls
{
    /// <summary>How a DLL next to the exe differs from a clean install: it is not in the list, it is in it with another size or SHA-256, or it could not be read to tell (gone or locked since it was listed).</summary>
    public enum State { Extra, Changed, Unreadable }

    public const long Build = 26432499;

    /// <summary>The PE time stamp of the game's own exe of <see cref="Build"/> (file version 1.0.0.1, 14463552 bytes). The <c>exe</c> check (docs/launcher.md#the-install-check) compares it; a new supported build is a new stamp, changed together with <see cref="Build"/>.</summary>
    public const uint ExeStamp = 1793705969;

    public static readonly IReadOnlyDictionary<string, (long Size, string Sha256)> Files = new Dictionary<string, (long Size, string Sha256)>(StringComparer.OrdinalIgnoreCase)
    {
        ["cefproxy.dll"] = (698368, "db49c521 5262bed7 f36dad05 ff988bf1 53998b26 d5d905b7 5602b8fb 16735e6f"),
        ["chrome_elf.dll"] = (1241088, "bb0ccf02 700bbee8 99e7e95e 9e6e50fe 0e74cd8f 6a40386c 5bc420aa 5dd46298"),
        ["CrashRpt1402.dll"] = (130560, "f439a77c 531e8805 99eb7a7f b0690b3d 1c1720b5 6e1ac565 295d29fd ae6b9257"),
        ["d3dcompiler_47.dll"] = (4146520, "af8934f4 8cdb8bb1 73cea698 f2f09808 1e20f765 1b6cde27 27413d44 263e8bb3"),
        ["d3dx11_43.dll"] = (248672, "492e960c b3ccfc8c 25fc83f7 c464ba77 c86a2041 1347a1a9 b3e5d3e8 c9180a8d"),
        ["dbghelp.dll"] = (1080656, "9a26ffaf fb26fa65 49c6da75 f76238a9 03ca723f 9dad356f ba8d9106 7fe312fd"),
        ["fmod_event.dll"] = (421376, "e91445f4 f294d0c0 00c27fd3 a82438a1 e7314024 394bdabf 22f3a5eb 4a9b078e"),
        ["fmod_event_net.dll"] = (824832, "dd89fa92 38ca5511 8b1c4a93 5126e2b4 9bed3e9e cd4ce74f b0a31792 148dcb4f"),
        ["fmodex.dll"] = (1265664, "713921c2 93360ac9 7316217f 67001ff7 5cca95c2 5dc32c62 b12339c4 53862bd3"),
        ["FreeImage.dll"] = (2477056, "42bdfc13 b72ae2d2 38ccb8e4 7b67a575 64b4e0e9 8bc0e5fa bde91a33 54cb2d23"),
        ["icudt.dll"] = (9956864, "ef080ad7 436d544c 285d0261 31ad0faa 0b54d7e2 f098d5c6 c5920bbf 88b3f6a7"),
        ["libcef.dll"] = (214489088, "f6b03da7 92d64726 af613c7a f43fb83f 94270e54 0f67f0bc d2f0c2a6 132c67ee"),
        ["libEGL.dll"] = (415232, "e85419e5 ef6417f7 3863e0b5 d571d5b4 c3e77ae8 a0d75d8b 633c4e65 87ee4a58"),
        ["libGLESv2.dll"] = (7119360, "77225d31 56877af5 c7433648 9477ea7a 07a33f94 3f6ecc5f 7b9fc09d e79436e7"),
        ["message_router.dll"] = (911360, "9f5305b6 074754d7 9acd4ed9 1b19c4ef 96a22490 a8560726 cbcb152a df2f1d91"),
        ["minimal.dll"] = (818176, "0dec7811 5425ba45 a0539d95 b290204a 69fdc534 7025dd33 56377a33 d2ae04f0"),
        ["msvcp140.dll"] = (447568, "654412a5 0c83d218 d9f72f8b bd0e0d29 63ed0b58 be59d666 1e931f32 dc9f80d8"),
        ["ProfileContext_F.dll"] = (6656, "84880994 e61ada29 c927cf20 2eac8e0d bef95b32 214b0001 7ba1164f 15b70d20"),
        ["python27.dll"] = (2453504, "6b8e9437 3d800922 7a75e5a7 7093f283 55a8a018 af5b8590 78d6bfc0 6c506b79"),
        ["resource_manager.dll"] = (896000, "98bf2526 fbb469f4 8b112372 9aa167b7 668eed7a 83f9e318 8373da6c 8d018a67"),
        ["scheme_handler.dll"] = (865792, "4683757a 39a29dcb 8d9340a1 d9e8fe62 0d68e87c 0dad2e24 279f245d c4d164c0"),
        ["sentry.dll"] = (169984, "23352f76 b7ca8b2b 4834f12c 3f7d1fdf 351c4d7c 50e0e002 6eda71a5 5a6b99c6"),
        ["SentryProxy.dll"] = (18944, "0dc1929c 3d571f85 e7ea11e2 7affd760 7d87f06e 062c5851 8fc8e282 a89a27a2"),
        ["steam_api.dll"] = (107432, "fd16fba7 a64afdcf cad2a935 33743240 0c8c528e 3f210a66 72254f75 d39bad7e"),
        ["vcruntime140.dll"] = (124520, "60b813f8 b87ff4fa 344f081c 163c1a22 34b5e3e4 1f748721 c28fbdec bcfecb8a"),
        ["vk_swiftshader.dll"] = (4866048, "4ada2798 d140fb0b f0a9a1d5 030ae2a9 cbb846fe 51d6d151 ab56677e 004e5561"),
        ["vulkan-1.dll"] = (836608, "d390e5ec 3ab114f8 3a4af8bf 801edeca 6a904513 f9fd6410 fea2bb72 628d47b6"),
    };

    // ponytail: remembered by path, size and write time (the 214 MB libcef.dll is hashed once, not at every page that shows the game check); a file that changes without either is not noticed until the launcher restarts
    static readonly ConcurrentDictionary<string, string> Hashes = new();

    /// <summary>The <c>.dll</c> and <c>.asi</c> files directly in <paramref name="folder"/> that are not in <paramref name="clean"/> or differ from it in size or SHA-256, by name; a file that vanishes or cannot be read meanwhile is <see cref="State.Unreadable"/>, a folder that cannot be read throws.</summary>
    public static List<(FileInfo File, State State)> Foreign(string folder, IReadOnlyDictionary<string, (long Size, string Sha256)> clean)
    {
        var found = new List<(FileInfo, State)>();
        foreach (var path in Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p) is { } e && (e.Equals(".dll", StringComparison.OrdinalIgnoreCase) || e.Equals(".asi", StringComparison.OrdinalIgnoreCase))).Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            try
            {
                if (!clean.TryGetValue(file.Name, out var want)) found.Add((file, State.Extra));
                else if (file.Length != want.Size || !Hashes.GetOrAdd($"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}", _ => Sha256Of(path)).Equals(want.Sha256.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) found.Add((file, State.Changed));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { found.Add((file, State.Unreadable)); } // gone or locked since it was listed: the others are still judged
        }
        return found;
    }

    /// <summary>Whether the DLLs of a game of this Steam build (null: not known, as for a folder outside a Steam library) are judged: the table is of <see cref="Build"/> alone.</summary>
    public static bool Judges(string? build) => build is null || build == Build.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static string Sha256Of(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}

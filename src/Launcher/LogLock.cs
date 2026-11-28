using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using static Resurgence.Launcher.LogLock;

namespace Resurgence.Launcher;

/// <summary>
/// The game's log file writer has no lock of its own: a line written by one thread while another thread writes or flushes the log can crash the game.
/// At each start of the known exe the launcher starts the game suspended and wraps the writer's write and flush entries in the game's own memory lock:
/// two small functions in a page of the game's memory take the lock, call the original entry and release the lock, and the writer's table of entries
/// points at them. Every value is checked before anything is written; on any other exe or any other value nothing is written and the game starts as it is.
/// Nothing on disk changes.
/// </summary>
/// <param name="ExeSha256">The SHA-256 of the exe this applies to (lowercase hex).</param>
/// <param name="Table">Offset from the image base of the writer's table: three entries, the cleanup, the write and the flush.</param>
/// <param name="Cleanup">Offset of the cleanup entry the table holds (checked, never changed).</param>
/// <param name="Write">Offset of the write entry (two arguments on the stack, the writer in ecx).</param>
/// <param name="Flush">Offset of the flush entry (no argument, the writer in ecx).</param>
/// <param name="Lock">Offset of the game's memory lock, a Windows critical section the game creates before its first log line.</param>
/// <param name="Enter">Offset of the exe's import of <c>EnterCriticalSection</c>.</param>
/// <param name="Leave">Offset of the exe's import of <c>LeaveCriticalSection</c>.</param>
public sealed record LogLock(string ExeSha256, uint Table, uint Cleanup, uint Write, uint Flush, uint Lock, uint Enter, uint Leave)
{
    /// <summary>The exe Steam serves since November 2026 (its SHA-256 written as bytes).</summary>
    public static readonly LogLock Known = new(Convert.ToHexStringLower([38, 250, 59, 103, 124, 212, 15, 46, 213, 173, 25, 87, 221, 136, 153, 61, 48, 190, 35, 47, 217, 235, 142, 213, 114, 82, 247, 150, 121, 111, 45, 58]),
        Table: 10880620, Cleanup: 1698912, Write: 1700352, Flush: 1699728, Lock: 14214368, Enter: 9806144, Leave: 9806160);

    /// <summary>The flush function starts this many bytes into the page, after the write function.</summary>
    public const int FlushAt = 44;

    /// <summary>The exe's SHA-256 (lowercase hex); null when it cannot be read.</summary>
    public static string? Sha256(string exe)
    {
        try
        {
            using var f = File.OpenRead(exe);
            return Convert.ToHexStringLower(SHA256.HashData(f));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The two functions for an image loaded at <paramref name="image"/>: the write one, then the flush one at <see cref="FlushAt"/>. They use absolute addresses only, so they run at any address.</summary>
    public byte[] Code(uint image)
    {
        var (lk, enter, leave) = (D(image + Lock), D(image + Enter), D(image + Leave));
        return [
            // write: push ecx, push lock, call [Enter], pop ecx; push the two arguments again; mov eax, Write; call eax
            .. H("5168"), .. lk, .. H("FF15"), .. enter, .. H("59 FF742408 FF742408 B8"), .. D(image + Write), .. H("FFD0"),
            // keep the result: push eax, push lock, call [Leave], pop eax, ret 8
            .. H("5068"), .. lk, .. H("FF15"), .. leave, .. H("58 C20800"),
            // flush: push ecx, push lock, call [Enter], pop ecx, mov eax, Flush; call eax; push lock, call [Leave], ret
            .. H("5168"), .. lk, .. H("FF15"), .. enter, .. H("59 B8"), .. D(image + Flush), .. H("FFD0 68"), .. lk, .. H("FF15"), .. leave, .. H("C3"),
        ];
    }

    /// <summary>Checks the table, then writes the two functions and points the table at them; nothing is written unless every check passed. Null when applied, else why not.</summary>
    public string? Apply(ISuspendedGame game)
    {
        var image = game.Image;
        if (image == 0 || game.Read(image + Table, 12) is not { } table) return "the game's memory could not be read";
        byte[] expected = [.. D(image + Cleanup), .. D(image + Write), .. D(image + Flush)];
        if (!table.AsSpan().SequenceEqual(expected)) return "the log writer's table holds other values";
        var code = Code(image);
        var page = game.Allocate(code.Length);
        if (page == 0) return "no memory could be allocated in the game";
        if (!game.Write(page, code)) return "the lock's functions could not be written";
        return game.Write(image + Table + 4, [.. D(page), .. D(page + FlushAt)]) ? null : "the log writer's table could not be written";
    }

    /// <summary>Applies the lock to a game started suspended, logs the outcome, then resumes the game in every case; false when it could not be resumed.</summary>
    public bool ApplyAndResume(ISuspendedGame game, ILogger? log)
    {
        if (Apply(game) is { } why) log?.LogWarning("The game's log lock is not applied, the game starts without it: {Why}", why);
        else log?.LogInformation("The game's log lock is applied");
        return game.Resume();
    }

    internal static byte[] D(uint value) => BitConverter.GetBytes(value);
    internal static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
}

/// <summary>
/// In the current game the Snow Goose's call animation raises an event of its own instead of the one the other birds' call animations raise, so its call is heard but never registers as a clue.
/// At each start of the known exe (the one of <see cref="LogLock.Known"/>), while the game is suspended, the launcher points one call of the game at a small function in a page of the game's memory.
/// The game makes that call for each event of an animation it loads; the function looks at the event's name: when it is exactly the 28 characters <c>snd_snow_goose_call_sequence</c>,
/// it hands the game the name <c>play_call_sound</c> instead (kept after the function in the game's own text form: the 15 characters, their count and the capacity), then jumps to the game's own
/// registration function. Every other name passes through unchanged. Every value is checked before anything is written; on any other exe or any other value nothing is written and the game starts as it is.
/// Nothing on disk changes.
/// </summary>
/// <param name="ExeSha256">The SHA-256 of the exe this applies to (lowercase hex).</param>
/// <param name="Site">Offset from the image base of the call that registers one event of an animation: a call of <paramref name="Register"/>, pointed at the function.</param>
/// <param name="Args">Offset of the 13 bytes before that call that push its arguments (checked, never changed).</param>
/// <param name="CategoryPush">Offset of the push of the events' category before them (checked, never changed).</param>
/// <param name="Category">Offset of the category's text, <c>animation_track_event</c> (checked, never changed).</param>
/// <param name="Register">Offset of the game's registration function (its first 12 bytes checked, never changed).</param>
public sealed record GooseCall(string ExeSha256, uint Site, uint Args, uint CategoryPush, uint Category, uint Register)
{
    /// <summary>The same exe as the log lock.</summary>
    public static readonly GooseCall Known = new(LogLock.Known.ExeSha256, Site: 5041552, Args: 5041539, CategoryPush: 5041502, Category: 11006028, Register: 6946480);

    /// <summary>The new name starts this many bytes into the page, after the function; <see cref="Size"/> is the whole.</summary>
    public const int NameAt = 96, Size = 124;

    /// <summary>The function for an image loaded at <paramref name="image"/>, written at <paramref name="page"/> (where its new name is).</summary>
    public byte[] Code(uint image, uint page) => [
        // mov eax, [esp+4] (the name); cmp [eax+16], 28 (its count); jne to the jump; mov eax, [eax] (its characters)
        .. H("8B442404 8378101C 7548 8B00"),
        // the 28 characters four at a time ("snd_", "snow", "_goo", "se_c", "all_", "sequ", "ence"), each followed by jne to the jump
        .. H("8138736E645F 753E 817804736E6F77 7535 8178085F676F6F 752C 81780C73655F63 7523 817810616C6C5F 751A 81781473657175 7511 817818656E6365 7508"),
        // the same name: mov [esp+4], the new name
        .. H("C7442404"), .. D(page + NameAt),
        // the jump: mov eax, Register; jmp eax; then filler up to the name
        .. H("B8"), .. D(image + Register), .. H("FFE0 CCCCCCCCCCCCCC"),
        // the new name as the game keeps text: 16 bytes of characters, the count, the capacity, 4 unused bytes
        .. "play_call_sound\0"u8, .. D(15), .. D(15), .. D(0),
    ];

    /// <summary>The call instruction at <see cref="Site"/> of an image loaded at <paramref name="image"/> when it calls <paramref name="target"/>.</summary>
    byte[] Call(uint image, uint target) => [.. H("E8"), .. D(unchecked(target - (image + Site + 5)))];

    /// <summary>Checks the five places, then writes the function and points the call at it; nothing is written unless every check passed. Null when applied, else why not.</summary>
    public string? Apply(ISuspendedGame game)
    {
        var image = game.Image;
        if (image == 0) return "the game's memory could not be read";
        (uint At, byte[] Expected, string What)[] checks = [
            (Site, Call(image, image + Register), "the registration call"),
            (Args, H("55 8D4C244C 51 56 8BCF 896C247C"), "the registration call's arguments"),
            (CategoryPush, [.. H("6A15 68"), .. D(image + Category)], "the category's push"),
            (Category, [.. "animation_track_event\0"u8], "the category's text"),
            (Register, H("55 8BEC 83E4F8 81EC1C040000"), "the registration function"),
        ];
        foreach (var (at, expected, what) in checks)
        {
            if (game.Read(image + at, expected.Length) is not { } found) return "the game's memory could not be read";
            if (!found.AsSpan().SequenceEqual(expected)) return $"other values in {what}";
        }
        var page = game.Allocate(Size);
        if (page == 0) return "no memory could be allocated in the game";
        if (!game.Write(page, Code(image, page))) return "the function could not be written";
        return game.Write(image + Site, Call(image, page)) ? null : "the registration call could not be written";
    }

    /// <summary>Applies the patch to a game started suspended and logs the outcome; the game is resumed by <see cref="LogLock.ApplyAndResume"/>.</summary>
    public void ApplyAndLog(ISuspendedGame game, ILogger? log)
    {
        if (Apply(game) is { } why) log?.LogWarning("The game's goose call patch is not applied, the game starts without it: {Why}", why);
        else log?.LogInformation("The game's goose call patch is applied");
    }
}

/// <summary>
/// In the current game one of a rabbit's nervous standing animations plays a loud honk where the game before played a quiet stomp, so a dozen rabbits around make constant noise; the honk belongs to the event's possessed rabbits only.
/// At each start of the known exe (the one of <see cref="LogLock.Known"/>), while the game is suspended, the launcher points the start of the game's function that plays an animal's sounds at a small function in a page of the game's memory.
/// The small function looks at the sound about to play. For every sound but the rabbit's honk it runs the game's own code unchanged. For the honk it looks at the animal that makes it: an animal whose behaviour profile is one of the event's keeps the honk;
/// any other animal, and any case that cannot be identified (every step to the profile is guarded), gets the rabbit's quiet stomp instead, which the function finds in the game's own table of sounds and hands over in place of the honk
/// (a game whose table has no stomp plays the honk as before). It uses absolute addresses for two values only, so it is built for the image base; it does not depend on the page's address, only the jump written at the site does.
/// Every value is checked before anything is written; on any other exe or any other value nothing is written and the game starts as it is. Nothing on disk changes.
/// </summary>
/// <param name="ExeSha256">The SHA-256 of the exe this applies to (lowercase hex).</param>
/// <param name="Site">Offset from the image base of the start of the game's function that plays an animal's sounds: its first six bytes are replaced by a jump to the function and a filler.</param>
/// <param name="Profile">Offset of the value the function compares the animal's first field with, to know it is the kind of animal that has a behaviour profile.</param>
/// <param name="Checks">The places checked before anything is written, in order (the first is the site): the offset from the image base and the expected bytes as hex, where <c>@N</c> stands for the 4 bytes of the image base plus <c>N</c>.</param>
public sealed record RabbitSound(string ExeSha256, uint Site, uint Profile, (uint At, string Expected)[] Checks)
{
    /// <summary>The same exe as the log lock.</summary>
    public static readonly RabbitSound Known = new(LogLock.Known.ExeSha256, Site: 6747264, Profile: 10909564, Checks: [
        // the start of the function (the site's six bytes, then the push of its handler's address) and the places in and around it the function relies on
        (6747264, "55 8B EC 83 E4 F8 6A FF 68 @8952384"),
        (6747308, "8B F9 89 7C 24 18 8B 57 1C 89 54 24 14 3B 57 20"),
        (6747330, "8B 0A 85 C9"),
        (6747340, "8B 01 8B 49 04 3B C1"),
        (6747360, "8B 75 08 3B 30 74 0C 83 C0 04 3B C1 75 F2"),
        (6747379, "8B 45 0C 8B 48 04"),
        (6747820, "83 C2 14"),
        (6747854, "C2 08 00"),
        // the places the function reaches the animal and the table of sounds through
        (6945702, "8B 08 8B 44 24 3C 8B 11 8B 52 04 50 53 FF D2"),
        (6944776, "8D 70 10 38 5C 24 20 74 64 8B 50 0C 8B 44 24 18 89 16"),
        (2510786, "8B 80 6C 12 00 00 8B 50 0C 3B 51 04"),
        (2636364, "8B 83 6C 12 00 00 8B 40 0C 85 C0 75 08 33 FF 89 7C 24 20 EB 0B 05 4C FF FF FF 89 44 24 20 8B F8 57 89 9F 74 04 00 00"),
        (2392707, "C7 86 B4 00 00 00 @10909564"),
        (2631587, "8B 86 1C 12 00 00 53 8B 58 04 57 56 32 D2 8B CB E8"),
        (2635882, "8B 44 24 38 8D 50 0C 52 8B 50 08 52"),
        (2614156, "68 @10752268"),
        (2614235, "8D 4F 0C E8"),
        (10752268, "A0 A5 75 A3"),
        (231233, "83 7E 14 10 72 02 8B 36"),
    ]);

    /// <summary>The function for an image loaded at <paramref name="image"/>: 177 bytes, to be written at the start of a page of the game's memory.</summary>
    public byte[] Code(uint image) => [
        // mov eax, [esp+4] (the sound); cmp [eax], the honk's id; je to the honk's case
        .. H("8B442404 81388A6B39B2 740D"),
        // the game's own first six bytes of the function, then mov eax, the rest of the function; jmp eax: every other sound, and a possessed rabbit's honk, go this way
        .. H("558BEC83E4F8 B8"), .. D(image + Site + 6), .. H("FFE0"),
        // the honk's case: mov eax, [esp+8] (the animal); then its first field must be the value that marks a kind of animal with a behaviour profile, each link tested for null on the way (null: the stomp)
        .. H("8B442408 85C0 7456 8B4004 85C0 744F 8138"), .. D(image + Profile), .. H("7547"),
        // the links on to the profile's name (null: the stomp); a name shorter than 16 characters gets the stomp
        .. H("8B80C0030000 85C0 743D 8B801C120000 85C0 7433 8B4004 85C0 742C 83782010 7226 8B400C"),
        // the name's first 16 characters four at a time: a difference gets the stomp, the event's profile jumps back to the game's own code
        .. H("8138616E696D 751B 817804616C2E77 7512 81780865726577 7509 81780C6F6C665F 7495"),
        // the stomp: push ebx, esi; walk the game's table of sounds (entries of 20 bytes, each a list of sounds) for the sound with the stomp's id, none: the game's own code
        .. H("53 56 8B511C 3B5120 7329 8B32 85F6 741A 8B06 3B4604 7313 8B18 85DB 7408 813B3BB5DCC2 740A 83C004 EBE8 83C214 EBD6"),
        // found: mov [esp+12], it (the sound the game plays); pop esi, ebx; jmp back to the game's own first six bytes above
        .. H("895C240C 5E 5B E95BFFFFFF"),
    ];

    static byte[] Expect(string expected, uint image) =>
        H(Regex.Replace(expected, @"@(\d+)", m => Convert.ToHexString(D(image + uint.Parse(m.Groups[1].Value)))));

    /// <summary>Checks every place, then writes the function and points the site at it; nothing is written unless every check passed. Null when applied, else why not.</summary>
    public string? Apply(ISuspendedGame game)
    {
        var image = game.Image;
        if (image == 0) return "the game's memory could not be read";
        for (var i = 0; i < Checks.Length; i++)
        {
            var expected = Expect(Checks[i].Expected, image);
            if (game.Read(image + Checks[i].At, expected.Length) is not { } found) return $"check {i + 1} could not be read";
            if (!found.AsSpan().SequenceEqual(expected)) return $"check {i + 1} holds other values";
        }
        var code = Code(image);
        var page = game.Allocate(code.Length);
        if (page == 0) return "no memory could be allocated in the game";
        if (!game.Write(page, code)) return "the sound function could not be written";
        // jmp to the page, then a filler for the sixth byte
        return game.Write(image + Site, [.. H("E9"), .. D(unchecked(page - (image + Site + 5))), .. H("90")]) ? null : "the jump to the sound function could not be written";
    }

    /// <summary>Applies the patch to a game started suspended and logs the outcome; the game is resumed by <see cref="LogLock.ApplyAndResume"/>.</summary>
    public void ApplyAndLog(ISuspendedGame game, ILogger? log)
    {
        if (Apply(game) is { } why) log?.LogWarning("The game's rabbit sound patch is not applied, the game starts without it: {Why}", why);
        else log?.LogInformation("The game's rabbit sound patch is applied");
    }
}

/// <summary>
/// The game picks an animal's coat from its species' coat table: for each texture slot of the animal's model it finds the table's group by the slot's name, picks one of the group's layers by weight, and finds that layer among the model's by name,
/// each time with an exact comparison of the letters; when the group or the layer is not found, it picks one of the slot's layers at random with the same chance each. Since the game update of late October the snowshoe hare's coat tables
/// are filed under a name whose letter case differs from the one the hare's model uses, so the game finds no group and picks one of the model's three skins at random, each with the same chance: the normal one, the albino one and the seasonal event's.
/// At each start of the known exe (the one of <see cref="LogLock.Known"/>), while the game is suspended, the launcher points the two calls of the game's exact comparison of two names (the group lookup and the layer match after the weighted pick)
/// at one small function in a page of the game's memory. The function takes the same arguments the same way (two addresses in ecx and edx, the length on the stack, the caller removes it) and calls the compare the game already imports,
/// <c>_memicmp</c>, which ignores letter case, on the same bytes in the same order; it returns that function's result, which like the original is zero for equal names, negative or positive otherwise, and the callers only test it against zero.
/// The game's third comparison, which groups the tables when the file is loaded, is not touched: it must stay exact. Every value is checked before anything is written; on any other exe or any other value nothing is written and the game starts as it is.
/// Nothing on disk changes.
/// </summary>
/// <param name="ExeSha256">The SHA-256 of the exe this applies to (lowercase hex).</param>
/// <param name="Lookup">Offset from the image base of the call in the table lookup: a call of the game's exact compare, pointed at the function.</param>
/// <param name="Layer">Offset of the call in the layer match, the same kind of call, pointed at the function.</param>
/// <param name="Import">Offset of the exe's import of <c>_memicmp</c> (the exe is checked before it runs, so the slot still holds the place of the import's name).</param>
/// <param name="Checks">The places checked before anything is written, in order: the offset from the image base and the expected bytes as hex.</param>
public sealed record CoatTable(string ExeSha256, uint Lookup, uint Layer, uint Import, (uint At, string Expected)[] Checks)
{
    /// <summary>The same exe as the log lock.</summary>
    public static readonly CoatTable Known = new(LogLock.Known.ExeSha256, Lookup: 4458892, Layer: 4470959, Import: 9806852, Checks: [
        // the two calls, with the instructions that show how they are used: the name's address in edx and the length pushed before, the length removed and the result tested against zero after
        (4458885, "8B 17 EB 02 8B D7 50 E8 BF 06 BC FF 83 C4 04 85 C0"),
        (4470950, "8B 57 F0 EB 03 8D 57 F0 50 E8 9C D7 BB FF 83 C4 04 85 C0"),
        // the group lookup's first instructions: it reads the table's groups of materials; the game has a second, identical-looking lookup for another kind of group, which is not this one
        (4458816, "53 55 8B 6C 24 0C 56 57 8B 78 20 8B 40 24"),
        // the game's compare: its first instructions (the length from the stack, the addresses from edx and ecx), and its two ends (the result in eax, the stack left to the caller)
        (4176, "56 8B 74 24 08 57 83 FE 04 72 19 EB 03"),
        (4275, "C1 F8 1F 5F 83 C8 01 5E C3 5F 33 C0 5E C3"),
        // the import's slot holds the place of its name, which is the hint then the name
        (9806852, "FC D1 B4 00"),
        (11850238, "5F 6D 65 6D 69 63 6D 70 00"),
    ]);

    /// <summary>The function's size.</summary>
    public const int Size = 16;

    /// <summary>The function for an image loaded at <paramref name="image"/>: 16 bytes, to be written at the start of a page of the game's memory.</summary>
    public byte[] Code(uint image) => [
        // push [esp+4] (the length), push ecx, push edx: the arguments of the case-blind compare, the first and second names in the order the game's compare reads them
        .. H("FF742404 51 52"),
        // call [the import]; add esp, 12; ret (the caller removes the length)
        .. H("FF15"), .. D(image + Import), .. H("83C40C C3"),
    ];

    /// <summary>The call instruction at <paramref name="at"/> of an image loaded at <paramref name="image"/> when it calls <paramref name="target"/>.</summary>
    static byte[] Call(uint image, uint at, uint target) => [.. H("E8"), .. D(unchecked(target - (image + at + 5)))];

    /// <summary>Checks every place, then writes the function and points the two calls at it; nothing is written unless every check passed. Null when applied, else why not.</summary>
    public string? Apply(ISuspendedGame game)
    {
        var image = game.Image;
        if (image == 0) return "the game's memory could not be read";
        for (var i = 0; i < Checks.Length; i++)
        {
            var expected = H(Checks[i].Expected);
            if (game.Read(image + Checks[i].At, expected.Length) is not { } found) return $"check {i + 1} could not be read";
            if (!found.AsSpan().SequenceEqual(expected)) return $"check {i + 1} holds other values";
        }
        var page = game.Allocate(Size);
        if (page == 0) return "no memory could be allocated in the game";
        if (!game.Write(page, Code(image))) return "the compare function could not be written";
        // the layer match first: a group found followed by an exact layer match falls back to the random pick, as before, so a failure between the two writes leaves the game as it was
        return game.Write(image + Layer, Call(image, Layer, page)) && game.Write(image + Lookup, Call(image, Lookup, page)) ? null : "the calls could not be written";
    }

    /// <summary>Applies the patch to a game started suspended and logs the outcome; the game is resumed by <see cref="LogLock.ApplyAndResume"/>.</summary>
    public void ApplyAndLog(ISuspendedGame game, ILogger? log)
    {
        if (Apply(game) is { } why) log?.LogWarning("The game's coat table patch is not applied, the game starts without it: {Why}", why);
        else log?.LogInformation("The game's coat table patch is applied");
    }
}

/// <summary>
/// In a multiplayer game the game asks the host whether a retrieving dog may pick a bird up, and a guest's answer comes over the network, but the guest's game reads the dog's answer field on the next frame and nothing clears it between two retrieves:
/// a guest's dog acts on the answer to its previous retrieve (after one refusal its next retrieve fails at the bird, and a stale "yes" lets it take a bird the host's dog already holds). The host's own answer needs no network round trip, so the host never sees this.
/// At each start of the known exe (the one of <see cref="LogLock.Known"/>), while the game is suspended, the launcher points the one call where the game asks that question at a 15-byte routine in a page of the game's memory:
/// it clears the dog's answer field (the dog is in <c>esi</c>, as the game's own function expects), then jumps to the game's own function, which sees the registers and the stack a direct call would leave.
/// The dog then waits for the host's real answer. Every value is checked before anything is written; on any other exe or any other value nothing is written and the game starts as it is. Nothing on disk changes.
/// </summary>
/// <param name="ExeSha256">The SHA-256 of the exe this applies to (lowercase hex).</param>
/// <param name="Site">Offset from the image base of the call to the game's function that sends the question: only its 4-byte target is changed.</param>
/// <param name="Target">Offset of that function, which the routine jumps to.</param>
/// <param name="Checks">The places checked before anything is written, in order: the offset from the image base and the expected bytes as hex. The first ends with the call after the instructions around it, the second is the start of the function, the third is the game's setup of the dog that proves the field's offset.</param>
public sealed record DogAnswer(string ExeSha256, uint Site, uint Target, (uint At, string Expected)[] Checks)
{
    /// <summary>The same exe as the log lock.</summary>
    public static readonly DogAnswer Known = new(LogLock.Known.ExeSha256, Site: 3764403, Target: 2703488, Checks: [
        // the dog owner's push of the population id, the dog getter, the bird's index to edx, the dog from the getter to esi, the call (to the function), and the retrieve state 5 set after it
        (3764387, "50 51 8B CB E8 B4 64 EC FF 8B D6 83 C4 04 8B F0 E8 C8 CF EF FF C7 87 9C 00 00 00 05 00 00 00"),
        // the function: its first instructions take the dog from esi
        (2703488, "83 EC 18 85 F6 74 05 8D 4E 14"),
        // the dog's setup, which writes the query id at +168 and the answer at +172, the field the routine clears
        (2691708, "89 BE A8 00 00 00 89 9E AC 00 00 00"),
    ]);

    /// <summary>The routine's size.</summary>
    public const int Size = 15;

    /// <summary>The routine for an image loaded at <paramref name="image"/>, written at <paramref name="page"/>: mov dword ptr [esi+172], 0; jmp the function.</summary>
    public byte[] Code(uint image, uint page) => [.. H("C786AC000000 00000000 E9"), .. D(unchecked(image + Target - (page + Size)))];

    /// <summary>Checks every place, then writes the routine and points the call at it; nothing is written unless every check passed. Null when applied, else why not.</summary>
    public string? Apply(ISuspendedGame game)
    {
        var image = game.Image;
        if (image == 0) return "the game's memory could not be read";
        for (var i = 0; i < Checks.Length; i++)
        {
            var expected = H(Checks[i].Expected);
            if (game.Read(image + Checks[i].At, expected.Length) is not { } found) return $"check {i + 1} could not be read";
            if (!found.AsSpan().SequenceEqual(expected)) return $"check {i + 1} holds other values";
        }
        var page = game.Allocate(Size);
        if (page == 0) return "no memory could be allocated in the game";
        if (!game.Write(page, Code(image, page))) return "the routine could not be written";
        // only the call's 4-byte target changes, after the opcode: until this write lands no code points at the routine
        return game.Write(image + Site + 1, D(unchecked(page - (image + Site + 5)))) ? null : "the call could not be written";
    }

    /// <summary>Applies the patch to a game started suspended and logs the outcome; the game is resumed by <see cref="LogLock.ApplyAndResume"/>.</summary>
    public void ApplyAndLog(ISuspendedGame game, ILogger? log)
    {
        if (Apply(game) is { } why) log?.LogWarning("The game's dog answer patch is not applied, the game starts without it: {Why}", why);
        else log?.LogInformation("The game's dog answer patch is applied");
    }
}

/// <summary>The game started suspended, as the log lock, the goose call patch, the rabbit sound patch, the coat table patch and the dog answer patch read and write it: the seam a test replaces with a byte array.</summary>
public interface ISuspendedGame
{
    /// <summary>Where the exe is loaded; 0 when not known.</summary>
    uint Image { get; }
    /// <summary>The bytes, or null when they cannot all be read.</summary>
    byte[]? Read(uint address, int count);
    /// <summary>Writes over any protection, code included (put back afterwards, the instruction cache flushed); false when it fails.</summary>
    bool Write(uint address, byte[] bytes);
    /// <summary>A new executable page of at least <paramref name="size"/> bytes; 0 when none.</summary>
    uint Allocate(int size);
    /// <summary>Lets the game run; false when it fails.</summary>
    bool Resume();
}

/// <summary>A game process created suspended (<c>CreateProcess</c>), with the same exe, arguments, folder and environment as <see cref="Process.Start(ProcessStartInfo)"/> would use.</summary>
sealed class SuspendedProcess : ISuspendedGame
{
    readonly nint process, thread;
    readonly uint pid;

    SuspendedProcess(ProcessInformation p)
    {
        (process, thread, pid) = (p.Process, p.Thread, p.ProcessId);
        // the 32-bit PEB's ImageBaseAddress: set by Windows when it maps the exe, before the first instruction runs
        if (NtQueryInformationProcess(process, Wow64Information, out var peb, nint.Size, 0) == 0 && peb != 0 && ReadAt(peb + 8, 4) is { } b) Image = BitConverter.ToUInt32(b);
    }

    public uint Image { get; }

    /// <summary>Null when Windows does not start it.</summary>
    public static SuspendedProcess? Create(ProcessStartInfo info)
    {
        var env = new StringBuilder();
        foreach (var (k, v) in info.Environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)) env.Append(k).Append('=').Append(v).Append('\0');
        var si = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        return CreateProcessW(info.FileName, new StringBuilder($"\"{info.FileName}\" {info.Arguments}"), 0, 0, false, Suspended | UnicodeEnvironment, env.Append('\0').ToString(),
            string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref si, out var p) ? new(p) : null;
    }

    public byte[]? Read(uint address, int count) => ReadAt((nint)address, count);

    byte[]? ReadAt(nint address, int count)
    {
        var b = new byte[count];
        return ReadProcessMemory(process, address, b, count, out var n) && n == count ? b : null;
    }

    public bool Write(uint address, byte[] bytes)
    {
        if (!VirtualProtectEx(process, (nint)address, bytes.Length, ReadWrite, out var old)) return false;
        var ok = WriteProcessMemory(process, (nint)address, bytes, bytes.Length, out var n) && n == bytes.Length;
        VirtualProtectEx(process, (nint)address, bytes.Length, old, out _);
        if (ok) FlushInstructionCache(process, (nint)address, bytes.Length);
        return ok;
    }

    public uint Allocate(int size)
    {
        var a = VirtualAllocEx(process, 0, size, Commit | Reserve, ExecuteRead);
        // the game is 32-bit: a page it cannot address is no use (the lowest free one is taken, so this does not happen in practice)
        if ((ulong)a + (ulong)size <= uint.MaxValue) return (uint)a;
        if (a != 0) VirtualFreeEx(process, a, 0, Release);
        return 0;
    }

    public bool Resume() => ResumeThread(thread) != uint.MaxValue;

    /// <summary>The running game, which owns the process handle from now on.</summary>
    public GameProcess Started()
    {
        CloseHandle(thread);
        var (h, id) = (process, pid);
        int? code() => GetExitCodeProcess(h, out var c) && c != StillActive ? unchecked((int)c) : null;
        return new(() => code() is null && GameProcess.HasVisibleWindow(id), code, () => CloseHandle(h)) { Id = (int)id, Memory = () => GameMemory.Read(h) };
    }

    /// <summary>Ends a game that could not be resumed.</summary>
    public void Kill()
    {
        TerminateProcess(process, 1);
        CloseHandle(thread);
        CloseHandle(process);
    }

    const uint Suspended = 4, UnicodeEnvironment = 1024, Commit = 4096, Reserve = 8192, Release = 32768, ExecuteRead = 32, ReadWrite = 4, StillActive = 259;
    const int Wow64Information = 26;

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short Show, Reserved2Size;
        public nint Reserved2, StdIn, StdOut, StdErr;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inherit, uint flags, string environment, string? directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint process, int kind, out nint value, int size, nint returned);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, int size, out nint read);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, int size, out nint written);
    [DllImport("kernel32.dll")] static extern bool VirtualProtectEx(nint process, nint address, nint size, uint protect, out uint old);
    [DllImport("kernel32.dll")] static extern nint VirtualAllocEx(nint process, nint address, nint size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(nint process, nint address, nint size, uint type);
    [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(nint process, nint address, nint size);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}

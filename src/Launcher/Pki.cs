using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Resurgence.Launcher;

/// <summary>
/// The local certificate authority and the certificate the forwarder answers with. The authority is name-constrained to the game's names
/// (a critical name constraints extension: Windows refuses a certificate it signed for any other name) and its key exists only in memory
/// while <see cref="Install"/> runs: the leaf is signed, the key is dropped, so it can never be exported or reused. The leaf's key is a
/// non-exportable key of the user's Windows key store; only its public certificate is a file.
/// </summary>
static class Pki
{
    public const string CaSubject = "CN=Hunter Resurgence local CA";
    static readonly TimeSpan Life = TimeSpan.FromDays(730);

    /// <summary>Where setup keeps its parts: the leaf's folder, the name of its key in the key store and the name of the current user's certificate store that trusts the authority.
    /// <see cref="Default"/> in the launcher (the old kit's names, so an old setup is found too); temporary ones in the tests.</summary>
    public record Place(string Dir, string KeyName, string Store)
    {
        public static readonly Place Default = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hunter-resurgence-play"), "hunter-resurgence-play-leaf", "Root");

        public string LeafFile => Path.Combine(Dir, "leaf.cer");

        public virtual X509Store Open()
        {
            var s = new X509Store(Store, StoreLocation.CurrentUser);
            s.Open(OpenFlags.ReadWrite);
            return s;
        }
    }

    /// <summary>True when any part of a setup exists: the leaf file, its key or the trusted authority.</summary>
    public static bool Installed(Place? place = null)
    {
        var p = place ?? Place.Default;
        if (File.Exists(p.LeafFile) || CngKey.Exists(p.KeyName)) return true;
        using var store = p.Open();
        return Authorities(store).Count > 0;
    }

    public static X509Certificate2 NewCa(IEnumerable<string> hosts, DateTimeOffset now)
    {
        var req = new CertificateRequest(CaSubject, RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(NameConstraints(hosts));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(now.AddDays(-1), now + Life);
    }

    /// <summary>The RFC 5280 name constraints extension (2.5.29.30), critical, permitting only DNS names under <paramref name="hosts"/>.</summary>
    public static X509Extension NameConstraints(IEnumerable<string> hosts)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) // permittedSubtrees
            foreach (var h in hosts)
                using (w.PushSequence()) // GeneralSubtree
                    w.WriteCharacterString(UniversalTagNumber.IA5String, h, new Asn1Tag(TagClass.ContextSpecific, 2)); // dNSName
        return new X509Extension(new Oid("2.5.29.30"), w.Encode(), true);
    }

    /// <summary>The DNS names a name constraints extension permits.</summary>
    public static string[] PermittedNames(X509Extension e)
    {
        var seq = new AsnReader(e.RawData, AsnEncodingRules.DER).ReadSequence();
        var permitted = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        var names = new List<string>();
        while (permitted.HasData)
            names.Add(permitted.ReadSequence().ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
        return [.. names];
    }

    public static X509Certificate2 NewLeaf(X509Certificate2 ca, RSA key, IEnumerable<string> hosts, DateTimeOffset now)
    {
        var names = hosts.ToArray();
        var req = new CertificateRequest("CN=" + names[0], key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in names) san.AddDnsName(n);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] = (byte)((serial[0] & 127) | 16); // a positive number
        return req.Create(ca, now.AddDays(-1), now + Life - TimeSpan.FromDays(1), serial);
    }

    /// <summary>A 2048-bit RSA key that cannot be exported. With a name it is kept in the user's key store (<see cref="Place.KeyName"/>), without it lives in memory.</summary>
    public static RSA NewLeafKey(string? name) => new RSACng(CngKey.Create(CngAlgorithm.Rsa, name, new CngKeyCreationParameters
    {
        ExportPolicy = CngExportPolicies.None,
        KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
        Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) },
    }));

    /// <summary>Makes the authority and the leaf, trusts the authority for the current user (Windows asks once), and drops the authority's key.</summary>
    public static void Install(IEnumerable<string> hosts, DateTimeOffset now, Place? place = null)
    {
        var p = place ?? Place.Default;
        var names = hosts.ToArray();
        // the old leaf goes first and the new one is written last: a setup that stops anywhere before the end (the player said No at the certificate prompt) leaves none, so the next start offers setup again
        if (File.Exists(p.LeafFile)) File.Delete(p.LeafFile);
        Untrust(p);
        Directory.CreateDirectory(p.Dir);
        using var ca = NewCa(names, now);
        using var key = NewLeafKey(p.KeyName);
        using var leaf = NewLeaf(ca, key, names, now);
        using (var store = p.Open())
        using (var pub = X509CertificateLoader.LoadCertificate(ca.RawData)) // the certificate alone, no key
            store.Add(pub);
        File.WriteAllBytes(p.LeafFile, leaf.RawData);
    }

    /// <summary>The authority, the leaf's key and its folder. Safe when nothing is installed.</summary>
    public static void Uninstall(Place? place = null)
    {
        var p = place ?? Place.Default;
        Untrust(p);
        if (CngKey.Exists(p.KeyName)) using (var k = CngKey.Open(p.KeyName)) k.Delete();
        if (Directory.Exists(p.Dir)) Directory.Delete(p.Dir, true);
    }

    static X509Certificate2Collection Authorities(X509Store store) => store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, CaSubject, false);

    static void Untrust(Place p)
    {
        using var store = p.Open();
        if (Authorities(store) is { Count: > 0 } found) store.RemoveRange(found);
    }

    /// <summary>Null when setup is in place for exactly <paramref name="hosts"/>: the leaf and its key exist, the leaf is valid and a trusted authority signed it. Else why not (a translated text).</summary>
    public static string? Check(string[] hosts, DateTimeOffset now, Place? place = null)
    {
        var p = place ?? Place.Default;
        if (!File.Exists(p.LeafFile) || !CngKey.Exists(p.KeyName)) return Kit.S("Kit.NoLeaf");
        using var leaf = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(p.LeafFile));
        if (leaf.NotAfter < now) return Kit.S("Kit.LeafExpired");
        var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault()?.EnumerateDnsNames().Order().ToArray() ?? [];
        if (!names.SequenceEqual(hosts.Order())) return Kit.S("Kit.OtherNames");
        using var store = p.Open();
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.CustomTrustStore.AddRange(Authorities(store));
        return chain.Build(leaf) ? null : Kit.S("Kit.NoCa");
    }

    /// <summary>The leaf with its key, for Kestrel. Throws with a texts key when setup has not run or the leaf has expired.</summary>
    public static X509Certificate2 LoadLeaf(DateTimeOffset now, Place? place = null)
    {
        var p = place ?? Place.Default;
        if (!File.Exists(p.LeafFile) || !CngKey.Exists(p.KeyName)) throw new InvalidOperationException(Kit.S("Kit.NoLeaf"));
        using var pub = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(p.LeafFile));
        if (pub.NotAfter < now) throw new InvalidOperationException(Kit.S("Kit.LeafExpired"));
        return WithKey(pub, p.KeyName);
    }

    /// <summary>The public certificate joined to its stored key (the key never leaves the key store).</summary>
    public static X509Certificate2 WithKey(X509Certificate2 pub, string keyName) => pub.CopyWithPrivateKey(new RSACng(CngKey.Open(keyName)));
}

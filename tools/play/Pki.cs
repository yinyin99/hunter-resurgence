using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Play;

/// <summary>
/// The local certificate authority and the certificate the forwarder answers with. The authority is name-constrained to the game's names
/// (a critical name constraints extension: Windows refuses a certificate it signed for any other name) and its key exists only in memory
/// while <see cref="Install"/> runs: the leaf is signed, the key is dropped, so it can never be exported or reused. The leaf's key is a
/// non-exportable key of the user's Windows key store; only its public certificate is a file.
/// </summary>
static class Pki
{
    public const string CaSubject = "CN=Hunter Resurgence local CA";
    public const string KeyName = "hunter-resurgence-play-leaf";
    static readonly TimeSpan Life = TimeSpan.FromDays(730);

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hunter-resurgence-play");
    static string LeafFile => Path.Combine(Dir, "leaf.cer");

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
        serial[0] = (byte)((serial[0] & 0x7F) | 0x10); // a positive number
        return req.Create(ca, now.AddDays(-1), now + Life - TimeSpan.FromDays(1), serial);
    }

    /// <summary>A 2048-bit RSA key that cannot be exported. With a name it is kept in the user's key store (<see cref="KeyName"/>), without it lives in memory.</summary>
    public static RSA NewLeafKey(string? name) => new RSACng(CngKey.Create(CngAlgorithm.Rsa, name, new CngKeyCreationParameters
    {
        ExportPolicy = CngExportPolicies.None,
        KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
        Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) },
    }));

    /// <summary>Makes the authority and the leaf, trusts the authority for the current user (Windows asks once), and drops the authority's key.</summary>
    public static void Install(IEnumerable<string> hosts, DateTimeOffset now)
    {
        var names = hosts.ToArray();
        Untrust();
        Directory.CreateDirectory(Dir);
        using var ca = NewCa(names, now);
        using var key = NewLeafKey(KeyName);
        using var leaf = NewLeaf(ca, key, names, now);
        File.WriteAllBytes(LeafFile, leaf.RawData);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        using var pub = X509CertificateLoader.LoadCertificate(ca.RawData); // the certificate alone, no key
        store.Add(pub);
    }

    /// <summary>The authority, the leaf's key and its folder. Safe when nothing is installed.</summary>
    public static void Uninstall()
    {
        Untrust();
        if (CngKey.Exists(KeyName)) using (var k = CngKey.Open(KeyName)) k.Delete();
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
    }

    static void Untrust()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var found = store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, CaSubject, false);
        if (found.Count > 0) store.RemoveRange(found);
    }

    /// <summary>The leaf with its key, for Kestrel. Throws with a texts key when setup has not run or the leaf has expired.</summary>
    public static X509Certificate2 LoadLeaf(DateTimeOffset now)
    {
        if (!File.Exists(LeafFile) || !CngKey.Exists(KeyName)) throw new InvalidOperationException(Loc.S("RunNoLeaf"));
        using var pub = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(LeafFile));
        if (pub.NotAfter < now) throw new InvalidOperationException(Loc.S("RunLeafExpired"));
        return WithKey(pub, KeyName);
    }

    /// <summary>The public certificate joined to its stored key (the key never leaves the key store).</summary>
    public static X509Certificate2 WithKey(X509Certificate2 pub, string keyName) => pub.CopyWithPrivateKey(new RSACng(CngKey.Open(keyName)));
}

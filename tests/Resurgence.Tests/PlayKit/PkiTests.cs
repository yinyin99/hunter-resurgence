using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Play;

namespace Resurgence.Tests.PlayKit;

public class PkiTests
{
    static readonly string[] Names = ["api.example.test", "web.example.test"];
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    static bool Trusts(X509Certificate2 ca, X509Certificate2 leaf, out string status)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        using var root = X509CertificateLoader.LoadCertificate(ca.RawData);
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        var ok = chain.Build(leaf);
        status = string.Join(",", chain.ChainStatus.Select(s => s.Status));
        return ok;
    }

    [Fact]
    public void AuthorityHasACriticalNameConstraintForTheGivenNamesOnly()
    {
        using var ca = Pki.NewCa(Names, Now);
        var nc = Assert.Single(ca.Extensions.Cast<X509Extension>(), e => e.Oid!.Value == "2.5.29.30");
        Assert.True(nc.Critical);
        Assert.Equal(Names, Pki.PermittedNames(nc));
        Assert.True(ca.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, ca.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
    }

    [Fact]
    public void WindowsAcceptsTheLeafForItsNamesAndRefusesAnotherNameSignedByTheSameAuthority()
    {
        using var ca = Pki.NewCa(Names, Now);
        using var key = Pki.NewLeafKey(null);
        using var good = Pki.NewLeaf(ca, key, Names, Now);
        Assert.True(Trusts(ca, good, out var s), s);
        Assert.Equal(Names, good.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames().ToArray());

        using var key2 = Pki.NewLeafKey(null);
        using var evil = Pki.NewLeaf(ca, key2, ["evil.test"], Now);
        Assert.False(Trusts(ca, evil, out s));
        Assert.Contains("HasNotPermittedNameConstraint", s);
    }

    [Fact]
    public void LeafKeyCannotBeExported()
    {
        using var key = Pki.NewLeafKey(null);
        Assert.ThrowsAny<CryptographicException>(() => key.ExportParameters(true));
        Assert.NotEmpty(key.ExportParameters(false).Modulus!); // the public half is not secret
    }
}

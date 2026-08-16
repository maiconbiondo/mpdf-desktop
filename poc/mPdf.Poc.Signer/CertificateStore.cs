using System.Security.Cryptography.X509Certificates;

namespace mPdf.Poc.Signer;

public static class CertificateStore
{
    /// Certificados com chave privada do repositório pessoal do usuário —
    /// cobre A1 instalado, token A3 espetado e conectores de nuvem ativos.
    public static IReadOnlyList<X509Certificate2> ListSigningCertificates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates
            .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now)
            .OrderBy(c => c.GetNameInfo(X509NameType.SimpleName, false))
            .ToList();
    }

    public static X509Certificate2? FindByThumbprint(string thumbprint) =>
        ListSigningCertificates().FirstOrDefault(c =>
            c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));
}

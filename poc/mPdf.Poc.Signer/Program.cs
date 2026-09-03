using System.Security.Cryptography.X509Certificates;
using mPdf.Poc.Signer;
using mPdf.Poc.Signer.Signing;

// Saída em pt-BR sem acento nos rótulos críticos? NÃO — console do Windows moderno é UTF-8:
Console.OutputEncoding = System.Text.Encoding.UTF8;

return args switch
{
    ["certificados"] => ListCerts(),
    ["assinar", .. var rest] => SignCmd(rest),
    ["verificar", var path] => VerifyCmd(path),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        mPdf.Poc.Signer — PoC de assinatura PAdES ICP-Brasil (Marco 0)

        Uso:
          certificados
              Lista os certificados de assinatura disponíveis no Windows.
          assinar --entrada <pdf> --saida <pdf> (--thumbprint <hex> | --pfx <arquivo> --senha <senha>)
                  [--carimbo pagina,x,y,largura,altura] [--motivo <texto>] [--nao-certificar]
          verificar <pdf>
              Mostra o relatório das assinaturas do arquivo.
        """);
    return 1;
}

static int ListCerts()
{
    try
    {
        var certs = CertificateStore.ListSigningCertificates();
        if (certs.Count == 0) { Console.WriteLine("Nenhum certificado com chave privada encontrado."); return 1; }
        foreach (var c in certs)
            Console.WriteLine($"{c.Thumbprint}  {c.GetNameInfo(X509NameType.SimpleName, false)}  " +
                              $"(emissor: {c.GetNameInfo(X509NameType.SimpleName, true)}; válido até {c.NotAfter:dd/MM/yyyy}) " +
                              $"[{c.PublicKey.Oid.FriendlyName}]");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERRO: {ex.Message}");
        return 1;
    }
}

static int SignCmd(string[] rest)
{
    string? Get(string name)
    {
        var i = Array.IndexOf(rest, name);
        return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
    }
    var entrada = Get("--entrada"); var saida = Get("--saida");
    if (entrada is null || saida is null) return Usage();

    try
    {
        X509Certificate2? cert = null;
        if (Get("--thumbprint") is { } tp) cert = CertificateStore.FindByThumbprint(tp);
        else if (Get("--pfx") is { } pfx)
            cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, Get("--senha"));
        if (cert is null) { Console.WriteLine("Certificado não encontrado."); return 1; }

        VisibleStamp? stamp = null;
        if (Get("--carimbo") is { } c)
        {
            var p = c.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            stamp = new VisibleStamp((int)p[0], p[1], p[2], p[3], p[4]);
        }
        var options = new SignatureOptions
        {
            Certify = !rest.Contains("--nao-certificar"),
            Stamp = stamp,
            Reason = Get("--motivo"),
        };

        var signed = PadesSigner.Sign(File.ReadAllBytes(entrada), cert, options);
        File.WriteAllBytes(saida, signed);
        Console.WriteLine($"Assinado com sucesso: {saida}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERRO: {ex.Message}");
        return 1;
    }
}

static int VerifyCmd(string path)
{
    try
    {
        var infos = SignatureVerifier.Verify(File.ReadAllBytes(path));
        if (infos.Count == 0) { Console.WriteLine("O arquivo não contém assinaturas."); return 1; }
        foreach (var i in infos)
            Console.WriteLine($"{i.FieldName}: assinante={i.SignerName}; íntegra={(i.IntegrityOk ? "SIM" : "NÃO")}; " +
                              $"subfilter={i.SubFilter}; cobre o documento todo={(i.CoversWholeDocument ? "sim" : "não")}; " +
                              $"data={i.SigningTime:dd/MM/yyyy HH:mm}");
        return infos.All(i => i.IntegrityOk) ? 0 : 2;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERRO: {ex.Message}");
        return 1;
    }
}

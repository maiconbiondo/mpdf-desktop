using System;
using System.Globalization;
using System.Windows.Data;
using mPdf.App.Icons;
using mPdf.Signing;

namespace mPdf.App.Converters;

/// Plano 14 (Task 4) — conversores VISUAIS dos diálogos escuros (cartões de certificado). Puramente de
/// apresentação: derivam o GLIFO Phosphor do tipo do certificado e o TEXTO de detalhe a partir do
/// `SigningCertificateInfo` já CLASSIFICADO pelo catálogo (IsIcpBrasilPersonal/Company/IsRsa — ver
/// mPdf.Signing.CertificateCatalog). Nenhuma lógica de assinatura/seleção vive aqui; só formatam o que a
/// classificação existente já decidiu. Usados por SignDialog e BatchSignDialog (mesmo item `Info`).

/// `SigningCertificateInfo` -> nome do ícone Phosphor: `buildings` (e-CNPJ / pessoa jurídica) ou `user`
/// (e-CPF / pessoa física ou outros). Devolve o CARACTERE do glifo (via Ph.Glyph) pra um TextBlock com a
/// fonte Phosphor. ECC não muda o ícone de tipo — a desabilitação é sinalizada à parte (ph prohibit).
public sealed class CertificadoIconeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Ph.Glyph(value is SigningCertificateInfo { IsIcpBrasilCompany: true } ? "buildings" : "user");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// `SigningCertificateInfo` -> linha de detalhe (muda) do cartão. ECC (não-RSA): a explicação pt-BR do
/// porquê não pode ser usado; senão o tipo ICP-Brasil (e-CPF / e-CNPJ) ou "Certificado digital" quando o
/// certificado não segue a convenção ICP-Brasil (os dois flags falsos).
public sealed class CertificadoDetalheConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not SigningCertificateInfo info
            ? string.Empty
            : !info.IsRsa
                ? "Assinatura ECDSA não suportada nesta versão"
                : info.IsIcpBrasilPersonal
                    ? "e-CPF · Pessoa física"
                    : info.IsIcpBrasilCompany
                        ? "e-CNPJ · Pessoa jurídica"
                        : "Certificado digital";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

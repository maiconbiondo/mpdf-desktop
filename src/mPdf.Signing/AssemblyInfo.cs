using System.Runtime.CompilerServices;

// Expõe o seam de teste de CertificateCatalog (IX509StoreReader, WindowsX509StoreReader e o overload
// internal de ListSigningCertificates) a CertificateCatalogTests — mesmo padrão já usado em
// src/mPdf.Documents/AssemblyInfo.cs e src/mPdf.Rendering/AssemblyInfo.cs. SignatureReader (também
// internal) não precisou disso porque os testes existentes só o exercitam através de ISigningEngine
// público; CertificateCatalog precisa expor o reader FAKE diretamente (Task 2, Plano 4 — nenhum teste
// unitário de classificação pode instalar certificado real no repositório do Windows).
[assembly: InternalsVisibleTo("mPdf.Signing.Tests")]

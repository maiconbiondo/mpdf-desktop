# mPDF

*A desktop PDF reader, editor and digital signer for Windows, built around ICP-Brasil (PAdES) signing. WPF/.NET 10. Licensed under AGPL-3.0 — see [Licença](#licença).*

Leitor, editor e assinador de PDF para Windows, com foco em assinatura digital
no padrão ICP-Brasil (PAdES). Construído em WPF sobre .NET 10.

## O que o mPDF faz

- **Leitura:** abas múltiplas, rolagem contínua ou página única, zoom, seleção
  e cópia de texto, busca com destaques, miniaturas, sumário/marcadores do
  PDF, impressão via diálogo do Windows.
- **Anotações:** marca-texto, sublinhado, riscado, caixa de texto, nota
  adesiva, desenho livre, formas (retângulo/linha/seta), carimbos de imagem
  (galeria configurável) — tudo no padrão PDF interoperável, visível em
  qualquer leitor.
- **Organização de páginas:** reordenar, girar, excluir, extrair, inserir,
  juntar e dividir documentos, em um modo de grade de miniaturas.
- **Formulários:** preenchimento de AcroForms (texto, checkbox, radio, combo,
  lista) com opção de achatamento (flatten). Formulários XFA são detectados e
  sinalizados como não suportados.
- **Assinatura digital ICP-Brasil:** certificados A1 (arquivo ou instalado),
  A3 físico (token/cartão) e A3 em nuvem, todos via repositório de
  certificados do Windows. Padrão PAdES, carimbo visível ou invisível,
  múltiplas assinaturas por salvamento incremental, proteção contra
  alterações (DocMDP) opcional na primeira assinatura, assinatura em lote.
- **Validação:** painel automático com status da assinatura (válida, válida
  com ressalvas, inválida), verificação de integridade, cadeia de
  certificação e revogação, com opção de conferência no validador oficial.

As assinaturas PAdES geradas pelo mPDF foram aprovadas no validador oficial
[validar.iti.gov.br](https://validar.iti.gov.br), o serviço do Instituto
Nacional de Tecnologia da Informação (ITI) para conferência de assinaturas
digitais ICP-Brasil.

## Build

Pré-requisito: .NET 10 SDK.

```powershell
# Rodar a suíte de testes completa
dotnet test mPdf.slnx

# Gerar o instalador (Inno Setup 6 precisa estar instalado -
# winget install JRSoftware.InnoSetup). A senha do instalador é
# OPCIONAL: omitida, o instalador sai sem senha; fornecida via
# -Password, sai criptografado com essa senha.
.\tools\installer\build-installer.ps1
# ou, com senha:
.\tools\installer\build-installer.ps1 -Password "sua-senha"
```

O script de build roda a suíte de testes primeiro e aborta se houver
qualquer teste vermelho — nunca gera instalador com a suíte quebrada.

## Licença

mPDF é distribuído sob **AGPL-3.0** (ver [`LICENSE`](LICENSE)). A licença é
herdada da dependência de edição/assinatura de PDF ([iText](https://itextpdf.com/),
também AGPL-3.0): qualquer distribuição do mPDF — binário ou modificado —
precisa disponibilizar o código-fonte correspondente sob a mesma licença.

Licenças de terceiros (PDFium, componentes MIT/BSD/Apache-2.0 e demais
dependências) estão listadas em [`docs/licencas/`](docs/licencas/).

## Contribuindo

Pull requests são bem-vindos. Ao contribuir, você concorda que sua
contribuição será distribuída sob a mesma licença AGPL-3.0 deste projeto.

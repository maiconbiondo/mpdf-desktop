# pdfium-stress

Probe de estresse para reproduzir um *access violation* intermitente dentro do PDFium nativo
(`FPDFDOC_ExitFormFillEnvironment`), observado durante a revisão final do Plano 3a. **Não é um
projeto de teste** — ele existe justamente para **travar o processo** sob certas condições; por
isso é um `csproj` autônomo, fora de `mPdf.slnx` (não entra em `dotnet build`/`dotnet test`
rodados contra a solução) e fora de `tools`/`tests` do ponto de vista do xUnit.

## O que este probe é

É a **PROBE DE ACEITAÇÃO** para um fix ainda pendente em torno de `FPDF_FORMFILLINFO`
(Docnet.Core / PDFium). A hipótese, registrada em `Program.cs`:

> `FPDF_FORMFILLINFO` é um objeto gerenciado MOVÍVEL cujo ponteiro o PDFium retém depois de
> `FPDFDOC_InitFormFillEnvironment`. Um GC compactante entre o primeiro render anotado
> (inicialização preguiçosa do form-fill environment) e o `Dispose` (`Exit`) pode deixar o PDFium
> desreferenciando um ponteiro morto.

Cada iteração do laço reproduz exatamente essa sequência: cria um `PdfDocumentRenderer` → renderiza
uma página com anotações (`RenderFlags.RenderAnnotations`, que inicializa o form-fill environment
de forma preguiçosa) → força uma GC compactante + churn de heap (pra mover/reutilizar o endereço
antigo) → `Dispose()` (que chama `FPDFDOC_ExitFormFillEnvironment`, o ponto onde o crash acontece).

## Evidência empírica já coletada

Rodado localmente durante a revisão (logs `a.log`/`b.log`/`c.log`/`stress.log`, não versionados —
só a conclusão importa aqui):

- **Só render, sem GC forçada** (`norender` omitido, `nogc` presente): 40 iterações, sobrevive.
- **Só GC forçada, sem render** (`nogc` omitido, `norender` presente): 40 iterações, sobrevive.
- **Os DOIS juntos** (render + GC forçada, configuração padrão): o processo morre de forma
  reproduzível por volta da **2ª iteração**, sempre durante/logo após o `Dispose()` — exatamente o
  ponto que a hipótese aponta (`FPDFDOC_ExitFormFillEnvironment` lendo um ponteiro que a GC
  compactante já moveu).

Ou seja: nenhuma das duas condições isoladas reproduz o bug — só a COMBINAÇÃO das duas.

## Contrato de aceitação

- **Hoje (Docnet.Core 2.6.0, sem o fix)**: `dotnet run --project tools/pdfium-stress` (parâmetros
  padrão: render + GC ligados) **deve travar** o processo (access violation nativa) por volta da
  2ª iteração — comportamento ESPERADO, não uma falha do probe.
- **Depois do fix** (pin do `FPDF_FORMFILLINFO`, ou workaround equivalente que impeça a GC de mover
  o objeto entre `Init` e `Exit`): a mesma execução **deve sobreviver 40+ iterações** e imprimir
  `SURVIVED all iterations — no AV reproduced`.

Este probe é o critério objetivo de "o fix funcionou" — rode-o de novo depois de qualquer mudança
relacionada a `PdfDocumentRenderer`/`FPDF_FORMFILLINFO`/pinning antes de considerar o fix aceito.

## Como rodar

```
dotnet run --project tools/pdfium-stress -- [iterações] [a4|carimbo] [norender] [nogc]
```

- `iterações` (posição 1, opcional): quantas vezes repetir o laço. Default: 300.
- `a4` (posição 2, opcional): usa `tests/fixtures/fixture-a4.pdf` (sem assinatura/widget) em vez do
  default `poc/samples/teste-carimbo.pdf` (COM um widget de assinatura — é o que dispara a
  inicialização preguiçosa do form-fill environment no render; `a4` tende a NÃO reproduzir o crash,
  útil como controle negativo).
- `norender`: pula o render — mantém só a GC forçada. Controle negativo (ver "Evidência" acima).
- `nogc`: pula a GC forçada — mantém só o render. Controle negativo (ver "Evidência" acima).

Exemplo (configuração que reproduz o crash em Docnet.Core 2.6.0):

```
dotnet run --project tools/pdfium-stress -- 300
```

Exemplo de controle negativo (sobrevive, confirma que a causa é a COMBINAÇÃO render+GC):

```
dotnet run --project tools/pdfium-stress -- 40 carimbo norender
dotnet run --project tools/pdfium-stress -- 40 carimbo nogc
```

## Por que fica fora de `mPdf.slnx`

Um `csproj` que **crasha por design** não pode entrar num `dotnet test`/`dotnet build` de solução
normal — derrubaria a suíte inteira (ou o pipeline de CI) sem que isso signifique regressão
nenhuma no resto do código. Por isso ele é um projeto standalone, referenciado só por caminho
relativo a partir daqui, nunca listado em `mPdf.slnx`.

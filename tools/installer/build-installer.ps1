<#
.SYNOPSIS
    Gera o instalador do mPDF (Inno Setup), a partir de uma publicacao self-contained
    (win-x64) da aplicacao. A senha do instalador e OPCIONAL a partir da v1.1.

.DESCRIPTION
    Passos executados, nesta ordem, cada um abortando o build se falhar:
      1. Se -Password foi fornecido, confere que nao e vazio/so espacos (a senha NUNCA
         fica gravada em arquivo neste repositorio - forneca via parametro de linha de
         comando, na hora do build). Se -Password foi omitido, o instalador sai sem
         senha - esse e o padrao a partir da v1.1 (projeto de codigo aberto, a senha
         deixou de ser mecanismo de contencao).
      2. Roda a suite de testes COMPLETA (`dotnet test mPdf.slnx`). Aborta se
         qualquer teste falhar - nunca gera instalador com a suite vermelha.
      3. Publica src/mPdf.App em modo self-contained win-x64 (NAO single-file:
         PDFium (nativo) + WPF precisam da pasta completa, nao de um unico exe).
      4. Confere que mPdf.App.exe e o pdfium.dll nativo estao na pasta publicada.
      5. Extrai a versao do produto do csproj (fonte UNICA - ver <Version> em
         src/mPdf.App/mPdf.App.csproj) e repassa para o Inno via /DMyAppVersion.
      6. Invoca o ISCC.exe (Inno Setup 6) com versao, pasta de publicacao e (se
         fornecida) a senha, gerando o instalador em tools/installer/Output/.

.PARAMETER Password
    Senha do instalador (Password= + Encryption=yes no Inno). OPCIONAL - se omitida,
    o instalador e gerado sem senha (padrao a partir da v1.1). Quando fornecida, nao
    pode ser vazia nem conter apenas espacos. NUNCA commitar a senha real neste
    repositorio; para testes de aceitacao, use uma senha de teste dedicada (ex.:
    "teste-aceitacao-2026"), nunca a senha real de producao.

.EXAMPLE
    .\tools\installer\build-installer.ps1
    Gera o instalador sem senha (padrao a partir da v1.1).

.EXAMPLE
    .\tools\installer\build-installer.ps1 -Password "minha-senha-forte"
    Gera o instalador com Password=+Encryption=yes no Inno (comportamento legado,
    ainda suportado para quem quiser proteger o payload por outro motivo).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Password
)

$ErrorActionPreference = 'Stop'

# Password e opcional (ver cabecalho). PSBoundParameters distingue "parametro omitido"
# de "parametro fornecido como string vazia" - so validamos quando foi de fato fornecido.
$PasswordProvided = $PSBoundParameters.ContainsKey('Password')
if ($PasswordProvided -and [string]::IsNullOrWhiteSpace($Password)) {
    throw "Quando -Password e fornecido, ele nao pode ser vazio nem conter apenas espacos. Omita o parametro inteiramente para gerar o instalador sem senha, ou forneca uma senha real (nao vazia)."
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$SlnFile = Join-Path $RepoRoot 'mPdf.slnx'
$AppProject = Join-Path $RepoRoot 'src\mPdf.App'
$AppCsproj = Join-Path $AppProject 'mPdf.App.csproj'
$PublishDir = Join-Path $PSScriptRoot 'publish\win-x64'
$OutputDir = Join-Path $PSScriptRoot 'Output'
$IssFile = Join-Path $PSScriptRoot 'mpdf.iss'

function Find-Iscc {
    # Caminho usado pela instalacao per-user via winget (JRSoftware.InnoSetup) nesta
    # maquina, com fallback para os caminhos padrao (instalacao all-users / 32-bit).
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    $onPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "ISCC.exe (Inno Setup 6) nao encontrado em nenhum dos caminhos conhecidos nem no PATH. Instale com: winget install JRSoftware.InnoSetup"
}

function Find-VcVars {
    # vcvars64.bat do VS Build Tools (workload C++), necessario p/ o link do Native AOT do preview handler.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $base = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null | Select-Object -First 1
        if ($base) {
            $vc = Join-Path $base 'VC\Auxiliary\Build\vcvars64.bat'
            if (Test-Path $vc) { return $vc }
        }
    }
    $candidates = @(
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat',
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'),
        'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    throw "vcvars64.bat nao encontrado. O preview handler (Native AOT) precisa do VS Build Tools com o workload 'Desktop development with C++' (MSVC + Windows SDK). Instale-o e tente de novo."
}

if ($PasswordProvided) {
    Write-Host "=== [1/6] Senha recebida (nunca impressa no log) ===" -ForegroundColor Cyan
} else {
    Write-Host "=== [1/6] Sem senha - instalador sera gerado sem Password=/Encryption= (padrao v1.1+) ===" -ForegroundColor Cyan
}

Write-Host "=== [2/6] Rodando suite de testes completa (dotnet test) ===" -ForegroundColor Cyan
& dotnet test $SlnFile
if ($LASTEXITCODE -ne 0) {
    throw "Suite de testes falhou (exit code $LASTEXITCODE). Build do instalador ABORTADO - nunca gerar instalador com a suite vermelha."
}

Write-Host "=== [3/6] Publicando mPdf.App (self-contained, win-x64) ===" -ForegroundColor Cyan
if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
& dotnet publish $AppProject -c Release -r win-x64 --self-contained true -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish falhou (exit code $LASTEXITCODE)."
}

Write-Host "=== [4/6] Conferindo artefatos publicados ===" -ForegroundColor Cyan
$exePath = Join-Path $PublishDir 'mPdf.App.exe'
if (-not (Test-Path $exePath)) {
    throw "mPdf.App.exe nao encontrado em $PublishDir apos o publish. Publish incompleto ou malsucedido."
}
$pdfiumMatch = Get-ChildItem -Path $PublishDir -Filter 'pdfium.dll' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pdfiumMatch) {
    throw "pdfium.dll (nativo, via Docnet.Core) nao encontrado em $PublishDir apos o publish. A renderizacao de PDF nao vai funcionar - abortando."
}
Write-Host "  OK: mPdf.App.exe e pdfium.dll ($($pdfiumMatch.FullName)) presentes." -ForegroundColor Green

# ---------------------------------------------------------------------------
# Plano 24: Preview Handler do Explorer (2a abordagem). NATIVE AOT: o handler e uma DLL NATIVA
# (mPdf.PreviewHandler.Aot.dll) sem CoreCLR - a 1a abordagem (comhost .NET) PENDURAVA o Explorer
# dentro do prevhost sandboxed de baixa integridade (bug conhecido do .NET Core em shell extensions).
# A DLL AOT + pdfium.dll sao os UNICOS arquivos - SEM dependencia do runtime .NET (resolve tambem o
# impasse das maquinas self-contained). Precisa do MSVC (link.exe): publicamos dentro do vcvars64 +
# IlcUseEnvironmentalTools=true (o findvcvarsall.bat do ILC nao acha o vswhere de forma confiavel).
# So a .dll + pdfium.dll vao para previewhandler\ (o .pdb nativo embute o caminho de build - NAO copiar).
# ---------------------------------------------------------------------------
Write-Host "=== [3b] Publicando mPdf.PreviewHandler.Aot (Native AOT, win-x64) em previewhandler\ ===" -ForegroundColor Cyan
$PreviewProject = Join-Path $RepoRoot 'src\mPdf.PreviewHandler.Aot\mPdf.PreviewHandler.Aot.csproj'
$PreviewAotOut = Join-Path $PSScriptRoot 'publish\preview-aot'
$PreviewDir = Join-Path $PublishDir 'previewhandler'
$vcvars = Find-VcVars
Write-Host "  vcvars: $vcvars"
if (Test-Path $PreviewAotOut) { Remove-Item $PreviewAotOut -Recurse -Force }
$aotCmd = "call `"$vcvars`" >nul 2>&1 && dotnet publish `"$PreviewProject`" -c Release -r win-x64 -p:IlcUseEnvironmentalTools=true -o `"$PreviewAotOut`""
& cmd /c $aotCmd
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (Native AOT) do preview handler falhou (exit code $LASTEXITCODE). Confira o VS Build Tools C++."
}
$aotDll = Join-Path $PreviewAotOut 'mPdf.PreviewHandler.Aot.dll'
if (-not (Test-Path $aotDll)) {
    throw "mPdf.PreviewHandler.Aot.dll nao encontrado em $PreviewAotOut - o publish AOT nao gerou a DLL nativa. Abortando."
}
$aotPdfium = Join-Path $PreviewAotOut 'pdfium.dll'
if (-not (Test-Path $aotPdfium)) {
    throw "pdfium.dll nao encontrado no publish AOT ($PreviewAotOut) - o render nao funcionaria no prevhost. Abortando."
}
# So os 2 arquivos de runtime (a DLL AOT ja embute Docnet/mPdf.Rendering; o .pdb nativo NAO vai - vaza caminho).
New-Item -ItemType Directory -Path $PreviewDir -Force | Out-Null
Copy-Item $aotDll $PreviewDir -Force
Copy-Item $aotPdfium $PreviewDir -Force
Write-Host "  OK: mPdf.PreviewHandler.Aot.dll + pdfium.dll presentes em previewhandler\." -ForegroundColor Green

# ---------------------------------------------------------------------------
# GUARDA DE VAZAMENTO DE CAMINHO (pos-publish, pre-ISCC).
#
# Os binarios do publish (.dll/.exe/.pdb) podem embutir o caminho absoluto da
# maquina de build nos simbolos - que costuma conter o nome do usuario/pastas
# pessoais entre o drive e a pasta do repositorio. O Directory.Build.props na
# raiz remapeia esse caminho (PathMap -> "/src/") justamente para isso NAO
# acontecer, mas a prova REAL e varrer os bytes do publish e ABORTAR o build se
# algum residuo aparecer. Sem esta guarda, um caminho pessoal poderia ser
# empacotado no instalador em silencio.
#
# Os tokens NAO sao hardcoded (este script e publicado no repo publico e nao
# pode conter o nome pessoal): sao DERIVADOS do proprio $RepoRoot em tempo de
# execucao. De um caminho como "<drive>\<usuario>\<pasta>\mPDF", os segmentos
# entre o drive (indice 0) e a pasta do repo (ultimo indice) sao exatamente as
# partes sensiveis; usamos o caminho inteiro + cada um desses segmentos.
#
# Le cada arquivo como latin1 (ISO-8859-1: 1 byte = 1 char, sem excecao de
# decodificacao, preserva todo valor de byte). Varre TODO o publish (incluindo
# .pdb, mesmo eles sendo excluidos do instalador via Excludes no .iss - defesa
# em profundidade).
Write-Host "=== [Guarda] Varrendo publish por caminho de build pessoal ===" -ForegroundColor Cyan
$segmentosRepo = $RepoRoot -split '[\\/]' | Where-Object { $_ -ne '' }
# Segmentos SENSIVEIS = os que ficam entre o drive (indice 0) e a pasta do repo
# (ultimo indice): o nome do usuario / pastas pessoais. O drive e a pasta do repo
# ("mPDF", que e publica) ficam de fora.
$segmentosSensiveis = @()
if ($segmentosRepo.Count -gt 2) {
    $segmentosSensiveis = $segmentosRepo[1..($segmentosRepo.Count - 2)]
}
# Tokens de caminho INTEIRO, nas duas variantes de separador (um .pdb/.dll pode
# gravar '\' ou '/'). Sao inequivocos - nao aparecem em texto legitimo.
$caminhosLiteral = @($RepoRoot, ($RepoRoot -replace '\\', '/'))
# Por segmento, casa so quando NAO cercado por [A-Za-z0-9] (ou seja, ladeado por
# separadores de caminho / bordas). Isso evita o UNICO falso-positivo conhecido:
# o slug publico do repositorio embutido no app (aviso AGPL "codigo-fonte em
# github.com/<usuario>/mpdf-desktop"), onde o nome do usuario aparece colado a
# letras (parte de um nome maior), nao ladeado por separadores. Num caminho real
# de build ("<drive>\<usuario>\...") o segmento vem sempre entre '\' e '\', entao
# um vazamento de verdade AINDA dispara.
$regexesSegmento = foreach ($seg in $segmentosSensiveis) {
    [regex]::new('(?<![A-Za-z0-9])' + [regex]::Escape($seg) + '(?![A-Za-z0-9])', 'IgnoreCase')
}
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$arquivosBin = Get-ChildItem -Path $PublishDir -Recurse -File -Include '*.dll', '*.exe', '*.pdb'
if ($arquivosBin.Count -eq 0) {
    throw "GUARDA: nenhum .dll/.exe/.pdb encontrado em $PublishDir para varrer - publish vazio nao e caminho feliz."
}
$vazamentos = New-Object System.Collections.Generic.List[string]
foreach ($bin in $arquivosBin) {
    $texto = $latin1.GetString([System.IO.File]::ReadAllBytes($bin.FullName))
    foreach ($cam in $caminhosLiteral) {
        if ($texto.IndexOf($cam, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $vazamentos.Add(("{0}  ->  caminho '{1}'" -f $bin.FullName, $cam))
        }
    }
    foreach ($rx in $regexesSegmento) {
        if ($rx.IsMatch($texto)) {
            $vazamentos.Add(("{0}  ->  segmento '{1}'" -f $bin.FullName, $rx.ToString()))
        }
    }
}
if ($vazamentos.Count -gt 0) {
    Write-Host "GUARDA DE CAMINHO RECUSOU O BUILD - caminho pessoal embutido em $($vazamentos.Count) ocorrencia(s):" -ForegroundColor Red
    foreach ($v in $vazamentos) { Write-Host "  $v" -ForegroundColor Red }
    throw "Caminho de build pessoal vazou nos binarios do publish. Confira o Directory.Build.props (PathMap/Deterministic) na raiz do repo. Build ABORTADO."
}
Write-Host "  Limpo: nenhum caminho pessoal em $($arquivosBin.Count) binario(s)." -ForegroundColor Green

Write-Host "=== [5/6] Extraindo versao do produto (fonte unica: csproj) ===" -ForegroundColor Cyan
[xml]$csprojXml = Get-Content $AppCsproj
$version = $csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Nao foi possivel extrair <Version> de $AppCsproj. Confira se a tag existe no PropertyGroup principal."
}
Write-Host "  Versao: $version" -ForegroundColor Green

Write-Host "=== [6/6] Gerando instalador (Inno Setup) ===" -ForegroundColor Cyan
$iscc = Find-Iscc
Write-Host "  ISCC: $iscc"
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
$isccArgs = @("/DMyAppVersion=$version", "/DPublishDir=$PublishDir")
if ($PasswordProvided) {
    $isccArgs += "/DInstallPassword=$Password"
}
$isccArgs += $IssFile
& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe falhou (exit code $LASTEXITCODE)."
}

Write-Host ""
Write-Host "=== Instalador gerado com sucesso em $OutputDir ===" -ForegroundColor Green
Get-ChildItem $OutputDir -Filter '*.exe' | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green
}

; mPDF - Script do instalador (Inno Setup 6)
;
; A partir da v1.1, o instalador tambem registra o mPDF como opcao para abrir arquivos
; .pdf (task "fileassoc", marcada por padrao - ver secao [Registry] abaixo), e a senha
; do instalador (Password=+Encryption=yes) e OPCIONAL: o projeto passou a ser distribuido
; como codigo aberto (AGPL-3.0), entao a senha deixou de ser mecanismo de contencao -
; ela continua disponivel apenas para quem quiser proteger o payload por outro motivo.
;
; Quando fornecida, a senha chega via parametro de linha de comando
; (/DInstallPassword=...), passado pelo tools/installer/build-installer.ps1 (parametro
; -Password, tambem opcional). Rodar o ISCC diretamente (sem passar pelo
; build-installer.ps1) tambem funciona: se /DInstallPassword for omitido, o instalador
; sai sem senha; se for passado vazio (ou so espacos), a guarda #if abaixo recusa
; compilar, com uma mensagem explicando o motivo.
;
; Parametros (via /D):
;   MyAppVersion    - OBRIGATORIO. Versao do produto, extraida do <Version> de
;                      mPdf.App.csproj (fonte UNICA — nao duplicar o numero aqui).
;   PublishDir      - OBRIGATORIO. Pasta gerada por `dotnet publish ... --self-contained
;                      true` (contem mPdf.App.exe, pdfium.dll nativo, e o resto da runtime).
;   InstallPassword - OPCIONAL. Senha do instalador. Omitido -> instalador sem senha.
;                      Fornecido vazio ou so espacos -> erro de compilacao (guarda abaixo).
;
; Exemplos de chamada manual (normalmente feita por build-installer.ps1):
;   iscc.exe /DMyAppVersion=1.1.0 /DPublishDir=C:\...\publish mpdf.iss                          (sem senha)
;   iscc.exe /DMyAppVersion=1.1.0 /DInstallPassword=xxxx /DPublishDir=C:\...\publish mpdf.iss    (com senha)

#ifndef MyAppVersion
  #error "MyAppVersion nao definido. Use /DMyAppVersion=<versao> (build-installer.ps1 faz isso automaticamente)."
#endif

#ifndef PublishDir
  #error "PublishDir nao definido. Use /DPublishDir=<pasta de publish> (build-installer.ps1 faz isso automaticamente)."
#endif

; InstallPassword agora e OPCIONAL (ver cabecalho). Quando fornecido (#ifdef), ainda
; precisa ser uma senha real - nao vazia, nao so espacos. Trim() e funcao nativa do ISPP
; (verificada com um .iss de teste antes de usar aqui) e cobre nao so "" literal, mas
; tambem uma senha so de espacos (" ") - sem o Trim, esse caso escaparia desta guarda e
; cairia no erro generico do proprio Inno la na frente, sem a mensagem pt-BR explicando
; o motivo.
#ifdef InstallPassword
  #if Trim(InstallPassword) == ""
    #error "InstallPassword foi fornecido vazio (ou so espacos), o que nao e permitido. Omita /DInstallPassword para gerar o instalador sem senha, ou forneca uma senha real (nao vazia)."
  #endif
#endif

#define MyAppName "mPDF"
#define MyAppPublisher "Projeto mPDF"
#define MyAppExeName "mPdf.App.exe"

[Setup]
; GUID fixo do produto mPDF - mantem identidade entre versoes para permitir
; upgrade/desinstalacao corretos. NAO gerar um novo a cada build.
AppId={{C267C7BD-85F1-449C-8B1D-69DA6013BC8D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={autopf}\mPDF
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Assistente enxuto: tira a tela de boas-vindas e a tela "pronto para instalar" - o
; instalador de nova instalacao vai direto de [escolher pasta] -> instalar -> concluir.
; A pagina de DIRETORIO continua ATIVA de proposito (NAO ha DisableDirPage) - o usuario
; escolhe onde instalar. A instalacao silenciosa (/VERYSILENT) ja ignorava todas essas
; paginas de qualquer forma - nao ha mudanca de comportamento no caminho silencioso.
DisableWelcomePage=yes
DisableReadyPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=Output
OutputBaseFilename=mPDF-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\..\src\mPdf.App\Assets\mpdf.ico
; Imagens on-brand do assistente (fundo escuro #161826 + logo mPDF) - geradas por
; gerar-imagens-wizard.ps1 e commitadas junto (o build nao depende de rodar o gerador).
; Tamanhos classicos do Inno: banner grande 164x314, icone pequeno 55x58.
WizardImageFile=wizard-banner.bmp
WizardSmallImageFile=wizard-small.bmp
; Instalacao para todo o computador (nao por usuario) - maquinas da organizacao.
PrivilegesRequired=admin
#ifdef InstallPassword
; Senha + criptografia do payload - OPCIONAL (ver cabecalho). So aparece no instalador
; quando /DInstallPassword foi fornecido no build.
Password={#InstallPassword}
Encryption=yes
#endif
; Publish self-contained win-x64 - so roda em Windows x64.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Pagina de avisos (o que o mPDF faz + licenca AGPL do iText e afins) antes da instalacao.
InfoBeforeFile=..\..\docs\licencas\AVISO-mPDF.txt
; Registra alteracoes de associacao de arquivo (task "fileassoc", secao [Registry]
; abaixo) - avisa o Windows/Explorer para atualizar o icon cache pos-instalacao.
ChangesAssociations=yes

; --- Plano 18 (Task 2): atualizacao SILENCIOSA + relaunch ---------------------------------------------
; AppMutex: nome GLOBAL de um mutex que o app SEGURA pela vida inteira (constante
; SingleInstanceNames.UpdateAppMutexName no codigo do app - FONTE UNICA DA VERDADE; um teste estrutural
; garante que este valor == aquela constante, senao o Inno nao detecta a instancia). Com ele o
; instalador detecta o mPDF rodando e, junto de CloseApplications=yes, ESPERA a instancia fechar antes
; de trocar os arquivos do .exe em uso (troca robusta durante a atualizacao silenciosa). O valor DEVE
; bater EXATAMENTE com a constante do app - NAO editar um lado sem o outro.
AppMutex=Global\mPDF-Update-Mutex-C267C7BD
CloseApplications=yes
; RestartApplications=no: o relaunch do app e feito pela secao [Run] abaixo (runasoriginaluser, des-
; elevado), NAO pelo mecanismo de Restart Manager do Inno (que reabriria elevado, como o instalador).
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na Area de Trabalho"; GroupDescription: "Atalhos adicionais:"; Flags: unchecked
Name: "fileassoc"; Description: "Registrar o mPDF como opção para abrir arquivos PDF"; GroupDescription: "Associação de arquivos:"

[Files]
; Publicacao self-contained completa (mPdf.App.exe + runtime .NET + pdfium.dll nativo).
; Excludes "*.pdb": os simbolos de depuracao nao sao necessarios ao usuario final,
; reduzem tamanho e superficie, e sao os arquivos que mais carregam caminhos de build
; embutidos (mesmo com o PathMap em Directory.Build.props, ficam de fora por garantia).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
; Textos de licenca de terceiros (AGPL/MIT/BSD/Apache/OFL) + aviso do mPDF, para consulta pos-instalacao.
Source: "..\..\docs\licencas\*"; DestDir: "{app}\licencas"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Associacao opcional de .pdf com o mPDF (task "fileassoc", marcada por padrao). Tudo
; abaixo so e escrito quando essa task esta marcada - se o usuario desmarcar na tela de
; tarefas, nenhuma chave/valor desta secao e criado. Isto REGISTRA o mPDF como uma opcao
; em "Abrir com" / "Aplicativos padrao" - NAO forca o mPDF como o app padrao de .pdf
; (essa escolha continua sendo do usuario, via Configuracoes do Windows).

; ProgID proprio do mPDF - identifica o "tipo de documento" que o mPDF sabe abrir.
Root: HKLM; Subkey: "Software\Classes\mPDF.Document"; ValueType: string; ValueName: ""; ValueData: "Documento PDF (mPDF)"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKLM; Subkey: "Software\Classes\mPDF.Document"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Documento PDF (mPDF)"; Tasks: fileassoc
Root: HKLM; Subkey: "Software\Classes\mPDF.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc
Root: HKLM; Subkey: "Software\Classes\mPDF.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc

; Adiciona o mPDF.Document a lista de ProgIDs alternativos do .pdf (convencao
; OpenWithProgids do Windows) - e assim que o mPDF aparece no menu "Abrir com" do
; Explorer para arquivos .pdf. So o VALOR proprio (mPDF.Document) e apagado no
; desinstalador - a chave .pdf\OpenWithProgids em si e de outros aplicativos tambem, e
; NUNCA e tocada ou removida por este instalador.
Root: HKLM; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "mPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc

; Capabilities do mPDF - e o que faz o mPDF aparecer em Configuracoes > Aplicativos
; padrao do Windows (ao lado do Adobe Reader e outros leitores de PDF instalados). A
; chave Software\mPDF inteira (Capabilities + FileAssociations) e removida no
; desinstalador - e exclusiva do mPDF, nenhum outro aplicativo escreve nela.
Root: HKLM; Subkey: "Software\mPDF"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKLM; Subkey: "Software\mPDF\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "mPDF"; Tasks: fileassoc
Root: HKLM; Subkey: "Software\mPDF\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Visualizador e assinador de PDF"; Tasks: fileassoc
Root: HKLM; Subkey: "Software\mPDF\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "mPDF.Document"; Tasks: fileassoc

; Registra o mPDF na lista central de aplicativos do Windows (RegisteredApplications)
; apontando para as Capabilities acima - passo final para o mPDF aparecer em
; Configuracoes > Aplicativos padrao. Essa chave e compartilhada por TODOS os
; aplicativos instalados na maquina - so o VALOR proprio (mPDF) e apagado no
; desinstalador, a chave RegisteredApplications e os valores de outros aplicativos
; NUNCA sao tocados.
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "mPDF"; ValueData: "Software\mPDF\Capabilities"; Flags: uninsdeletevalue; Tasks: fileassoc

; A configuracao do usuario (%AppData%\mPDF) NAO e tocada pelo instalador nem pelo
; desinstalador - sobrevive a desinstalacoes/atualizacoes (ver docs/rollout.md).

[Run]
; Plano 18 (Task 2): reabre o mPDF ao FIM da instalacao. Funciona TAMBEM em modo silencioso porque NAO
; tem `skipifsilent` (com skipifsilent o relaunch seria pulado no /VERYSILENT, e o app nao voltaria
; sozinho apos a atualizacao silenciosa). `runasoriginaluser`: o app reaberto roda DES-ELEVADO, como o
; usuario, nao como admin (o instalador roda elevado - sem essa flag o app herdaria a elevacao).
; `nowait`: nao bloqueia o encerramento do instalador esperando o app; `postinstall`: passo pos-copia.
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar o mPDF"; Flags: nowait postinstall runasoriginaluser

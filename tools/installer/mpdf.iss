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
; Plano 24: pre-visualizacao de PDF no painel do Explorer (preview handler COM). Marcada por padrao,
; desmarcavel. SUBSTITUI o preview handler de PDF de outros apps (Adobe/Edge) enquanto marcada -
; e o comportamento desejado (ver mPDF no painel). Desinstalar remove o registro.
Name: "previewhandler"; Description: "Pré-visualizar PDFs no painel de visualização do Explorer"; GroupDescription: "Integração com o Windows:"
; Task 3 (impressao pelo menu de contexto): registra os verbos classicos "Imprimir com mPDF" e
; "Impressao avancada (mPDF)" no menu de botao direito de qualquer .pdf. Marcada por padrao,
; desmarcavel. Ver bloco [Registry] abaixo (SystemFileAssociations\.pdf\shell).
Name: "contextprint"; Description: "Adicionar ""Imprimir com mPDF"" ao menu do botão direito em arquivos PDF"; GroupDescription: "Integração com o Windows:"

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

; ---------------------------------------------------------------------------
; Plano 24 (2a abordagem): PREVIEW HANDLER (task "previewhandler"). Registra o servidor COM como o
; preview handler de .pdf. O handler e uma DLL NATIVE AOT (mPdf.PreviewHandler.Aot.dll) - codigo nativo,
; SEM CoreCLR. A 1a abordagem (comhost .NET framework-dependent) PENDURAVA o Explorer dentro do prevhost
; sandboxed de baixa integridade (limitacao conhecida do .NET Core em shell extensions) - a versao AOT
; resolve isso e NAO exige o runtime .NET na maquina (a DLL exporta ela mesma DllGetClassObject). O
; InprocServer32 aponta direto pra DLL nativa (ThreadingModel=Apartment).
; AppID = a AppID do surrogate "prevhost" 64-BIT do Windows ({6d2b5079-...} = System32\prevhost.exe,
; "Preview Handler Surrogate Host", ja registrada pelo sistema com DllSurrogate) -> o handler roda
; ISOLADO no prevhost.exe 64-bit, nunca no Explorer. CRITICO: NAO usar {534A1E02-...} = o surrogate
; 32-BIT (SysWOW64\prevhost.exe); ele NAO consegue carregar nossa DLL x64 e o Explorer mostra
; "este arquivo nao pode ser visualizado" (REGDB_E_CLASSNOTREG 0x80040154 na ativacao do surrogate).
; CLSID do handler = {9A675AC7-E3B9-492B-A94C-9669CAD164BF} (PdfPreviewHandler.Clsid).
; Registro na view 64-bit (ArchitecturesInstallIn64BitMode) - o handler e x64, o prevhost 64-bit.
Root: HKLM; Subkey: "Software\Classes\CLSID\{{9A675AC7-E3B9-492B-A94C-9669CAD164BF}"; ValueType: string; ValueName: ""; ValueData: "mPDF - Pré-visualização de PDF"; Flags: uninsdeletekey; Tasks: previewhandler
Root: HKLM; Subkey: "Software\Classes\CLSID\{{9A675AC7-E3B9-492B-A94C-9669CAD164BF}"; ValueType: string; ValueName: "AppID"; ValueData: "{{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}"; Tasks: previewhandler
Root: HKLM; Subkey: "Software\Classes\CLSID\{{9A675AC7-E3B9-492B-A94C-9669CAD164BF}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "{app}\previewhandler\mPdf.PreviewHandler.Aot.dll"; Tasks: previewhandler
; ThreadingModel = Apartment (STA): preview handlers criam janela e bombeiam mensagens (exigem STA).
; (Na 1a abordagem, "Both" ainda deixava o prevhost hospedar no MTA e corrompia a leitura do IStream;
; mantemos Apartment por corretude.)
Root: HKLM; Subkey: "Software\Classes\CLSID\{{9A675AC7-E3B9-492B-A94C-9669CAD164BF}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"; Tasks: previewhandler
; Lista central de preview handlers do Windows - valor proprio ({clsid}) apagado no desinstalador.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\PreviewHandlers"; ValueType: string; ValueName: "{{9A675AC7-E3B9-492B-A94C-9669CAD164BF}"; ValueData: "mPDF - Pré-visualização de PDF"; Flags: uninsdeletevalue; Tasks: previewhandler
; Slot IPreviewHandler ({8895b1c6-...}) da extensao .pdf -> nosso CLSID. Vale pra TODOS os .pdf
; (independe do app padrao). uninsdeletekey remove no desinstalador.
Root: HKLM; Subkey: "Software\Classes\.pdf\shellex\{{8895B1C6-B41F-4C1C-A562-0D564250836F}"; ValueType: string; ValueName: ""; ValueData: "{{9A675AC7-E3B9-492B-A94C-9669CAD164BF}"; Flags: uninsdeletekey; Tasks: previewhandler

; ---------------------------------------------------------------------------
; Task 3 (impressao pelo menu de contexto, task "contextprint"): dois verbos classicos sob
; SystemFileAssociations\.pdf\shell - vale pra TODO .pdf, independente do leitor padrao (mesma
; logica do preview handler acima, NAO da associacao "fileassoc": nao precisa o mPDF ser o app
; padrao pra esses itens aparecerem no menu de botao direito). "Imprimir com mPDF" chama o app com
; /print (impressao silenciosa - ver ImpressaoContextoService); "Impressao avancada (mPDF)" chama
; com /printadv (abre o dialogo avancado de impressao). uninsdeletekey SO na chave raiz de cada
; verbo (mPDFImprimir / mPDFImprimirAvancado) - remover a raiz no desinstalador leva a subchave
; \command junto, entao ela nao repete a flag.
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\mPDFImprimir"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Imprimir com mPDF"; Flags: uninsdeletekey; Tasks: contextprint
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\mPDFImprimir"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: contextprint
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\mPDFImprimir\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" /print ""%1"""; Tasks: contextprint
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\mPDFImprimirAvancado"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Impressão avançada (mPDF)"; Flags: uninsdeletekey; Tasks: contextprint
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\mPDFImprimirAvancado"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: contextprint
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\mPDFImprimirAvancado\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" /printadv ""%1"""; Tasks: contextprint

; A configuracao do usuario (%AppData%\mPDF) NAO e tocada pelo instalador nem pelo
; desinstalador - sobrevive a desinstalacoes/atualizacoes (ver docs/rollout.md).

[Run]
; Plano 18 (Task 2): reabre o mPDF ao FIM da instalacao. Funciona TAMBEM em modo silencioso porque NAO
; tem `skipifsilent` (com skipifsilent o relaunch seria pulado no /VERYSILENT, e o app nao voltaria
; sozinho apos a atualizacao silenciosa). `runasoriginaluser`: o app reaberto roda DES-ELEVADO, como o
; usuario, nao como admin (o instalador roda elevado - sem essa flag o app herdaria a elevacao).
; `nowait`: nao bloqueia o encerramento do instalador esperando o app; `postinstall`: passo pos-copia.
; `Check: DeveReabrir`: o update do RMM (`/update`) lanca o instalador com o parametro custom /NORELAUNCH
; (ver SilentUpdateInstaller.ParametroNaoReabrir) para NAO reabrir o app na estacao - a funcao [Code]
; abaixo devolve False quando /NORELAUNCH esta na linha de comando, pulando este relaunch. O botao
; "Atualizar" da UI NAO passa /NORELAUNCH, entao continua reabrindo (comportamento identico ao de hoje).
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar o mPDF"; Flags: nowait postinstall runasoriginaluser; Check: DeveReabrir

[Code]
// Devolve False quando /NORELAUNCH esta na linha de comando (update silencioso do RMM: nao reabre o
// mPDF na estacao). Sem o parametro, devolve True e o relaunch da secao [Run] acontece como sempre.
function DeveReabrir(): Boolean;
var
  i: Integer;
begin
  Result := True;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/NORELAUNCH') = 0 then
    begin
      Result := False;
      Exit;
    end;
end;

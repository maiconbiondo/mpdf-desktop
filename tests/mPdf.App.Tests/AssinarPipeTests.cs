using System.IO;
using System.Threading;
using System.Threading.Tasks;
using mPdf.App.Services;
using mPdf.App.ViewModels;
using mPdf.Documents;
using mPdf.Signing;
using Xunit;

namespace mPdf.App.Tests;

file sealed class SemDialogoArquivo : IFileDialogService
{
    public string? PickPdfToOpen() => null;
    public string? PickPdfToSaveAs(string currentPath) => null;
    public string? PickImageToImport() => null;
    public string? PickPdfToSave(string suggestedName) => null;
}

file sealed class ConfirmCloseCancelar : IConfirmCloseService
{
    public CloseConfirmation Confirm(string documentTitle) => CloseConfirmation.Cancel;
}

/// v2.13.0 (verbo /assinar): nível VM + pipe real. O diálogo de assinatura é o FAKE de
/// `SignCommandTests` (devolve null = usuário cancelou) — nenhum repositório de certificados real é
/// aberto (catálogo fake vazio) e nada é assinado de verdade; `CallCount` prova que o SignCommand do
/// documento aberto chegou ao diálogo.
public class AssinarPipeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mpdf-assinar-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private readonly List<string> _erros = new();

    private MainViewModel Vm(FakeSignDialogService sign) =>
        new(new SemDialogoArquivo(),
            new RecentFilesStore(_dir),
            msg => _erros.Add(msg),
            new AppConfig(Path.Combine(_dir, "config")),
            new ConfirmCloseCancelar(),
            stampGallery: new StampGallery(Path.Combine(_dir, "carimbos")),
            notifyInfo: _ => { },
            listSigningCertificates: () => Array.Empty<SigningCertificateInfo>(),
            signDialog: sign);

    private string CopiaDoFixture(string nome = "Relatório exportado.pdf")
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, nome);
        File.Copy(Path.Combine(Fixtures.Root, "fixture-a4.pdf"), p);
        return p;
    }

    private static (string Mutex, string Pipe) NewNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return ($"mpdf-test-mutex-{id}", $"mpdf-test-pipe-{id}");
    }

    // O Mutex nomeado tem afinidade de THREAD (ReleaseMutex no Dispose precisa da mesma thread que o
    // adquiriu) — por isso toda a parte do pipe roda SÍNCRONA, sem await entre TryAcquire e Dispose.

    /// Primária + secundária pelo pipe REAL; devolve o que a primária recebeu (comando ou null) e se
    /// algum evento de caminho puro disparou.
    private static (ComandoInstancia? Comando, bool CaminhoPuro) EnviarPeloPipe(string linha, bool esperarComando)
    {
        var (mutexName, pipeName) = NewNames();
        using var primaria = new SingleInstanceService(mutexName, pipeName);
        ComandoInstancia? recebido = null;
        bool caminhoPuro = false;
        var chegou = new ManualResetEventSlim(false);
        primaria.PathReceived += _ => caminhoPuro = true;
        primaria.CommandReceived += c => { recebido = c; chegou.Set(); };
        Assert.True(primaria.TryAcquire(null));

        using (var secundaria = new SingleInstanceService(mutexName, pipeName))
            Assert.False(secundaria.TryAcquire(linha));

        if (esperarComando) Assert.True(chegou.Wait(TimeSpan.FromSeconds(5)), "primária não recebeu o comando a tempo");
        else Thread.Sleep(300); // só evidencia AUSÊNCIA de evento
        bool caminhoPuroAntes = Volatile.Read(ref caminhoPuro); // antes do envio de controle abaixo

        // o servidor continua escutando: um caminho válido em seguida ainda chega
        var ok = new ManualResetEventSlim(false);
        primaria.PathReceived += _ => ok.Set();
        using (var terceira = new SingleInstanceService(mutexName, pipeName))
            Assert.False(terceira.TryAcquire(@"D:\ok.pdf"));
        Assert.True(ok.Wait(TimeSpan.FromSeconds(5)), "servidor não voltou a escutar");

        return (recebido, caminhoPuroAntes);
    }

    [Fact] // a linha ?assinar|<pdf> recebida pela primária abre o documento e chama PromptForSignature 1x
    public async Task Pipe_LinhaDeAssinar_AbreEAssina()
    {
        var pdf = CopiaDoFixture();
        var sign = new FakeSignDialogService(result: null);
        var vm = Vm(sign);

        var (cmd, caminhoPuro) = EnviarPeloPipe(ProtocoloInstanciaUnica.MontarLinha(VerboInstancia.Assinar, pdf), esperarComando: true);
        Assert.NotNull(cmd);
        Assert.False(caminhoPuro); // a linha de comando nunca vira "abrir caminho"
        await vm.ExecutarComandoAsync(cmd!.Value); // o que App.HandleExternalCommandAsync faz (já na UI)

        var doc = Assert.Single(vm.Documents);
        Assert.Equal(pdf, doc.Session.FilePath);
        Assert.Same(doc, vm.SelectedDocument);
        Assert.Equal(1, sign.CallCount);
        Assert.Empty(_erros);
    }

    [Fact] // ?qualquercoisa|x não abre nada nem lança exceção (o servidor segue vivo)
    public void Pipe_LinhaDesconhecida_Ignorada()
    {
        var sign = new FakeSignDialogService(result: null);
        var vm = Vm(sign);

        var (cmd, caminhoPuro) = EnviarPeloPipe("?qualquercoisa|x", esperarComando: false);

        Assert.Null(cmd);
        Assert.False(caminhoPuro);
        Assert.Empty(vm.Documents);
        Assert.Equal(0, sign.CallCount);
    }

    [Fact] // 1ª instância: ExecutarComandoAsync(Assinar) abre e dispara o SignCommand do documento
    public async Task AbrirEAssinar_AbreEChamaDialogoDeAssinatura()
    {
        var pdf = CopiaDoFixture();
        var sign = new FakeSignDialogService(result: null);
        var vm = Vm(sign);

        await vm.AbrirEAssinarAsync(pdf);

        Assert.Single(vm.Documents);
        Assert.Equal(1, sign.CallCount);
        Assert.Empty(_erros);
    }

    [Fact] // já aberto: focaliza a aba existente (sem duplicar) e assina
    public async Task AbrirEAssinar_JaAberto_FocalizaSemDuplicar()
    {
        var pdf = CopiaDoFixture();
        var outro = CopiaDoFixture("outro.pdf");
        var sign = new FakeSignDialogService(result: null);
        var vm = Vm(sign);
        await vm.OpenPath(pdf);
        await vm.OpenPath(outro);
        var alvo = vm.Documents[0];
        Assert.NotSame(alvo, vm.SelectedDocument);

        await vm.AbrirEAssinarAsync(pdf.ToUpperInvariant());

        Assert.Equal(2, vm.Documents.Count);
        Assert.Same(alvo, vm.SelectedDocument);
        Assert.Equal(1, sign.CallCount);
    }

    [Fact] // arquivo inexistente -> mensagem pt-BR pelo mesmo canal de erro do OpenPath, sem diálogo
    public async Task AbrirEAssinar_ArquivoInexistente_NotificaErro()
    {
        var sign = new FakeSignDialogService(result: null);
        var vm = Vm(sign);

        await vm.AbrirEAssinarAsync(Path.Combine(_dir, "nao-existe.pdf"));

        Assert.Empty(vm.Documents);
        Assert.Equal(0, sign.CallCount);
        var erro = Assert.Single(_erros);
        Assert.Contains("Não foi possível assinar", erro);
    }

    [Fact] // arquivo existente que não é PDF (ex.: imagem) -> erro, nunca converte/abre
    public async Task AbrirEAssinar_NaoPdf_NotificaErro()
    {
        Directory.CreateDirectory(_dir);
        var img = Path.Combine(_dir, "foto.jpg");
        File.WriteAllBytes(img, new byte[] { 0xFF, 0xD8, 0xFF });
        var sign = new FakeSignDialogService(result: null);
        var vm = Vm(sign);

        await vm.AbrirEAssinarAsync(img);

        Assert.Empty(vm.Documents);
        Assert.Equal(0, sign.CallCount);
        Assert.Single(_erros);
    }

    // ---- Fix round 1 (revisão da Task 3) -----------------------------------------------------------

    [Fact] // CanSign falso (colocação de carimbo já ativa) -> exatamente 1 erro pt-BR, diálogo NUNCA chamado
    public async Task AbrirEAssinar_CanSignFalso_NotificaErroSemDialogo()
    {
        var pdf = CopiaDoFixture();
        var sign = new FakeSignDialogService(result: null);
        var vm = Vm(sign);
        await vm.OpenPath(pdf);
        var doc = Assert.Single(vm.Documents);
        doc.ActiveTool = mPdf.App.ViewModels.AnnotationTool.SignatureStamp;
        Assert.False(doc.SignCommand.CanExecute(null)); // pré-condição do cenário

        await vm.AbrirEAssinarAsync(pdf);

        Assert.Single(vm.Documents);
        Assert.Equal(0, sign.CallCount);
        var erro = Assert.Single(_erros);
        Assert.Equal("Este documento não pode ser assinado agora.", erro);
    }

    [Fact] // .pdf que existe mas é lixo -> OpenPath falha e notifica 1 erro; nenhuma chamada de assinatura
    public async Task AbrirEAssinar_PdfCorrompido_UmErroSemAssinar()
    {
        Directory.CreateDirectory(_dir);
        var lixo = Path.Combine(_dir, "corrompido.pdf");
        File.WriteAllBytes(lixo, System.Text.Encoding.ASCII.GetBytes("isto nao e um PDF de verdade"));
        var sign = new FakeSignDialogService(result: null);
        var vm = Vm(sign);

        await vm.AbrirEAssinarAsync(lixo);

        Assert.Empty(vm.Documents);
        Assert.Equal(0, sign.CallCount);
        Assert.Single(_erros);
    }
}

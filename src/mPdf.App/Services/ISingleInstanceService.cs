namespace mPdf.App.Services;

/// Task 1 (Plano 6): contrato de instância única (mutex nomeado + pipe nomeado) atrás de interface —
/// mesma disciplina de seam do resto do app (IFileDialogService, IConfirmCloseService, ...). O ÚNICO
/// consumidor de produção é App.xaml.cs (não testável headless, mesma categoria dos handlers de
/// crash — ver doc XML de App.OnDispatcherUnhandledException); a interface existe para simetria
/// arquitetural e para deixar explícito o contrato, mesmo sem um fake dedicado: os testes
/// (SingleInstanceServiceTests) exercitam a implementação REAL com nomes de mutex/pipe únicos por
/// teste, nunca os nomes fixos de produção (SingleInstanceNames) — mockar mutex/pipe do SO não provaria
/// nada sobre a interação real com o SO, que é justamente o que este serviço existe pra fazer certo.
public interface ISingleInstanceService : IDisposable
{
    /// Disparado (numa thread de BACKGROUND — o loop do pipe, nunca a thread de UI) quando uma OUTRA
    /// instância encaminha um caminho. O assinante (App.xaml.cs) precisa fazer o marshal pra thread de
    /// UI antes de tocar em MainWindow/ViewModel (afinidade STA do WPF).
    event Action<string>? PathReceived;

    /// Tenta se tornar a instância PRIMÁRIA (dona do mutex nomeado). Se conseguir, já deixa o loop de
    /// escuta do pipe rodando em background e devolve true — o chamador segue o startup normal. Se já
    /// existe uma primária, conecta como CLIENTE no pipe dela, encaminha `pathToForward` (se não for
    /// nulo/vazio) e devolve false — o chamador deve encerrar SEM criar nenhuma janela.
    bool TryAcquire(string? pathToForward);
}

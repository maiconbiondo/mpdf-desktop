namespace mPdf.App.Services;

/// <summary>
/// Decide, de forma PURA e testável, se uma exceção não tratada — já registrada no
/// <see cref="CrashLog"/> pelo chamador ANTES de consultar este gate — deve também mostrar um
/// `MessageBox` modal, ou se deve ficar só no log (revisão final pré-merge, Obs 21). Extraído do
/// handler seguindo o mesmo exemplar de <see cref="CrashLog"/>: a decisão fica num helper testável,
/// o handler em si (`App.OnDispatcherUnhandledException`) permanece não-testável headless (precisa de
/// um `Dispatcher.Run()` bombeando a fila de verdade — ver doc XML dele).
///
/// 3 EIXOS documentados (Obs 21):
///
/// TAXA — um timer/render-loop que falha REPETIDAMENTE não pode virar um loop infinito de caixas
/// modais empilhadas esperando clique. Mais de <see cref="MaxDialogsPerWindow"/> caixas aprovadas
/// dentro de <see cref="Window"/> (10s, janela DESLIZANTE) faz o gate parar de aprovar caixas NOVAS
/// (só log) até a janela liberar espaço de novo — não é um desligamento permanente, é um teto por
/// janela de tempo.
///
/// REENTRÂNCIA — um `MessageBox.Show` modal BOMBEIA a fila de mensagens do Dispatcher enquanto está
/// aberto (é assim que uma janela modal WPF processa clique/repaint sem travar o processo) — então uma
/// SEGUNDA exceção não tratada pode chegar e ser reentrante DE VERDADE (não só hipotética) enquanto a
/// 1ª caixa ainda está na tela. `TryEnter` devolve `false` incondicionalmente enquanto uma chamada
/// anterior ainda não chamou `Exit` — nunca uma 2ª caixa sobreposta, mesmo que a janela de taxa (acima)
/// ainda tivesse espaço.
///
/// RESÍDUO — nenhum estado sobrevive além do necessário pra decidir a PRÓXIMA chamada: a marca de
/// "mostrando agora" é limpa no `finally` do chamador (mesmo se `MessageBox.Show` lançar — não deveria,
/// mas não pode travar o gate permanentemente em "mostrando"); a janela deslizante de timestamps
/// descarta entradas mais velhas que <see cref="Window"/> a CADA chamada de `TryEnter` (não só quando o
/// teto é atingido), então o gate nunca acumula memória indefinidamente numa sessão de app longa.
///
/// Fonte de tempo INJETÁVEL (parâmetro explícito, não `DateTimeOffset.Now` lido internamente) — testes
/// provam a janela de 10s sem `Task.Delay`/sleep nenhum, avançando um relógio fake.
/// </summary>
public sealed class CrashDialogGate
{
    public const int MaxDialogsPerWindow = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly List<DateTimeOffset> _recentDialogTimestamps = [];
    private bool _currentlyShowing;

    /// <summary>
    /// Tenta entrar na seção crítica "vou mostrar um MessageBox agora". `true` = pode mostrar; o
    /// chamador DEVE chamar <see cref="Exit"/> num `finally` ao redor de `MessageBox.Show` (mesmo se ela
    /// lançar). `false` = não mostrar (reentrância OU teto de taxa atingido) — o chamador só loga (já
    /// feito ANTES de chamar este método) e segue.
    /// </summary>
    public bool TryEnter(DateTimeOffset now)
    {
        if (_currentlyShowing) return false; // REENTRÂNCIA — ver doc XML da classe

        _recentDialogTimestamps.RemoveAll(t => now - t > Window); // RESÍDUO — poda a cada chamada
        if (_recentDialogTimestamps.Count >= MaxDialogsPerWindow) return false; // TAXA

        _recentDialogTimestamps.Add(now);
        _currentlyShowing = true;
        return true;
    }

    /// <summary>Libera a seção crítica — chamar SEMPRE após um `TryEnter` que devolveu `true`, mesmo se
    /// `MessageBox.Show` lançou (por isso o chamador usa `finally`).</summary>
    public void Exit() => _currentlyShowing = false;
}

using System.Threading;
using System.Threading.Tasks;

namespace mPdf.App.Services;

/// Roda um trabalho numa thread dedicada em apartamento STA e devolve um `Task<T>` aguardável.
///
/// POR QUÊ (bug de assinatura A3/token relatado em campo — cartão AC SOLUTI via leitora OMNIKEY,
/// middleware SafeSign IC): a assinatura chama, lá no fundo, `RSACng.SignData` sobre a chave do
/// token, e o CSP/middleware do fabricante precisa ABRIR A JANELA DE PIN. Numa thread do POOL
/// (`Task.Run` = apartamento MTA), vários middlewares de smartcard — o SafeSign entre eles — NÃO
/// conseguem exibir esse diálogo e falham ANTES de pedir o PIN ("não pede o PIN e dá erro"); assim que
/// o PIN entra em cache do token, passa a funcionar em qualquer thread, escondendo a causa. Rodar a
/// assinatura numa thread STA (o mesmo apartamento da UI thread do WPF, mas sem BLOQUEAR a UI) faz o
/// diálogo de PIN aparecer normalmente. Comprovado na máquina do usuário: em STA o PIN aparece e
/// assina; em MTA falha sem pedir PIN. Só o caminho que TOCA A CHAVE PRIVADA (assinar) precisa disto —
/// leitura de assinaturas/HasSignatures/CanFillIncremental continuam no pool (não pedem PIN).
internal static class StaTask
{
    public static Task<T> Run<T>(System.Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (System.Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "mPdfAssinaturaSTA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}

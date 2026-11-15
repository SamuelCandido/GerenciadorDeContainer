using System.Net;
using System.Net.Sockets;

namespace MiniDocker.Nucleo.Rede;

/// <summary>
/// Publica uma porta: escuta no host e repassa cada conexão para a porta
/// interna do container. Roda dentro do processo dono do container (o shim),
/// então a porta existe exatamente enquanto o container roda. É o que o
/// <c>docker-proxy</c> faz — no Linux o Docker ainda tem NAT do kernel
/// (iptables) por baixo; aqui o proxy é o caminho todo.
///
/// Cada conexão vira dois "bombeamentos" em paralelo, um em cada sentido.
/// Quando um lado fecha a escrita, o proxy fecha a escrita do outro
/// (<i>half-close</i>), e a conexão termina quando os dois sentidos acabarem.
/// </summary>
public sealed class ProxyTcp : IDisposable
{
    private readonly TcpListener ouvinte;
    private readonly int portaDestino;
    private readonly CancellationTokenSource cancelamento = new();
    private readonly Task aceitando;
    private long conexoes;
    private long bytes;

    /// <param name="portaHost">0 sorteia uma porta livre.</param>
    /// <exception cref="InvalidOperationException">A porta do host já está em uso.</exception>
    public ProxyTcp(IPAddress ip, int portaHost, int portaDestino)
    {
        this.portaDestino = portaDestino;
        ouvinte = new TcpListener(ip, portaHost);
        try
        {
            ouvinte.Start();
        }
        catch (SocketException excecao) when (excecao.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException(
                $"A porta {portaHost} do host ja esta em uso por outro programa ou container.", excecao);
        }

        aceitando = Task.Run(AceitarAsync);
    }

    public int PortaHost => ((IPEndPoint)ouvinte.LocalEndpoint).Port;

    public long Conexoes => Interlocked.Read(ref conexoes);

    public long BytesTransferidos => Interlocked.Read(ref bytes);

    /// <summary>
    /// Uma porta livre do host para o programa do container escutar. O kernel
    /// escolhe (bind na porta 0); há uma janela até o programa ocupá-la, em que
    /// outro poderia pegá-la — o mesmo risco de qualquer sorteio de porta.
    /// </summary>
    public static int SortearPortaLivre()
    {
        var temporario = new TcpListener(IPAddress.Loopback, 0);
        temporario.Start();
        var porta = ((IPEndPoint)temporario.LocalEndpoint).Port;
        temporario.Stop();
        return porta;
    }

    private async Task AceitarAsync()
    {
        while (!cancelamento.IsCancellationRequested)
        {
            TcpClient cliente;
            try
            {
                cliente = await ouvinte.AcceptTcpClientAsync(cancelamento.Token);
            }
            catch (Exception) when (cancelamento.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            Interlocked.Increment(ref conexoes);
            _ = Task.Run(() => EncaminharAsync(cliente));
        }
    }

    private async Task EncaminharAsync(TcpClient cliente)
    {
        using (cliente)
        using (var destino = new TcpClient())
        {
            try
            {
                await destino.ConnectAsync(IPAddress.Loopback, portaDestino, cancelamento.Token);
            }
            catch (Exception)
            {
                // O programa do container ainda não está escutando (subindo) ou
                // caiu: a conexão é recusada, como seria sem o proxy.
                return;
            }

            await Task.WhenAll(
                BombearAsync(cliente.Client, destino.Client),
                BombearAsync(destino.Client, cliente.Client));
        }
    }

    private async Task BombearAsync(Socket origem, Socket destino)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int lidos;
            while ((lidos = await origem.ReceiveAsync(buffer, SocketFlags.None, cancelamento.Token)) > 0)
            {
                await destino.SendAsync(buffer.AsMemory(0, lidos), SocketFlags.None, cancelamento.Token);
                Interlocked.Add(ref bytes, lidos);
            }

            destino.Shutdown(SocketShutdown.Send);
        }
        catch (Exception)
        {
            // Um lado caiu ou o container parou: a conexão acaba aqui.
        }
    }

    public void Dispose()
    {
        cancelamento.Cancel();
        ouvinte.Stop();
        try
        {
            aceitando.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        cancelamento.Dispose();
    }
}

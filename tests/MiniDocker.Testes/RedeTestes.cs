using System.Net;
using System.Net.Sockets;
using System.Text;
using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Rede;
using MiniDocker.Nucleo.Servicos;

namespace MiniDocker.Testes;

public class RedeTestes : IDisposable
{
    private readonly string diretorioTemporario = Path.Combine(
        Path.GetTempPath(), "MiniDocker-Testes", Guid.NewGuid().ToString("N"));
    private readonly ServicoConteiner servico;

    public RedeTestes()
    {
        servico = new ServicoConteiner(diretorioTemporario);
    }

    [Theory]
    [InlineData("8080:80", "127.0.0.1", 8080, 80)]
    [InlineData("80", "127.0.0.1", 0, 80)]
    [InlineData("0.0.0.0:9000:3000", "0.0.0.0", 9000, 3000)]
    [InlineData("8080:80/tcp", "127.0.0.1", 8080, 80)]
    public void InterpretaOFormatoDoDocker(string texto, string ip, int host, int container)
    {
        var porta = MapeamentoPorta.Interpretar(texto);

        Assert.Equal(ip, porta.Ip);
        Assert.Equal(host, porta.PortaHost);
        Assert.Equal(container, porta.PortaContainer);
    }

    [Theory]
    [InlineData("8080:80/udp")]
    [InlineData("abc")]
    [InlineData("8080:0")]
    [InlineData("70000:80")]
    [InlineData("1.2.3:8080:80")]
    [InlineData("a:b:c:d")]
    public void RecusaPortaInvalida(string texto)
    {
        Assert.Throws<ArgumentException>(() => MapeamentoPorta.Interpretar(texto));
    }

    [Fact]
    public void ProxyRepassaOsBytesNosDoisSentidos()
    {
        using var servidor = new ServidorEco();
        using var proxy = new ProxyTcp(IPAddress.Loopback, 0, servidor.Porta);

        var resposta = Conversar(proxy.PortaHost, "ola container");

        Assert.Equal("eco: ola container", resposta);
        Assert.Equal(1, proxy.Conexoes);
        Assert.True(proxy.BytesTransferidos > 0);
    }

    [Fact]
    public void ProxyAtendeVariasConexoesAoMesmoTempo()
    {
        using var servidor = new ServidorEco();
        using var proxy = new ProxyTcp(IPAddress.Loopback, 0, servidor.Porta);

        var respostas = Enumerable.Range(0, 20).AsParallel().Select(i => Conversar(proxy.PortaHost, $"pedido {i}")).ToList();

        Assert.Equal(20, respostas.Distinct().Count());
        Assert.All(respostas, r => Assert.StartsWith("eco: pedido ", r));
    }

    [Fact]
    public void PortaDoHostOcupadaDaErroClaro()
    {
        using var ocupada = new ProxyTcp(IPAddress.Loopback, 0, 1);

        var erro = Assert.Throws<InvalidOperationException>(() => new ProxyTcp(IPAddress.Loopback, ocupada.PortaHost, 1));
        Assert.Contains("em uso", erro.Message);
    }

    [Fact]
    public void FecharOProxyLiberaAPorta()
    {
        int porta;
        using (var proxy = new ProxyTcp(IPAddress.Loopback, 0, 1))
        {
            porta = proxy.PortaHost;
        }

        using var denovo = new ProxyTcp(IPAddress.Loopback, porta, 1);
        Assert.Equal(porta, denovo.PortaHost);
    }

    [Fact]
    public void RedeAbreTudoOuNada()
    {
        using var ocupada = new ProxyTcp(IPAddress.Loopback, 0, 1);
        var livre = ProxyTcp.SortearPortaLivre();
        var portas = new[]
        {
            MapeamentoPorta.Interpretar($"{livre}:80"),
            MapeamentoPorta.Interpretar($"{ocupada.PortaHost}:81")
        };

        Assert.Throws<InvalidOperationException>(() => RedeConteiner.Abrir(portas));

        // A primeira porta foi fechada quando a segunda falhou.
        using var proxy = new ProxyTcp(IPAddress.Loopback, livre, 1);
    }

    [Fact]
    public void RedeSorteiaPortaInternaEDoHost()
    {
        using var rede = RedeConteiner.Abrir([MapeamentoPorta.Interpretar("80")]);

        var porta = rede.Mapeamentos.Single();
        Assert.NotNull(porta.PortaInterna);
        Assert.True(porta.PortaHostAtual > 0);
        Assert.NotEqual(porta.PortaInterna, porta.PortaHostAtual);
    }

    [Fact]
    public void CriarGuardaAsPortasERecusaRepetidas()
    {
        var conteiner = servico.Criar("web", "scratch", "x", new OpcoesConteiner { Portas = ["8080:80", "9443:443"] });
        Assert.Equal([80, 443], conteiner.Portas.Select(p => p.PortaContainer));

        Assert.Throws<ArgumentException>(() =>
            servico.Criar("w2", "scratch", "x", new OpcoesConteiner { Portas = ["8080:80", "8081:80"] }));
        Assert.Throws<ArgumentException>(() =>
            servico.Criar("w3", "scratch", "x", new OpcoesConteiner { Portas = ["8080:80", "8080:81"] }));
    }

    [Fact]
    public void ReplicasNaoPodemDividirUmaPortaFixa()
    {
        servico.Criar("web-1", "scratch", "x", new OpcoesConteiner { Grupo = "web", Portas = ["8080:80"] });

        var erro = Assert.Throws<ArgumentException>(() => servico.Escalar("web", 2));
        Assert.Contains("-p 80", erro.Message);
    }

    [Fact]
    public void ReplicasComPortaSorteadaEscalam()
    {
        servico.Criar("web-1", "scratch", "x", new OpcoesConteiner { Grupo = "web", Portas = ["80"] });

        var novas = servico.Escalar("web", 3);

        Assert.All(novas, c => Assert.True(c.Portas.Single() is { PortaHost: 0, PortaContainer: 80 }));
    }

    [FatoWindows]
    public void ProgramaDoContainerRecebeAPortaInterna()
    {
        var saida = new List<string>();

        var conteiner = servico.Executar("web", "scratch", "echo %PORT% %PORT_80%",
            new OpcoesConteiner { Portas = ["80"] }, saida.Add);

        var portas = saida.Single().Split(' ');
        Assert.Equal(portas[0], portas[1]);
        Assert.Equal(conteiner.Portas.Single().PortaInterna.ToString(), portas[0]);
    }

    private static string Conversar(int porta, string mensagem)
    {
        using var cliente = new TcpClient();
        cliente.Connect(IPAddress.Loopback, porta);
        var fluxo = cliente.GetStream();
        var bytes = Encoding.UTF8.GetBytes(mensagem);
        fluxo.Write(bytes);
        cliente.Client.Shutdown(SocketShutdown.Send);

        using var leitor = new StreamReader(fluxo);
        return leitor.ReadToEnd();
    }

    /// <summary>Faz o papel do programa do container: responde "eco: " + o que recebeu.</summary>
    private sealed class ServidorEco : IDisposable
    {
        private readonly TcpListener ouvinte = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancelamento = new();

        public ServidorEco()
        {
            ouvinte.Start();
            _ = Task.Run(async () =>
            {
                while (!cancelamento.IsCancellationRequested)
                {
                    TcpClient cliente;
                    try
                    {
                        cliente = await ouvinte.AcceptTcpClientAsync(cancelamento.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    _ = Task.Run(() =>
                    {
                        using (cliente)
                        {
                            var fluxo = cliente.GetStream();
                            var texto = new StreamReader(fluxo).ReadToEnd();
                            fluxo.Write(Encoding.UTF8.GetBytes("eco: " + texto));
                        }
                    });
                }
            });
        }

        public int Porta => ((IPEndPoint)ouvinte.LocalEndpoint).Port;

        public void Dispose()
        {
            cancelamento.Cancel();
            ouvinte.Stop();
        }
    }

    public void Dispose()
    {
        foreach (var conteiner in servico.Listar())
        {
            servico.Remover(conteiner.Nome, forcar: true);
        }

        if (Directory.Exists(diretorioTemporario))
        {
            Directory.Delete(diretorioTemporario, recursive: true);
        }
    }
}

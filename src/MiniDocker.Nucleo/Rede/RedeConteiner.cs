using System.Net;
using MiniDocker.Nucleo.Modelos;

namespace MiniDocker.Nucleo.Rede;

/// <summary>
/// As portas publicadas de um container durante uma execução: um proxy por
/// porta, cada um com a sua porta interna sorteada.
/// </summary>
public sealed class RedeConteiner : IDisposable
{
    private readonly List<ProxyTcp> proxies;

    private RedeConteiner(List<ProxyTcp> proxies, List<MapeamentoPorta> mapeamentos)
    {
        this.proxies = proxies;
        Mapeamentos = mapeamentos;
    }

    /// <summary>Os mapeamentos com as portas desta execução preenchidas.</summary>
    public IReadOnlyList<MapeamentoPorta> Mapeamentos { get; }

    public long Conexoes => proxies.Sum(p => p.Conexoes);

    /// <summary>Abre todas as portas, ou nenhuma: se uma falhar, as já abertas são fechadas.</summary>
    public static RedeConteiner Abrir(IEnumerable<MapeamentoPorta> portas)
    {
        var proxies = new List<ProxyTcp>();
        var mapeamentos = new List<MapeamentoPorta>();
        try
        {
            foreach (var porta in portas)
            {
                var interna = ProxyTcp.SortearPortaLivre();
                var proxy = new ProxyTcp(IPAddress.Parse(porta.Ip), porta.PortaHost, interna);
                proxies.Add(proxy);
                mapeamentos.Add(porta with { PortaHostAtual = proxy.PortaHost, PortaInterna = interna });
            }
        }
        catch
        {
            proxies.ForEach(p => p.Dispose());
            throw;
        }

        return new RedeConteiner(proxies, mapeamentos);
    }

    public void Dispose() => proxies.ForEach(p => p.Dispose());
}

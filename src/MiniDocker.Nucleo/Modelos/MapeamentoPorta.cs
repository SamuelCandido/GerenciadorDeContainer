using System.Net;
using System.Text.Json.Serialization;

namespace MiniDocker.Nucleo.Modelos;

/// <summary>
/// Uma porta publicada (<c>-p 8080:80</c>). Três números entram na conta:
///
/// <code>
/// navegador ──► 127.0.0.1:8080  ──proxy──►  127.0.0.1:51234  (o programa do container)
///               porta do host               porta interna, em %PORT%
///               "a porta 80 do container"
/// </code>
///
/// O container não tem pilha de rede própria (exigiria Server Silos), então
/// "porta 80 do container" é um nome: o MiniDocker sorteia uma porta livre do
/// host, entrega ao programa em <c>PORT</c> e <c>PORT_80</c>, e publica com um
/// proxy. É o papel do <c>docker-proxy</c>.
/// </summary>
public sealed record MapeamentoPorta
{
    /// <summary>Endereço onde a porta é publicada. Padrão: só nesta máquina.</summary>
    public string Ip { get; init; } = IPAddress.Loopback.ToString();

    /// <summary>Porta pedida no host; 0 sorteia uma livre a cada start (<c>-p 80</c>).</summary>
    public int PortaHost { get; init; }

    /// <summary>A porta "do container", como a aplicação a conhece.</summary>
    public int PortaContainer { get; init; }

    /// <summary>Porta do host em uso nesta execução (igual a <see cref="PortaHost"/>, ou a sorteada).</summary>
    public int? PortaHostAtual { get; init; }

    /// <summary>Porta em que o programa do container escuta nesta execução.</summary>
    public int? PortaInterna { get; init; }

    [JsonIgnore]
    public bool Aleatoria => PortaHost == 0;

    public string Descrever() =>
        $"{Ip}:{(PortaHostAtual ?? PortaHost) switch { 0 => "?", var p => p.ToString() }}->{PortaContainer}";

    /// <summary>
    /// Interpreta <c>[IP:]PORTA_HOST:PORTA_CONTAINER</c> ou só
    /// <c>PORTA_CONTAINER</c> (porta do host sorteada), com <c>/tcp</c> opcional.
    /// </summary>
    public static MapeamentoPorta Interpretar(string especificacao)
    {
        var texto = especificacao.Trim();
        if (texto.EndsWith("/udp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Porta '{especificacao}': so TCP e suportado.");
        }

        if (texto.EndsWith("/tcp", StringComparison.OrdinalIgnoreCase))
        {
            texto = texto[..^4];
        }

        var partes = texto.Split(':');
        var (ip, host, container) = partes.Length switch
        {
            1 => (IPAddress.Loopback.ToString(), "0", partes[0]),
            2 => (IPAddress.Loopback.ToString(), partes[0], partes[1]),
            3 => (partes[0], partes[1], partes[2]),
            _ => throw new ArgumentException($"Porta invalida: '{especificacao}'. Use -p 8080:80 ou -p 80.")
        };

        // Comparar com a forma canônica recusa as notações antigas que o
        // TryParse aceita, como "1.2.3" (que viraria 1.2.0.3).
        if (!IPAddress.TryParse(ip, out var endereco) || endereco.ToString() != ip)
        {
            throw new ArgumentException($"Endereco invalido em '{especificacao}': '{ip}'.");
        }

        return new MapeamentoPorta
        {
            Ip = ip,
            PortaHost = LerPorta(host.Length == 0 ? "0" : host, especificacao, aceitaZero: true),
            PortaContainer = LerPorta(container, especificacao, aceitaZero: false)
        };
    }

    private static int LerPorta(string texto, string especificacao, bool aceitaZero) =>
        int.TryParse(texto, out var porta) && porta <= 65535 && (porta > 0 || (aceitaZero && porta == 0))
            ? porta
            : throw new ArgumentException($"Porta invalida em '{especificacao}': '{texto}'.");
}

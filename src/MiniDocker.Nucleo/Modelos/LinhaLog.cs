using System.Globalization;

namespace MiniDocker.Nucleo.Modelos;

/// <summary>
/// Uma linha do log de um container. No arquivo, cada linha é gravada como
/// <c>2026-11-15T10:00:00.1234567Z texto</c>; o horário fica escondido no
/// <c>logs</c> e aparece com <c>logs -t</c>.
/// </summary>
public sealed record LinhaLog(string Conteiner, DateTime? Momento, string Texto)
{
    public static string Formatar(DateTime momento, string texto) =>
        $"{momento.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)} {texto}";

    /// <summary>Separa o horário do texto. Linhas de logs antigos, sem horário, ficam com <c>Momento</c> nulo.</summary>
    public static LinhaLog Interpretar(string conteiner, string linha)
    {
        var espaco = linha.IndexOf(' ');
        if (espaco >= 20 && linha[..espaco].Contains('T')
            && DateTime.TryParse(linha[..espaco], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var momento))
        {
            return new LinhaLog(conteiner, momento, linha[(espaco + 1)..]);
        }

        return new LinhaLog(conteiner, null, linha);
    }
}

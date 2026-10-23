using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MiniDocker.Nucleo.Modelos;

/// <summary>
/// Um sistema de arquivos base mais os padrões de execução (comando e
/// ambiente). Criar um container a partir dela copia o rootfs da imagem para
/// o container, então cada container altera a sua cópia sem afetar a imagem
/// nem os outros containers. O Docker evita a cópia com camadas e um sistema
/// de arquivos de união (overlayfs); aqui as camadas ficam achatadas.
/// </summary>
public class Imagem
{
    /// <summary>Imagem vazia embutida, como o <c>scratch</c> do Docker.</summary>
    public const string NomeScratch = "scratch";

    public const string PlataformaWindows = "windows";
    public const string PlataformaLinux = "linux";

    public string Id { get; set; } = GerarId();
    public string Repositorio { get; set; } = string.Empty;
    public string Tag { get; set; } = "latest";
    public DateTime CriadaEm { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Para qual kernel os programas da imagem foram feitos. Imagens Windows
    /// vêm do build; imagens Linux, do <c>pull</c> no Docker Hub.
    /// </summary>
    public string Plataforma { get; set; } = PlataformaWindows;

    /// <summary>Comando padrão (<c>CMD</c>), usado quando o run não informa um.</summary>
    public string? Comando { get; set; }

    /// <summary>Variáveis definidas por <c>ENV</c>, herdadas pelos containers.</summary>
    public Dictionary<string, string> Ambiente { get; set; } = [];

    /// <summary>Instruções que produziram a imagem, da base até aqui.</summary>
    public List<string> Historico { get; set; } = [];

    /// <summary>
    /// Imagem Windows: a pasta com os arquivos. Imagem Linux: o arquivo .tar
    /// da camada, como veio do registro — os arquivos Linux (links simbólicos,
    /// permissões) não sobrevivem a uma extração no NTFS.
    /// </summary>
    public string CaminhoRootFs { get; set; } = string.Empty;
    public long TamanhoBytes { get; set; }

    [JsonIgnore]
    public string Referencia => $"{Repositorio}:{Tag}";

    [JsonIgnore]
    public bool EhScratch => Repositorio == NomeScratch;

    public static Imagem Scratch { get; } = new() { Id = "", Repositorio = NomeScratch };

    private static string GerarId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    }
}

/// <summary>Regras de nome de imagem no formato <c>nome[:tag]</c>.</summary>
public static partial class ReferenciaImagem
{
    /// <summary>Separa <c>nome:tag</c>, assumindo <c>latest</c> quando falta a tag.</summary>
    public static (string Repositorio, string Tag) Separar(string referencia)
    {
        var separador = referencia.LastIndexOf(':');
        var (repositorio, tag) = separador < 0
            ? (referencia, "latest")
            : (referencia[..separador], referencia[(separador + 1)..]);

        if (!PadraoRepositorio().IsMatch(repositorio))
        {
            throw new ArgumentException(
                $"Nome de imagem invalido: '{repositorio}'. Use letras minusculas, digitos, '.', '_' ou '-'.");
        }

        if (!PadraoTag().IsMatch(tag))
        {
            throw new ArgumentException($"Tag invalida: '{tag}'.");
        }

        return (repositorio, tag);
    }

    public static string Normalizar(string referencia)
    {
        var (repositorio, tag) = Separar(referencia);
        return $"{repositorio}:{tag}";
    }

    // Aceita um nível de namespace, como no Docker Hub: "usuario/imagem".
    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]*(/[a-z0-9][a-z0-9_.-]*)?$")]
    private static partial Regex PadraoRepositorio();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex PadraoTag();
}

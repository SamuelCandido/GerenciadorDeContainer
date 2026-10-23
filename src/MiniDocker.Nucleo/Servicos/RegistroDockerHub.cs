using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace MiniDocker.Nucleo.Servicos;

internal sealed record ConfiguracaoImagem(string? Comando, Dictionary<string, string> Ambiente);

/// <summary>
/// Cliente mínimo da API de registro do Docker Hub — a mesma que o
/// <c>docker pull</c> usa.
///
/// <code>
/// 1. token anônimo          auth.docker.io/token
/// 2. índice da imagem       /v2/library/alpine/manifests/latest   → uma entrada por plataforma
/// 3. manifesto da nossa     /v2/library/alpine/manifests/sha256:… → config + camadas
/// 4. configuração           /v2/library/alpine/blobs/sha256:…     → CMD e ENV
/// 5. camadas                /v2/library/alpine/blobs/sha256:…     → os arquivos, um .tar.gz por camada
/// </code>
///
/// As camadas são juntadas num .tar só, da base para o topo: um arquivo que
/// aparece numa camada de cima substitui o de baixo. Versão reduzida: os
/// marcadores de remoção do Docker (arquivos <c>.wh.*</c>) são ignorados, então
/// um arquivo apagado por uma camada de cima continua lá.
/// </summary>
internal static class RegistroDockerHub
{
    private const string Registro = "https://registry-1.docker.io/v2";

    private static readonly string[] TiposIndice =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json"
    ];

    private static readonly string[] TiposManifesto =
    [
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json"
    ];

    /// <summary>Baixa a imagem e grava a camada descompactada em <paramref name="destinoTar"/>.</summary>
    public static ConfiguracaoImagem Baixar(string repositorio, string tag, string destinoTar, Action<string> saida)
    {
        // Imagens oficiais ficam no namespace "library": alpine = library/alpine.
        var nome = repositorio.Contains('/') ? repositorio : $"library/{repositorio}";
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("minidocker/1.0");

        var token = LerJson(http, $"https://auth.docker.io/token?service=registry.docker.io&scope=repository:{nome}:pull")["token"]!
            .GetValue<string>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        saida($"Baixando {repositorio}:{tag} do Docker Hub");
        var manifesto = LerJson(http, $"{Registro}/{nome}/manifests/{tag}", [.. TiposIndice, .. TiposManifesto],
            $"Imagem '{repositorio}:{tag}' nao encontrada no Docker Hub.");

        if (manifesto["manifests"] is JsonArray plataformas)
        {
            var arquitetura = Arquitetura();
            var escolhido = plataformas.FirstOrDefault(m =>
                    m?["platform"]?["os"]?.GetValue<string>() == "linux" &&
                    m["platform"]?["architecture"]?.GetValue<string>() == arquitetura)
                ?? throw new InvalidOperationException($"'{repositorio}:{tag}' nao tem versao linux/{arquitetura}.");
            manifesto = LerJson(http, $"{Registro}/{nome}/manifests/{escolhido["digest"]}", TiposManifesto);
        }

        var configuracao = LerJson(http, $"{Registro}/{nome}/blobs/{manifesto["config"]!["digest"]}")["config"];

        using (var arquivo = File.Create(destinoTar))
        using (var juntas = new TarWriter(arquivo))
        {
            foreach (var camada in manifesto["layers"]!.AsArray())
            {
                saida($"Camada {camada!["digest"]!.GetValue<string>()[7..19]}: " +
                      $"{camada["size"]!.GetValue<long>() / 1024.0 / 1024.0:F1} MB");
                using var resposta = Enviar(http, $"{Registro}/{nome}/blobs/{camada["digest"]}");
                using var compactado = resposta.Content.ReadAsStream();
                using var descompactado = new GZipStream(compactado, CompressionMode.Decompress);
                using var leitor = new TarReader(descompactado);

                while (leitor.GetNextEntry() is { } entrada)
                {
                    if (!Path.GetFileName(entrada.Name.TrimEnd('/')).StartsWith(".wh."))
                    {
                        juntas.WriteEntry(entrada);
                    }
                }
            }
        }

        return new ConfiguracaoImagem(LerComando(configuracao), LerAmbiente(configuracao));
    }

    /// <summary>ENTRYPOINT + CMD da imagem, juntos num comando de shell.</summary>
    private static string? LerComando(JsonNode? configuracao)
    {
        var partes = new[] { "Entrypoint", "Cmd" }
            .SelectMany(campo => configuracao?[campo] is JsonArray valores
                ? valores.Select(v => v!.GetValue<string>())
                : [])
            .Select(p => p.Contains(' ') ? $"'{p}'" : p)
            .ToList();
        return partes.Count > 0 ? string.Join(' ', partes) : null;
    }

    private static Dictionary<string, string> LerAmbiente(JsonNode? configuracao)
    {
        var ambiente = new Dictionary<string, string>();
        if (configuracao?["Env"] is JsonArray variaveis)
        {
            foreach (var definicao in variaveis.Select(v => v!.GetValue<string>()))
            {
                var igual = definicao.IndexOf('=');
                if (igual > 0)
                {
                    ambiente[definicao[..igual]] = definicao[(igual + 1)..];
                }
            }
        }

        return ambiente;
    }

    private static string Arquitetura() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        var outra => throw new PlatformNotSupportedException($"Arquitetura {outra} nao suportada.")
    };

    private static JsonNode LerJson(HttpClient http, string url, string[]? aceitos = null, string? erro404 = null)
    {
        using var resposta = Enviar(http, url, aceitos, erro404);
        using var fluxo = resposta.Content.ReadAsStream();
        return JsonNode.Parse(fluxo)!;
    }

    private static HttpResponseMessage Enviar(HttpClient http, string url, string[]? aceitos = null, string? erro404 = null)
    {
        var pedido = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var tipo in aceitos ?? [])
        {
            pedido.Headers.Accept.ParseAdd(tipo);
        }

        var resposta = http.Send(pedido, HttpCompletionOption.ResponseHeadersRead);
        if (!resposta.IsSuccessStatusCode)
        {
            resposta.Dispose();
            throw new InvalidOperationException(
                resposta.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized
                    && erro404 is not null
                    ? erro404
                    : $"O Docker Hub respondeu {(int)resposta.StatusCode} para {url}.");
        }

        return resposta;
    }
}

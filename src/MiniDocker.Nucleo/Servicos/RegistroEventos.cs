using System.Text.Json;

namespace MiniDocker.Nucleo.Servicos;

/// <summary>Uma linha do <c>minidocker events</c>.</summary>
public sealed record Evento(DateTime Momento, string Conteiner, string Tipo, string? Detalhe = null)
{
    /// <summary>Tipos que indicam um problema: aparecem destacados e filtram com <c>events --erros</c>.</summary>
    public bool EhErro => Tipo is TiposEvento.Erro or TiposEvento.Falhou;

    public override string ToString() =>
        $"{Momento.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {Conteiner,-16} {Tipo,-10} {Detalhe}".TrimEnd();
}

public static class TiposEvento
{
    public const string Criado = "criado";
    public const string Iniciado = "iniciado";
    public const string Terminou = "terminou";
    public const string Falhou = "falhou";
    public const string Reiniciando = "reiniciando";
    public const string Parado = "parado";
    public const string Removido = "removido";

    /// <summary>Um evento de erro vindo do kernel (limite de memória, falha de processo...).</summary>
    public const string Erro = "erro";
}

/// <summary>
/// O histórico de eventos de todos os containers, em <c>eventos.jsonl</c> (um
/// JSON por linha). Vários processos gravam nele — cada shim, o CLI — então
/// cada gravação acontece dentro da mesma seção crítica entre processos do
/// <c>containers.json</c>: um arquivo de trava aberto com <c>FileShare.None</c>.
/// Sem ela, duas gravações simultâneas no fim do arquivo se sobrescreveriam.
/// </summary>
public sealed class RegistroEventos
{
    private static readonly object Sincronizador = new();
    private static readonly TimeSpan EsperaMaximaTrava = TimeSpan.FromSeconds(10);

    private readonly string caminho;

    public RegistroEventos(string? raizWorkspace = null)
    {
        var raiz = Workspace.Raiz(raizWorkspace);
        Directory.CreateDirectory(raiz);
        caminho = Path.Combine(raiz, "eventos.jsonl");
    }

    public void Registrar(string conteiner, string tipo, string? detalhe = null)
    {
        var linha = JsonSerializer.Serialize(new Evento(DateTime.UtcNow, conteiner, tipo, detalhe)) + "\n";

        lock (Sincronizador)
        {
            using var trava = AbrirTrava();
            using var fluxo = new FileStream(caminho, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var escritor = new StreamWriter(fluxo);
            escritor.Write(linha);
        }
    }

    private FileStream AbrirTrava()
    {
        var limite = DateTime.UtcNow + EsperaMaximaTrava;
        while (true)
        {
            try
            {
                return new FileStream(caminho + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < limite)
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>Lê os eventos a partir de <paramref name="posicao"/> e devolve onde parou, para o <c>events -f</c>.</summary>
    public long Ler(long posicao, Action<Evento> aoLer)
    {
        if (!File.Exists(caminho))
        {
            return posicao;
        }

        using var fluxo = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fluxo.Seek(posicao, SeekOrigin.Begin);
        using var leitor = new StreamReader(fluxo);

        // Só consome linhas completas: uma linha pela metade (sendo gravada
        // agora) fica para a próxima leitura.
        var consumido = posicao;
        var resto = leitor.ReadToEnd();
        var inicio = 0;
        int fim;
        while ((fim = resto.IndexOf('\n', inicio)) >= 0)
        {
            var texto = resto[inicio..fim].Trim();
            consumido += System.Text.Encoding.UTF8.GetByteCount(resto.AsSpan(inicio, fim - inicio + 1));
            inicio = fim + 1;

            if (texto.Length > 0 && TentarLer(texto) is { } evento)
            {
                aoLer(evento);
            }
        }

        return consumido;
    }

    public IReadOnlyList<Evento> LerTodos()
    {
        var eventos = new List<Evento>();
        Ler(0, eventos.Add);
        return eventos;
    }

    private static Evento? TentarLer(string linha)
    {
        try
        {
            return JsonSerializer.Deserialize<Evento>(linha);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

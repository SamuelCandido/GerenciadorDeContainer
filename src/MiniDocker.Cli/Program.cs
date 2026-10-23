using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

var servico = new ServicoConteiner();
var imagens = new ServicoImagem();

try
{
	if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
	{
		ExibirAjuda();
		return 0;
	}

	var resto = args[1..];
	switch (args[0])
	{
		case "run":
			return CriarOuExecutar(resto, executar: true);
		case "create":
			return CriarOuExecutar(resto, executar: false);
		case "start":
			return Iniciar(resto);
		case "stop":
			return ParaCada(resto, nome => servico.Parar(nome));
		case "restart":
			return ParaCada(resto, Reiniciar);
		case "rm":
			var remocao = LinhaComando.Analisar(resto, ["-f", "--force"], []);
			var forcar = remocao.Tem("-f", "--force");
			return ParaCada(remocao.Posicionais, nome => servico.Remover(nome, forcar));
		case "ps":
			return Listar(resto);
		case "logs":
			return Logs(resto);
		case "exec":
			return Exec(resto);
		case "stats":
			return Stats(resto);
		case "inspect":
			return Inspecionar(resto);
		case "build":
			return Build(resto);
		case "images":
			return ListarImagens(resto);
		case "rmi":
			return ParaCada(resto, nome => imagens.Remover(nome));
		case "commit":
			return Commit(resto);
		case "save":
			return Salvar(resto);
		case "load":
			return Carregar(resto);
		case "__shim":
			return Shim(resto);
		default:
			Console.Error.WriteLine($"Comando desconhecido: '{args[0]}'.");
			ExibirAjuda();
			return 1;
	}
}
catch (Exception excecao)
{
	Console.Error.WriteLine($"Erro: {excecao.Message}");
	return 1;
}

int CriarOuExecutar(string[] argumentos, bool executar)
{
	string[] flags = executar ? ["-d", "--detach", "--rm", "-i", "-t", "-it"] : ["--rm"];
	var linha = LinhaComando.Analisar(argumentos, flags,
		["--name", "-e", "--env", "-m", "--memory", "--cpus", "--pids-limit"],
		pararNoPosicional: true);

	if (linha.Posicionais.Count == 0)
	{
		throw new ArgumentException("Informe a imagem. Ex.: minidocker run scratch echo ola");
	}

	var desanexado = linha.Tem("-d", "--detach");
	var interativo = linha.Tem("-i", "-t", "-it");
	if (desanexado && interativo)
	{
		throw new ArgumentException("As opcoes -d e -it nao podem ser usadas juntas.");
	}

	var opcoes = new OpcoesConteiner
	{
		Limites = LerLimites(linha),
		Ambiente = LerAmbiente(linha),
		RemoverAoFinalizar = linha.Tem("--rm")
	};
	var comando = linha.Posicionais.Count > 1 ? string.Join(' ', linha.Posicionais.Skip(1)) : null;
	var conteiner = servico.Criar(linha.Valor("--name"), linha.Posicionais[0], comando, opcoes);

	if (!executar)
	{
		Console.WriteLine(conteiner.Id);
		return 0;
	}

	if (desanexado)
	{
		IniciarEmSegundoPlano(conteiner);
		Console.WriteLine(conteiner.Id);
		return 0;
	}

	return IniciarAnexado(conteiner.Nome, interativo);
}

int Iniciar(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, ["-a", "--attach"], []);
	if (linha.Tem("-a", "--attach"))
	{
		if (linha.Posicionais.Count != 1)
		{
			throw new ArgumentException("start -a aceita um unico container.");
		}

		return IniciarAnexado(linha.Posicionais[0], interativo: false);
	}

	return ParaCada(linha.Posicionais, nome => IniciarEmSegundoPlano(servico.Obter(nome)));
}

void Reiniciar(string nome)
{
	if (servico.Obter(nome).Estado == EstadoConteiner.Executando)
	{
		servico.Parar(nome);
	}

	IniciarEmSegundoPlano(servico.Obter(nome));
}

// O CLI vira o dono do container: segura o Job Object e repassa a saída.
// Ctrl+C aqui equivale a um stop, como no docker run sem -d.
int IniciarAnexado(string nome, bool interativo)
{
	Console.CancelKeyPress += (_, evento) =>
	{
		evento.Cancel = true;
		if (!interativo)
		{
			ThreadPool.QueueUserWorkItem(_ =>
			{
				try
				{
					servico.Parar(nome);
				}
				catch (Exception)
				{
					// O container pode ter terminado sozinho nesse meio-tempo.
				}
			});
		}
	};

	var conteiner = servico.Iniciar(nome, Console.WriteLine, interativo);
	return conteiner.CodigoSaida ?? 1;
}

// O papel do containerd-shim do Docker: um processo em segundo plano que é
// dono do Job Object, grava o log e registra o fim. O CLI pode sair logo em
// seguida sem levar o container junto.
void IniciarEmSegundoPlano(Conteiner conteiner)
{
	if (conteiner.Estado == EstadoConteiner.Executando)
	{
		throw new InvalidOperationException($"O container '{conteiner.Nome}' ja esta em execucao.");
	}

	var informacoes = new ProcessStartInfo
	{
		FileName = Environment.ProcessPath!,
		UseShellExecute = false,
		CreateNoWindow = true,
		// Sem redirecionar, o shim herdaria o stdout deste processo e um
		// "minidocker run -d ... | more" esperaria o container terminar.
		RedirectStandardInput = true,
		RedirectStandardOutput = true,
		RedirectStandardError = true
	};
	if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
	{
		informacoes.ArgumentList.Add(typeof(Program).Assembly.Location);
	}
	informacoes.ArgumentList.Add("__shim");
	informacoes.ArgumentList.Add(conteiner.Nome);

	using var shim = Process.Start(informacoes)!;
	shim.StandardInput.Close();
	shim.StandardOutput.Close();
	shim.StandardError.Close();

	// Espera o shim registrar o início, para que um "ps" logo em seguida já
	// mostre o container em execução e erros de início apareçam aqui.
	var anterior = conteiner.IniciadoEm;
	var limite = DateTime.UtcNow.AddSeconds(10);
	while (DateTime.UtcNow < limite)
	{
		if (Iniciou(conteiner.Nome, anterior))
		{
			return;
		}

		if (shim.HasExited)
		{
			if (Iniciou(conteiner.Nome, anterior))
			{
				return;
			}

			throw new InvalidOperationException(
				$"Falha ao iniciar '{conteiner.Nome}'. Veja: minidocker logs {conteiner.Nome}");
		}

		Thread.Sleep(50);
	}

	throw new TimeoutException($"O container '{conteiner.Nome}' demorou demais para iniciar.");
}

bool Iniciou(string nome, DateTime? inicioAnterior)
{
	try
	{
		return servico.Obter(nome).IniciadoEm != inicioAnterior;
	}
	catch (InvalidOperationException)
	{
		// Com --rm, o container pode ter rodado e sido removido antes da checagem.
		return true;
	}
}

int Shim(string[] argumentos)
{
	var conteiner = servico.Iniciar(argumentos[0]);
	return conteiner.CodigoSaida ?? 1;
}

int Listar(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, ["-a", "--all", "-q", "--quiet"], []);
	var conteineres = servico.Listar()
		.Where(c => linha.Tem("-a", "--all") || c.Estado == EstadoConteiner.Executando)
		.OrderByDescending(c => c.CriadoEm)
		.ToList();

	if (linha.Tem("-q", "--quiet"))
	{
		conteineres.ForEach(c => Console.WriteLine(c.Id));
		return 0;
	}

	ImprimirTabela(
		["CONTAINER ID", "IMAGEM", "COMANDO", "CRIADO", "STATUS", "NOME"],
		conteineres.Select(c => new[]
		{
			c.Id, c.Imagem, $"\"{Truncar(c.Comando, 24)}\"", $"ha {Decorrido(c.CriadoEm)}", Status(c), c.Nome
		}));
	return 0;
}

int Logs(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, ["-f", "--follow"], []);
	if (linha.Posicionais.Count != 1)
	{
		throw new ArgumentException("Uso: minidocker logs [-f] CONTAINER");
	}

	using var cancelamento = new CancellationTokenSource();
	Console.CancelKeyPress += (_, evento) =>
	{
		evento.Cancel = true;
		cancelamento.Cancel();
	};

	servico.LerLogs(linha.Posicionais[0], linha.Tem("-f", "--follow"), Console.WriteLine, cancelamento.Token);
	return 0;
}

int Exec(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, ["-i", "-t", "-it"], [], pararNoPosicional: true);
	if (linha.Posicionais.Count < 2)
	{
		throw new ArgumentException("Uso: minidocker exec CONTAINER COMANDO");
	}

	// O Ctrl+C chega também ao processo do exec, que divide o console com o CLI;
	// o CLI só ignora e espera ele terminar.
	Console.CancelKeyPress += (_, evento) => evento.Cancel = true;
	return servico.ExecutarNoConteiner(linha.Posicionais[0], string.Join(' ', linha.Posicionais.Skip(1)));
}

int Stats(string[] argumentos)
{
	var nomes = argumentos.Length > 0
		? argumentos.ToList()
		: servico.Listar().Where(c => c.Estado == EstadoConteiner.Executando).Select(c => c.Nome).ToList();

	// O Job Object dá o tempo de CPU acumulado; o percentual sai de duas
	// leituras separadas por um intervalo, como faz o docker stats.
	var intervalo = TimeSpan.FromMilliseconds(500);
	var antes = nomes.ToDictionary(n => n, n => TentarEstatisticas(n));
	Thread.Sleep(intervalo);

	var linhas = new List<string[]>();
	foreach (var nome in nomes)
	{
		if (antes[nome] is not { } primeira || TentarEstatisticas(nome) is not { } segunda)
		{
			continue;
		}

		var conteiner = servico.Obter(nome);
		var cpu = (segunda.TempoCpuMs - primeira.TempoCpuMs) / intervalo.TotalMilliseconds * 100;
		var limiteMemoria = conteiner.Limites.MemoriaMaximaBytes is { } m ? FormatarBytes(m) : "-";
		var limiteProcessos = conteiner.Limites.ProcessosMaximos?.ToString() ?? "-";

		linhas.Add([
			conteiner.Id, conteiner.Nome, $"{cpu:F1}%",
			$"{FormatarBytes(segunda.PicoMemoriaBytes)} / {limiteMemoria}",
			$"{segunda.ProcessosAtivos} / {limiteProcessos}",
			segunda.TotalProcessos.ToString()
		]);
	}

	ImprimirTabela(["CONTAINER ID", "NOME", "CPU %", "MEM PICO / LIMITE", "PROCS / LIMITE", "PROCS CRIADOS"], linhas);
	return 0;
}

ContabilidadeTrabalho? TentarEstatisticas(string nome)
{
	try
	{
		return servico.Estatisticas(nome);
	}
	catch (InvalidOperationException)
	{
		return null;
	}
}

int Inspecionar(string[] argumentos)
{
	if (argumentos.Length == 0)
	{
		throw new ArgumentException("Uso: minidocker inspect CONTAINER|IMAGEM...");
	}

	var objetos = new List<object>();
	foreach (var nome in argumentos)
	{
		try
		{
			objetos.Add(servico.Obter(nome));
		}
		catch (InvalidOperationException)
		{
			objetos.Add(imagens.Obter(nome));
		}
	}

	var opcoes = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
	Console.WriteLine(JsonSerializer.Serialize(objetos, opcoes));
	return 0;
}

int Build(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, [], ["-t", "--tag", "-f", "--file"]);
	var referencia = linha.Valor("-t", "--tag")
		?? throw new ArgumentException("Informe o nome da imagem com -t. Ex.: minidocker build -t app .");
	var contexto = linha.Posicionais.Count > 0 ? linha.Posicionais[0] : ".";

	var imagem = imagens.Construir(contexto, referencia, linha.Valor("-f", "--file"), Console.WriteLine);
	Console.WriteLine($"Imagem {imagem.Id} construida como {imagem.Referencia}");
	return 0;
}

int ListarImagens(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, ["-q", "--quiet"], []);
	var lista = imagens.Listar();

	if (linha.Tem("-q", "--quiet"))
	{
		foreach (var imagem in lista)
		{
			Console.WriteLine(imagem.Id);
		}

		return 0;
	}

	ImprimirTabela(
		["REPOSITORIO", "TAG", "IMAGE ID", "CRIADA", "TAMANHO"],
		lista.Select(i => new[]
		{
			i.Repositorio, i.Tag, i.Id, $"ha {Decorrido(i.CriadaEm)}", FormatarBytes(i.TamanhoBytes)
		}));
	return 0;
}

int Commit(string[] argumentos)
{
	if (argumentos.Length != 2)
	{
		throw new ArgumentException("Uso: minidocker commit CONTAINER IMAGEM[:TAG]");
	}

	var imagem = imagens.Commit(servico.Obter(argumentos[0]), argumentos[1]);
	Console.WriteLine(imagem.Id);
	return 0;
}

int Salvar(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, [], ["-o", "--output"]);
	var arquivo = linha.Valor("-o", "--output")
		?? throw new ArgumentException("Uso: minidocker save -o ARQUIVO.zip IMAGEM");
	if (linha.Posicionais.Count != 1)
	{
		throw new ArgumentException("Uso: minidocker save -o ARQUIVO.zip IMAGEM");
	}

	imagens.Exportar(linha.Posicionais[0], arquivo);
	return 0;
}

int Carregar(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, [], ["-i", "--input"]);
	var arquivo = linha.Valor("-i", "--input")
		?? throw new ArgumentException("Uso: minidocker load -i ARQUIVO.zip");

	var imagem = imagens.Importar(arquivo);
	Console.WriteLine($"Imagem carregada: {imagem.Referencia} ({imagem.Id})");
	return 0;
}

// Aplica a ação a cada nome, como o Docker: um erro não impede os demais,
// mas faz o comando terminar com código 1.
static int ParaCada(IEnumerable<string> nomes, Action<string> acao)
{
	var lista = nomes.ToList();
	if (lista.Count == 0)
	{
		throw new ArgumentException("Informe ao menos um nome.");
	}

	var codigo = 0;
	foreach (var nome in lista)
	{
		try
		{
			acao(nome);
			Console.WriteLine(nome);
		}
		catch (Exception excecao)
		{
			Console.Error.WriteLine($"Erro: {excecao.Message}");
			codigo = 1;
		}
	}

	return codigo;
}

static LimitesRecursos LerLimites(LinhaComando linha)
{
	return new LimitesRecursos
	{
		MemoriaMaximaBytes = linha.Valor("-m", "--memory") is { } memoria ? LerBytes(memoria) : null,
		PercentualMaximoCpu = linha.Valor("--cpus") is { } cpus ? LerPercentualCpu(cpus) : null,
		ProcessosMaximos = linha.Valor("--pids-limit") is { } processos
			? int.TryParse(processos, out var n) ? n : throw new ArgumentException($"--pids-limit invalido: '{processos}'.")
			: null
	};
}

// "-e CHAVE=valor" define; "-e CHAVE" copia o valor do host, como no Docker.
// É a única forma de uma variável do host entrar no container: explicitamente.
static Dictionary<string, string> LerAmbiente(LinhaComando linha)
{
	var ambiente = new Dictionary<string, string>();
	foreach (var definicao in linha.Valores("-e", "--env"))
	{
		var igual = definicao.IndexOf('=');
		if (igual > 0)
		{
			ambiente[definicao[..igual]] = definicao[(igual + 1)..];
		}
		else if (Environment.GetEnvironmentVariable(definicao) is { } valor)
		{
			ambiente[definicao] = valor;
		}
	}

	return ambiente;
}

// Aceita o formato do Docker: 512k, 256m, 1g (ou com "b" no fim).
static long LerBytes(string texto)
{
	var valor = texto.Trim().ToLowerInvariant().TrimEnd('b');
	long multiplicador = 1;
	if (valor.Length > 0 && valor[^1] is 'k' or 'm' or 'g')
	{
		multiplicador = valor[^1] switch { 'k' => 1L << 10, 'm' => 1L << 20, _ => 1L << 30 };
		valor = valor[..^1];
	}

	if (!long.TryParse(valor, out var numero) || numero <= 0)
	{
		throw new ArgumentException($"Memoria invalida: '{texto}'. Exemplos: 256m, 1g.");
	}

	return numero * multiplicador;
}

// O Docker mede CPU em núcleos (--cpus 0.5 = meio núcleo); o Job Object, em
// percentual da máquina inteira. A conversão divide pelo número de núcleos.
static int LerPercentualCpu(string texto)
{
	if (!double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpus) || cpus <= 0)
	{
		throw new ArgumentException($"--cpus invalido: '{texto}'. Exemplo: 0.5");
	}

	var nucleos = Environment.ProcessorCount;
	if (cpus > nucleos)
	{
		throw new ArgumentException($"--cpus {texto} excede os {nucleos} nucleos desta maquina.");
	}

	return Math.Clamp((int)Math.Ceiling(cpus / nucleos * 100), 1, 100);
}

static string Status(Conteiner conteiner) => conteiner.Estado switch
{
	EstadoConteiner.Executando => $"Executando ha {Decorrido(conteiner.IniciadoEm)}",
	EstadoConteiner.Criado => "Criado",
	_ => $"{conteiner.Estado} ({conteiner.CodigoSaida?.ToString() ?? "?"}) ha {Decorrido(conteiner.FinalizadoEm)}"
};

static string Decorrido(DateTime? instante)
{
	if (instante is null)
	{
		return "?";
	}

	var tempo = DateTime.UtcNow - instante.Value;
	return tempo.TotalSeconds < 60 ? $"{(int)tempo.TotalSeconds} s"
		: tempo.TotalMinutes < 60 ? $"{(int)tempo.TotalMinutes} min"
		: tempo.TotalHours < 24 ? $"{(int)tempo.TotalHours} h"
		: $"{(int)tempo.TotalDays} dias";
}

static string FormatarBytes(long bytes)
{
	string[] unidades = ["B", "KB", "MB", "GB"];
	double valor = bytes;
	var indice = 0;
	while (valor >= 1024 && indice < unidades.Length - 1)
	{
		valor /= 1024;
		indice++;
	}

	return indice == 0 ? $"{bytes} B" : $"{valor:F1} {unidades[indice]}";
}

static string Truncar(string texto, int tamanho) =>
	texto.Length <= tamanho ? texto : texto[..(tamanho - 1)] + "…";

static void ImprimirTabela(string[] cabecalho, IEnumerable<string[]> linhas)
{
	var todas = linhas.Prepend(cabecalho).ToList();
	var larguras = cabecalho.Select((_, coluna) => todas.Max(l => l[coluna].Length)).ToArray();

	foreach (var linha in todas)
	{
		Console.WriteLine(string.Join("   ", linha.Select((celula, coluna) =>
			coluna == linha.Length - 1 ? celula : celula.PadRight(larguras[coluna]))));
	}
}

static void ExibirAjuda()
{
	Console.WriteLine("""
		Uso: minidocker COMANDO [OPCOES]

		Containers:
		  run [OPCOES] IMAGEM [COMANDO]   Cria e executa um container
		      -d, --detach                  em segundo plano (imprime o id)
		      -it                           interativo, ligado a este console
		      --name NOME                   nome do container (padrao: gerado)
		      -e, --env CHAVE=valor         variavel de ambiente (repetivel)
		      -m, --memory 256m             limite de memoria
		      --cpus 0.5                    limite de CPU, em nucleos
		      --pids-limit 10               limite de processos vivos
		      --rm                          remove o container ao terminar
		  create [OPCOES] IMAGEM [COMANDO] Cria sem executar
		  start [-a] CONTAINER...          Inicia (em segundo plano; -a anexa)
		  stop CONTAINER...                Encerra o grupo de processos
		  restart CONTAINER...             Para e inicia de novo
		  rm [-f] CONTAINER...             Remove (-f para antes, se preciso)
		  ps [-a] [-q]                     Lista os em execucao (-a: todos)
		  logs [-f] CONTAINER              Mostra a saida (-f: acompanha)
		  exec CONTAINER COMANDO           Executa um comando dentro do container
		  stats [CONTAINER...]             Consumo ao vivo, lido do Job Object
		  inspect NOME...                  Detalhes em JSON (container ou imagem)

		Imagens:
		  build -t NOME[:TAG] [-f ARQUIVO] [CONTEXTO]   Constroi a partir do MiniDockerfile
		  images [-q]                      Lista as imagens
		  rmi IMAGEM...                    Remove imagens
		  commit CONTAINER NOME[:TAG]      Cria uma imagem a partir de um container
		  save -o ARQUIVO.zip IMAGEM       Exporta uma imagem
		  load -i ARQUIVO.zip              Importa uma imagem

		A imagem "scratch" e embutida e vazia.

		Exemplos:
		  minidocker run scratch echo ola
		  minidocker run -d --name web --cpus 0.5 scratch ping -t 127.0.0.1
		  minidocker logs -f web
		  minidocker stop web
		  minidocker start web
		""");
}

/// <summary>
/// Separa as opções (<c>-d</c>, <c>--name x</c>, <c>--name=x</c>) dos
/// argumentos posicionais.
/// </summary>
sealed class LinhaComando
{
	private readonly List<(string Nome, string Valor)> opcoes = [];

	public List<string> Posicionais { get; } = [];

	public bool Tem(params string[] nomes) => opcoes.Any(o => nomes.Contains(o.Nome));

	public string? Valor(params string[] nomes) =>
		opcoes.LastOrDefault(o => nomes.Contains(o.Nome)).Valor;

	public IEnumerable<string> Valores(params string[] nomes) =>
		opcoes.Where(o => nomes.Contains(o.Nome)).Select(o => o.Valor);

	/// <param name="pararNoPosicional">
	/// Tudo a partir do primeiro posicional fica sem interpretar. Em
	/// <c>run scratch ping -n 3 127.0.0.1</c>, o <c>-n</c> é do ping.
	/// </param>
	public static LinhaComando Analisar(
		IEnumerable<string> argumentos, string[] semValor, string[] comValor, bool pararNoPosicional = false)
	{
		var linha = new LinhaComando();
		var lista = argumentos.ToList();

		for (var i = 0; i < lista.Count; i++)
		{
			var argumento = lista[i];
			if (argumento.Length < 2 || argumento[0] != '-')
			{
				if (pararNoPosicional)
				{
					linha.Posicionais.AddRange(lista.Skip(i));
					break;
				}

				linha.Posicionais.Add(argumento);
				continue;
			}

			var igual = argumento.IndexOf('=');
			var nome = igual > 0 ? argumento[..igual] : argumento;

			if (comValor.Contains(nome))
			{
				var valor = igual > 0 ? argumento[(igual + 1)..]
					: i + 1 < lista.Count ? lista[++i]
					: throw new ArgumentException($"A opcao {nome} precisa de um valor.");
				linha.opcoes.Add((nome, valor));
			}
			else if (semValor.Contains(argumento))
			{
				linha.opcoes.Add((argumento, string.Empty));
			}
			else if (argumento[1] != '-' && argumento.Skip(1).All(letra => semValor.Contains($"-{letra}")))
			{
				// Flags curtas combinadas, como em "ps -aq".
				linha.opcoes.AddRange(argumento.Skip(1).Select(letra => ($"-{letra}", string.Empty)));
			}
			else
			{
				throw new ArgumentException($"Opcao desconhecida: '{argumento}'.");
			}
		}

		return linha;
	}
}

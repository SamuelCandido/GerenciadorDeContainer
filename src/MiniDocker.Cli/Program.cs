using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

var servico = new ServicoConteiner();
var imagens = new ServicoImagem();
var volumes = new ServicoVolume();

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
		case "volume":
			return Volume(resto);
		case "scale":
			return Escalar(resto);
		case "events":
			return Eventos(resto);
		case "port":
			return Portas(resto);
		case "compose":
			return Compose(resto);
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
	string[] flags = executar
		? ["-d", "--detach", "--rm", "-i", "-t", "-it", "--privileged"]
		: ["--rm", "--privileged"];
	var linha = LinhaComando.Analisar(argumentos, flags,
		["--name", "-e", "--env", "-m", "--memory", "--cpus", "--pids-limit", "-v", "--volume",
			"--restart", "--replicas", "-p", "--publish"],
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
		RemoverAoFinalizar = linha.Tem("--rm"),
		Volumes = linha.Valores("-v", "--volume").ToList(),
		Portas = linha.Valores("-p", "--publish").ToList(),
		Privilegiado = linha.Tem("--privileged"),
		Reinicio = linha.Valor("--restart") is { } reinicio ? PoliticaReinicio.Interpretar(reinicio) : PoliticaReinicio.Nunca
	};
	var comando = linha.Posicionais.Count > 1 ? string.Join(' ', linha.Posicionais.Skip(1)) : null;

	if (linha.Valor("--replicas") is { } textoReplicas)
	{
		if (executar && !desanexado)
		{
			throw new ArgumentException("--replicas roda em segundo plano: use run -d --replicas N.");
		}

		return CriarReplicas(linha, opcoes, comando, textoReplicas, iniciar: executar);
	}

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

// --replicas N: N containers iguais, GRUPO-1..GRUPO-N, todos em segundo plano.
int CriarReplicas(LinhaComando linha, OpcoesConteiner opcoes, string? comando, string texto, bool iniciar)
{
	var grupo = linha.Valor("--name")
		?? throw new ArgumentException("Com --replicas, informe o nome do grupo com --name.");
	if (!int.TryParse(texto, out var quantidade) || quantidade < 1)
	{
		throw new ArgumentException($"--replicas invalido: '{texto}'.");
	}

	ServicoConteiner.ValidarPortasDoGrupo(opcoes.Portas.Select(MapeamentoPorta.Interpretar), quantidade);

	if (servico.ListarGrupo(grupo).Count > 0)
	{
		throw new InvalidOperationException($"O grupo '{grupo}' ja existe. Use: minidocker scale {grupo}={quantidade}");
	}

	var replicas = new List<Conteiner>();
	for (var indice = 1; indice <= quantidade; indice++)
	{
		replicas.Add(servico.Criar(ServicoConteiner.NomeReplica(grupo, indice), linha.Posicionais[0], comando,
			opcoes with { Grupo = grupo }));
	}

	foreach (var replica in replicas)
	{
		if (iniciar)
		{
			IniciarEmSegundoPlano(replica);
		}

		Console.WriteLine(replica.Nome);
	}

	return 0;
}

int Escalar(string[] argumentos)
{
	if (argumentos.Length == 0)
	{
		throw new ArgumentException("Uso: minidocker scale GRUPO=N [GRUPO=N...]");
	}

	foreach (var pedido in argumentos)
	{
		var igual = pedido.IndexOf('=');
		if (igual <= 0 || !int.TryParse(pedido[(igual + 1)..], out var quantidade))
		{
			throw new ArgumentException($"Uso: minidocker scale GRUPO=N (recebido '{pedido}').");
		}

		var grupo = pedido[..igual];
		var antes = servico.ListarGrupo(grupo).Select(c => c.Nome).ToHashSet();
		var novas = servico.Escalar(grupo, quantidade);

		foreach (var removida in antes.Except(servico.ListarGrupo(grupo).Select(c => c.Nome)))
		{
			Console.WriteLine($"- {removida} removida");
		}

		// Sobe as novas e também as que estavam paradas: o grupo fica com N rodando.
		foreach (var replica in servico.ListarGrupo(grupo).Where(c => c.Estado != EstadoConteiner.Executando))
		{
			IniciarEmSegundoPlano(replica);
			Console.WriteLine(novas.Any(n => n.Nome == replica.Nome) ? $"+ {replica.Nome} criada" : $"> {replica.Nome} iniciada");
		}

		Console.WriteLine($"{grupo}: {servico.ListarGrupo(grupo).Count} replica(s)");
	}

	return 0;
}

int Eventos(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, ["-f", "--follow", "--erros"], []);
	var filtro = linha.Posicionais.ToHashSet();
	var soErros = linha.Tem("--erros");
	var registro = new RegistroEventos();

	void Imprimir(Evento evento)
	{
		if ((filtro.Count == 0 || filtro.Contains(evento.Conteiner)) && (!soErros || evento.EhErro))
		{
			Console.WriteLine(evento.EhErro ? $"{evento}  <<<" : evento.ToString());
		}
	}

	var posicao = registro.Ler(0, Imprimir);
	if (!linha.Tem("-f", "--follow"))
	{
		return 0;
	}

	using var cancelamento = new CancellationTokenSource();
	Console.CancelKeyPress += (_, evento) =>
	{
		evento.Cancel = true;
		cancelamento.Cancel();
	};

	while (!cancelamento.Token.WaitHandle.WaitOne(300))
	{
		posicao = registro.Ler(posicao, Imprimir);
	}

	return 0;
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

	ImprimirConteineres(conteineres);
	return 0;
}

int Logs(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, ["-f", "--follow", "-t", "--timestamps"], []);
	if (linha.Posicionais.Count == 0)
	{
		throw new ArgumentException("Uso: minidocker logs [-f] [-t] CONTAINER|GRUPO...");
	}

	// Um nome que nao e container pode ser um grupo de replicas: entram todas.
	var nomes = new List<string>();
	foreach (var nome in linha.Posicionais)
	{
		try
		{
			nomes.Add(servico.Obter(nome).Nome);
		}
		catch (InvalidOperationException) when (servico.ListarGrupo(nome) is { Count: > 0 } grupo)
		{
			nomes.AddRange(grupo.Select(c => c.Nome));
		}
	}

	MostrarLogs(nomes, linha.Tem("-f", "--follow"), linha.Tem("-t", "--timestamps"));
	return 0;
}

// Com mais de um container, cada linha ganha o nome de quem a escreveu, numa
// cor por container, como o docker compose logs.
void MostrarLogs(IReadOnlyList<string> nomes, bool seguir, bool horario)
{
	using var cancelamento = new CancellationTokenSource();
	Console.CancelKeyPress += (_, evento) =>
	{
		evento.Cancel = true;
		cancelamento.Cancel();
	};

	ConsoleColor[] cores = [ConsoleColor.Cyan, ConsoleColor.Yellow, ConsoleColor.Green, ConsoleColor.Magenta, ConsoleColor.Blue, ConsoleColor.Red];
	var largura = nomes.Max(n => n.Length);
	var varios = nomes.Count > 1;

	servico.LerLogs(nomes, seguir, linhaLog =>
	{
		var momento = horario && linhaLog.Momento is { } m ? $"{m.ToLocalTime():HH:mm:ss.fff} " : "";
		if (varios)
		{
			Console.ForegroundColor = cores[IndiceDe(nomes, linhaLog.Conteiner) % cores.Length];
			Console.Write($"{linhaLog.Conteiner.PadRight(largura)} | ");
			Console.ResetColor();
		}

		Console.WriteLine(momento + linhaLog.Texto);
	}, cancelamento.Token);
}

static int IndiceDe(IReadOnlyList<string> lista, string item)
{
	for (var i = 0; i < lista.Count; i++)
	{
		if (lista[i] == item)
		{
			return i;
		}
	}

	return 0;
}

int Portas(string[] argumentos)
{
	if (argumentos.Length != 1)
	{
		throw new ArgumentException("Uso: minidocker port CONTAINER");
	}

	var conteiner = servico.Obter(argumentos[0]);
	if (conteiner.Estado != EstadoConteiner.Executando)
	{
		throw new InvalidOperationException($"O container '{conteiner.Nome}' nao esta em execucao.");
	}

	foreach (var porta in conteiner.Portas)
	{
		Console.WriteLine($"{porta.PortaContainer}/tcp -> {porta.Ip}:{porta.PortaHostAtual}   (interna {porta.PortaInterna})");
	}

	return 0;
}

int Compose(string[] argumentos)
{
	var linha = LinhaComando.Analisar(argumentos, ["-f", "--follow", "-t", "--timestamps"], ["--file", "-F"]);
	var arquivo = linha.Valor("--file", "-F") ?? MiniDocker.Nucleo.Compose.ArquivoCompose.NomePadrao;
	var subcomando = linha.Posicionais.Count > 0 ? linha.Posicionais[0] : "";
	var definicao = MiniDocker.Nucleo.Compose.ArquivoCompose.Ler(arquivo);
	var compose = new MiniDocker.Nucleo.Compose.ServicoCompose(servico, imagens);

	switch (subcomando)
	{
		case "up":
			Console.WriteLine($"Projeto {definicao.Projeto}: {string.Join(" -> ", definicao.Servicos.Select(s => s.Nome))}");
			foreach (var conteiner in compose.Subir(definicao, Console.WriteLine))
			{
				if (servico.Obter(conteiner.Nome).Estado != EstadoConteiner.Executando)
				{
					IniciarEmSegundoPlano(servico.Obter(conteiner.Nome));
					Console.WriteLine($"[{conteiner.Grupo}] {conteiner.Nome} iniciado");
				}
			}

			ImprimirConteineres(compose.ListarProjeto(definicao.Projeto));
			return 0;

		case "down":
			foreach (var nome in compose.Derrubar(definicao.Projeto))
			{
				Console.WriteLine($"{nome} removido");
			}

			Console.WriteLine("Volumes e imagens foram mantidos.");
			return 0;

		case "ps":
			ImprimirConteineres(compose.ListarProjeto(definicao.Projeto));
			return 0;

		case "logs":
			var nomes = compose.ListarProjeto(definicao.Projeto).Select(c => c.Nome).ToList();
			if (linha.Posicionais.Count > 1)
			{
				var servicos = linha.Posicionais.Skip(1).ToHashSet();
				nomes = compose.ListarProjeto(definicao.Projeto)
					.Where(c => c.Grupo is not null && servicos.Contains(c.Grupo)).Select(c => c.Nome).ToList();
			}

			if (nomes.Count == 0)
			{
				throw new InvalidOperationException($"O projeto '{definicao.Projeto}' nao tem containers. Use: minidocker compose up");
			}

			MostrarLogs(nomes, linha.Tem("-f", "--follow"), linha.Tem("-t", "--timestamps"));
			return 0;

		case "config":
			Console.WriteLine($"projeto: {definicao.Projeto}  (pasta {definicao.Diretorio})");
			foreach (var s in definicao.Servicos)
			{
				Console.WriteLine($"[{s.Nome}] imagem={s.Imagem} replicas={s.Replicas}"
					+ (s.Build is { } b ? $" build={b}" : "")
					+ (s.Portas.Count > 0 ? $" portas={string.Join(",", s.Portas)}" : "")
					+ (s.Volumes.Count > 0 ? $" volumes={string.Join(",", s.Volumes)}" : "")
					+ (s.DependeDe.Count > 0 ? $" depende={string.Join(",", s.DependeDe)}" : ""));
			}

			return 0;

		default:
			throw new ArgumentException("Uso: minidocker compose [--file ARQUIVO] up | down | ps | logs [-f] [SERVICO...] | config");
	}
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
	var linha = LinhaComando.Analisar(argumentos, [], ["-t", "--tag", "-f", "--file", "--cmd", "-e", "--env"]);
	var referencia = linha.Valor("-t", "--tag")
		?? throw new ArgumentException("Informe o nome da imagem com -t. Ex.: minidocker build -t app PASTA");
	var contexto = linha.Posicionais.Count > 0 ? linha.Posicionais[0] : ".";

	var imagem = imagens.Construir(contexto, referencia, linha.Valor("-f", "--file"), Console.WriteLine,
		linha.Valor("--cmd"), LerAmbiente(linha));
	Console.WriteLine($"Imagem {imagem.Id} construida como {imagem.Referencia}");
	Console.WriteLine(imagem.Comando is { } cmd
		? $"Comando padrao: {cmd}"
		: $"Aviso: a imagem nao tem comando padrao. Informe no run (minidocker run {imagem.Repositorio} COMANDO) ou use --cmd no build.");
	return 0;
}

int Volume(string[] argumentos)
{
	var subcomando = argumentos.Length > 0 ? argumentos[0] : "ls";
	var resto = argumentos.Length > 0 ? argumentos[1..] : [];

	switch (subcomando)
	{
		case "ls":
			ImprimirTabela(
				["VOLUME", "TAMANHO", "USADO POR", "CAMINHO"],
				volumes.Listar().Select(v => new[]
				{
					v.Nome, FormatarBytes(v.TamanhoBytes),
					v.Conteineres.Count > 0 ? string.Join(",", v.Conteineres) : "-", v.Caminho
				}));
			return 0;
		case "create":
			return ParaCada(resto, nome => volumes.Criar(nome));
		case "rm":
			return ParaCada(resto, volumes.Remover);
		case "inspect":
			var opcoes = new JsonSerializerOptions { WriteIndented = true };
			Console.WriteLine(JsonSerializer.Serialize(resto.Select(volumes.Obter).ToList(), opcoes));
			return 0;
		default:
			throw new ArgumentException($"Uso: minidocker volume ls | create NOME | rm NOME | inspect NOME");
	}
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
		["REPOSITORIO", "TAG", "IMAGE ID", "COMANDO", "CRIADA", "TAMANHO"],
		lista.Select(i => new[]
		{
			i.Repositorio, i.Tag, i.Id, i.Comando is { } c ? $"\"{Truncar(c, 24)}\"" : "-",
			$"ha {Decorrido(i.CriadaEm)}", FormatarBytes(i.TamanhoBytes)
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
		MemoriaMaximaBytes = linha.Valor("-m", "--memory") is { } memoria ? Unidades.LerBytes(memoria) : null,
		PercentualMaximoCpu = linha.Valor("--cpus") is { } cpus ? Unidades.LerPercentualCpu(cpus) : null,
		ProcessosMaximos = linha.Valor("--pids-limit") is { } processos ? Unidades.LerInteiro(processos, "--pids-limit") : null
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

static void ImprimirConteineres(IEnumerable<Conteiner> conteineres)
{
	ImprimirTabela(
		["CONTAINER ID", "IMAGEM", "COMANDO", "CRIADO", "STATUS", "PORTAS", "NOME"],
		conteineres.Select(c => new[]
		{
			c.Id, c.Imagem, $"\"{Truncar(c.Comando, 24)}\"", $"ha {Decorrido(c.CriadoEm)}", Status(c),
			c.Estado == EstadoConteiner.Executando && c.Portas.Count > 0
				? string.Join(", ", c.Portas.Select(p => p.Descrever())) : "",
			c.Nome
		}));
}

static string Status(Conteiner conteiner) => conteiner.Estado switch
{
	EstadoConteiner.Executando when conteiner.Reinicios > 0 =>
		$"Executando ha {Decorrido(conteiner.IniciadoEm)}, {conteiner.Reinicios} reinicio(s) - ultimo: {conteiner.MotivoTermino}",
	EstadoConteiner.Executando => $"Executando ha {Decorrido(conteiner.IniciadoEm)}",
	EstadoConteiner.Criado => "Criado",
	EstadoConteiner.Parado => $"Parado ({conteiner.CodigoSaida?.ToString() ?? "?"}) ha {Decorrido(conteiner.FinalizadoEm)}",
	_ when conteiner.MotivoTermino is { } motivo =>
		$"{conteiner.Estado} ({conteiner.CodigoSaida?.ToString() ?? "?"}: {motivo}) ha {Decorrido(conteiner.FinalizadoEm)}",
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
		      -v PASTA:DESTINO              monta uma pasta do host no container (repetivel)
		      -v VOLUME:DESTINO             monta um volume nomeado (criado se nao existir)
		      --privileged                  sem confinamento: pode escrever fora do container
		      --restart on-failure[:N]      reinicia se terminar com erro (always: sempre)
		      --replicas N                  cria N copias (NOME-1..NOME-N); exige -d e --name
		      -p 8080:80                    publica a porta 80 do container em 127.0.0.1:8080
		      -p 80                         idem, numa porta do host sorteada
		      --rm                          remove o container ao terminar
		  create [OPCOES] IMAGEM [COMANDO] Cria sem executar
		  start [-a] CONTAINER...          Inicia (em segundo plano; -a anexa)
		  stop CONTAINER...                Encerra o grupo de processos
		  restart CONTAINER...             Para e inicia de novo
		  rm [-f] CONTAINER...             Remove (-f para antes, se preciso)
		  ps [-a] [-q]                     Lista os em execucao (-a: todos)
		  logs [-f] [-t] NOME...           Mostra a saida (-f: acompanha; -t: horario)
		                                   NOME pode ser um grupo: junta as replicas
		  port CONTAINER                   Portas publicadas
		  exec CONTAINER COMANDO           Executa um comando dentro do container
		  stats [CONTAINER...]             Consumo ao vivo, lido do Job Object
		  inspect NOME...                  Detalhes em JSON (container ou imagem)
		  scale GRUPO=N                    Ajusta o numero de replicas de um grupo
		  events [-f] [--erros] [NOME...]  Historico de eventos e erros (-f: acompanha)

		Imagens:
		  build -t NOME[:TAG] PASTA        Transforma a pasta numa imagem
		      --cmd COMANDO                 comando padrao (sem isso: o unico .bat/.cmd/.exe da pasta)
		      -e CHAVE=valor                variavel padrao (repetivel)
		      -f ARQUIVO                    usa um MiniDockerfile (opcional; so para RUN/FROM)
		  images [-q]                      Lista as imagens
		  rmi IMAGEM...                    Remove imagens
		  commit CONTAINER NOME[:TAG]      Cria uma imagem a partir de um container
		  save -o ARQUIVO.zip IMAGEM       Exporta uma imagem
		  load -i ARQUIVO.zip              Importa uma imagem

		Compose (arquivo minicompose.ini; outro com --file ARQUIVO):
		  compose up                       Constroi, cria e inicia todos os servicos
		  compose down                     Para e remove os containers do projeto
		  compose ps                       Containers do projeto
		  compose logs [-f] [SERVICO...]   Logs de todos os servicos, juntos
		  compose config                   Mostra o arquivo interpretado

		Volumes:
		  volume ls                       Lista os volumes nomeados
		  volume create NOME...            Cria volumes
		  volume rm NOME...                Remove volumes (e os dados) que nenhum container usa
		  volume inspect NOME...           Detalhes em JSON

		A imagem "scratch" e embutida e vazia.

		Exemplos:
		  minidocker build -t backup exemplos\backup
		  minidocker run --rm -v C:\docs:origem:ro -v backups:destino backup
		  minidocker run -d --replicas 3 --name worker -v C:\fila:fila --restart on-failure:3 processador
		  minidocker events --erros
		  cd exemplos & minidocker compose up
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

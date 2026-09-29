using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

var servico = new ServicoConteiner();

try
{
	if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
	{
		ExibirAjuda();
		return 0;
	}

	switch (args[0])
	{
		case "run" when args.Length >= 3:
			var (limites, comando) = InterpretarArgumentosDeExecucao(args.Skip(2).ToArray());
			var conteiner = servico.Executar(args[1], comando, limites);
			ExibirConteiner(conteiner);
			break;
		case "ps":
			ExibirTabela(servico.Listar());
			break;
		case "stop" when args.Length == 2:
			servico.Parar(args[1]);
			break;
		case "rm" when args.Length == 2:
			servico.Remover(args[1]);
			break;
		default:
			ExibirAjuda();
			return 1;
	}

	return 0;
}
catch (Exception excecao)
{
	Console.Error.WriteLine($"Erro: {excecao.Message}");
	return 1;
}

static void ExibirAjuda()
{
	Console.WriteLine("Uso:");
	Console.WriteLine("  minidocker run <nome> [limites] <comando>");
	Console.WriteLine("  minidocker ps");
	Console.WriteLine("  minidocker stop <nome>");
	Console.WriteLine("  minidocker rm <nome>");
	Console.WriteLine();
	Console.WriteLine("Limites (aplicados pelo Job Object do Windows):");
	Console.WriteLine("  --memoria <valor>    Memoria maxima. Aceita sufixos K, M e G. Ex.: --memoria 256M");
	Console.WriteLine("  --processos <n>      Numero maximo de processos vivos no grupo.");
	Console.WriteLine("  --cpu <1-100>        Teto de uso de CPU, em porcentagem.");
	Console.WriteLine();
	Console.WriteLine("Exemplo:");
	Console.WriteLine("  minidocker run c1 --memoria 128M --processos 4 \"echo ola\"");
}

// Separa os limites do comando: tudo que vem depois das flags conhecidas
// e seus valores forma o comando a ser executado dentro do container.
static (LimitesRecursos Limites, string Comando) InterpretarArgumentosDeExecucao(string[] argumentos)
{
	long? memoria = null;
	int? processos = null;
	int? cpu = null;
	var indice = 0;

	while (indice < argumentos.Length)
	{
		var flag = argumentos[indice];
		if (flag is not ("--memoria" or "--processos" or "--cpu"))
		{
			break;
		}

		if (indice + 1 >= argumentos.Length)
		{
			throw new ArgumentException($"A opcao {flag} exige um valor.");
		}

		var valor = argumentos[indice + 1];
		switch (flag)
		{
			case "--memoria":
				memoria = InterpretarTamanho(valor);
				break;
			case "--processos":
				processos = InterpretarInteiro(valor, flag);
				break;
			case "--cpu":
				cpu = InterpretarInteiro(valor, flag);
				break;
		}

		indice += 2;
	}

	var comando = string.Join(' ', argumentos.Skip(indice));
	if (string.IsNullOrWhiteSpace(comando))
	{
		throw new ArgumentException("Informe o comando a ser executado no container.");
	}

	var limites = new LimitesRecursos
	{
		MemoriaMaximaBytes = memoria,
		ProcessosMaximos = processos,
		PercentualMaximoCpu = cpu
	};

	return (limites, comando);
}

static long InterpretarTamanho(string valor)
{
	var texto = valor.Trim();
	var multiplicador = 1L;

	if (texto.Length > 0)
	{
		switch (char.ToUpperInvariant(texto[^1]))
		{
			case 'K':
				multiplicador = 1024L;
				texto = texto[..^1];
				break;
			case 'M':
				multiplicador = 1024L * 1024L;
				texto = texto[..^1];
				break;
			case 'G':
				multiplicador = 1024L * 1024L * 1024L;
				texto = texto[..^1];
				break;
		}
	}

	if (!long.TryParse(texto, out var numero))
	{
		throw new ArgumentException($"Valor de memoria invalido: '{valor}'.");
	}

	return numero * multiplicador;
}

static int InterpretarInteiro(string valor, string flag)
{
	if (!int.TryParse(valor, out var numero))
	{
		throw new ArgumentException($"Valor invalido para {flag}: '{valor}'.");
	}

	return numero;
}

static void ExibirTabela(IReadOnlyList<Conteiner> conteineres)
{
	Console.WriteLine("NOME\tID\tESTADO\tPID\tMEM\tCPU(ms)\tPROCS\tLIMITES\tCMD");
	foreach (var conteiner in conteineres)
	{
		ExibirConteiner(conteiner);
	}
}

static void ExibirConteiner(Conteiner conteiner)
{
	var linha = string.Join('\t', conteiner.Nome, conteiner.Id, conteiner.Estado,
		conteiner.IdProcesso?.ToString() ?? "-",
		conteiner.MemoriaUsadaBytes?.ToString() ?? "-",
		conteiner.TempoCpuMs?.ToString("F2") ?? "-",
		conteiner.TotalProcessos?.ToString() ?? "-",
		DescreverLimites(conteiner.Limites),
		conteiner.Comando);
	Console.WriteLine(linha);
}

static string DescreverLimites(LimitesRecursos limites)
{
	if (!limites.AlgumDefinido)
	{
		return "-";
	}

	var partes = new List<string>();
	if (limites.MemoriaMaximaBytes is { } memoria)
	{
		partes.Add($"mem={memoria}");
	}

	if (limites.ProcessosMaximos is { } processos)
	{
		partes.Add($"procs={processos}");
	}

	if (limites.PercentualMaximoCpu is { } cpu)
	{
		partes.Add($"cpu={cpu}%");
	}

	return string.Join(',', partes);
}

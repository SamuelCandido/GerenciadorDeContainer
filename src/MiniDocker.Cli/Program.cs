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
			var conteiner = servico.Executar(args[1], string.Join(' ', args.Skip(2)));
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
	Console.WriteLine("  minidocker run <nome> <comando>");
	Console.WriteLine("  minidocker ps");
	Console.WriteLine("  minidocker stop <nome>");
	Console.WriteLine("  minidocker rm <nome>");
	Console.WriteLine();
	Console.WriteLine("Exemplo:");
	Console.WriteLine("  minidocker run c1 \"echo ola\"");
	Console.WriteLine();
	Console.WriteLine("Limites de memoria, CPU e numero de processos chegam na 2a entrega.");
}

static void ExibirTabela(IReadOnlyList<Conteiner> conteineres)
{
	Console.WriteLine("NOME\tID\tESTADO\tPID\tMEM\tCPU(ms)\tPROCS\tCMD");
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
		conteiner.Comando);
	Console.WriteLine(linha);
}

using MiniDocker.Nucleo.Modelos;
using MiniDocker.Nucleo.Servicos;

namespace MiniDocker.Nucleo.Compose;

/// <summary>
/// <c>compose up/down</c>: transforma o arquivo em imagens e containers. Cada
/// serviço vira um grupo de réplicas (<c>painel-1</c>, <c>processador-1..3</c>),
/// marcado com o nome do projeto. Iniciar os containers fica com quem chama
/// (a CLI), porque é ela que lança o shim de cada um.
/// </summary>
public sealed class ServicoCompose(IServicoConteiner conteineres, IServicoImagem imagens)
{
    /// <summary>
    /// Constrói as imagens com <c>build</c> e deixa cada serviço com o número
    /// de réplicas pedido, criando ou removendo containers. Devolve os
    /// containers do projeto na ordem de subida — quem não estiver rodando
    /// deve ser iniciado nessa ordem.
    /// </summary>
    public IReadOnlyList<Conteiner> Subir(DefinicaoCompose compose, Action<string>? saida = null)
    {
        saida ??= _ => { };
        var resultado = new List<Conteiner>();

        foreach (var servico in compose.Servicos)
        {
            if (servico.Build is { } pasta)
            {
                saida($"[{servico.Nome}] construindo a imagem {servico.Imagem} de {pasta}");
                imagens.Construir(pasta, servico.Imagem, saida: linha => saida($"[{servico.Nome}]   {linha}"));
            }

            var membros = conteineres.ListarGrupo(servico.Nome);
            if (membros.FirstOrDefault(c => c.Projeto != compose.Projeto) is { } estranho)
            {
                throw new InvalidOperationException(
                    $"Ja existe o container '{estranho.Nome}' fora do projeto '{compose.Projeto}'. Remova-o ou renomeie o servico.");
            }

            if (membros.Count == 0)
            {
                var opcoes = Opcoes(compose, servico);
                for (var indice = 1; indice <= servico.Replicas; indice++)
                {
                    conteineres.Criar(ServicoConteiner.NomeReplica(servico.Nome, indice), servico.Imagem,
                        servico.Comando, opcoes);
                }

                saida($"[{servico.Nome}] {servico.Replicas} container(s) criado(s)");
            }
            else if (membros.Count != servico.Replicas)
            {
                conteineres.Escalar(servico.Nome, servico.Replicas);
                saida($"[{servico.Nome}] escalado de {membros.Count} para {servico.Replicas}");
            }

            resultado.AddRange(conteineres.ListarGrupo(servico.Nome));
        }

        return resultado;
    }

    /// <summary>Para e remove todos os containers do projeto. Volumes e imagens ficam.</summary>
    public IReadOnlyList<string> Derrubar(string projeto)
    {
        // Os mais novos primeiro: quem depende sai antes de quem é dependido.
        var removidos = new List<string>();
        foreach (var conteiner in ListarProjeto(projeto).OrderByDescending(c => c.CriadoEm))
        {
            conteineres.Remover(conteiner.Nome, forcar: true);
            removidos.Add(conteiner.Nome);
        }

        return removidos;
    }

    public IReadOnlyList<Conteiner> ListarProjeto(string projeto) =>
        conteineres.Listar()
            .Where(c => c.Projeto == projeto)
            .OrderBy(c => c.CriadoEm)
            .ToList();

    private static OpcoesConteiner Opcoes(DefinicaoCompose compose, DefinicaoServico servico) => new()
    {
        Ambiente = servico.Ambiente,
        Limites = servico.Limites,
        Volumes = servico.Volumes,
        Portas = servico.Portas,
        Reinicio = servico.Reinicio,
        Privilegiado = servico.Privilegiado,
        Grupo = servico.Nome,
        Projeto = compose.Projeto
    };
}

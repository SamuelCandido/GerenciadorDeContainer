# Etapa 3 (planejada) — Controle fino e confinamento

Conceitos da etapa final. As anteriores estão em [ETAPA-1.md](ETAPA-1.md) (Job Objects, isolamento, contabilidade) e [ETAPA-2.md](ETAPA-2.md) (segundo plano, job com nome, imagens).

Esta etapa ainda não foi implementada. Cada seção descreve o recurso, como implementá-lo e o conceito de SO por trás.

| Item | O que traz |
|---|---|
| [Volumes](#1-volumes) | Dados que sobrevivem ao `rm` |
| [Política de restart](#2-política-de-restart) | O container se recupera sozinho de uma falha |
| [Eventos do kernel](#3-eventos-do-kernel) | Saber por que um container morreu, sem ficar consultando |
| [Criação suspensa](#4-criação-suspensa-create_suspended) | Fecha a última corrida da Etapa 1 |
| [Confinamento do sistema de arquivos](#5-confinamento-do-sistema-de-arquivos) | O container não escreve fora da própria pasta |

## 1. Volumes

**O que é:** `-v C:\dados:dados` faz uma pasta do host aparecer dentro do rootfs. É assim que um container guarda dados que sobrevivem ao `rm`.

**Como fazer:** uma **junction** NTFS dentro do rootfs apontando para a pasta do host (o mesmo que `mklink /J`). Não exige privilégio de administrador, ao contrário de um link simbólico.

**Conceito de SO:** pontos de reanálise (*reparse points*) do NTFS — o sistema de arquivos redireciona a abertura do caminho para outro lugar. É a versão Windows de um *bind mount*. O `rm` precisa remover a junction **sem** seguir o link, senão apagaria os dados do host.

## 2. Política de restart

**O que é:** `--restart no | on-failure[:N] | always`, como no Docker.

**Como fazer:** o shim, ao ver o processo principal terminar, consulta a política e o código de saída e, se for o caso, inicia de novo dentro do mesmo Job Object, com espera crescente entre tentativas (*backoff* exponencial). Um `stop` explícito desliga o reinício.

**Conceito de SO:** supervisão de processos — o papel do `init`/`systemd` no Linux e do Gerenciador de Controle de Serviços no Windows (ações de recuperação de um serviço).

## 3. Eventos do kernel

**O que é:** o equivalente do `docker events`: saber, sem ficar consultando, que um processo nasceu, morreu ou estourou o limite de memória.

**Como fazer:** associar o Job Object a uma **porta de conclusão de E/S** (`JobObjectAssociateCompletionPortInformation`). O kernel passa a postar mensagens nela — `JOB_OBJECT_MSG_NEW_PROCESS`, `EXIT_PROCESS`, `ABNORMAL_EXIT_PROCESS`, `PROCESS_MEMORY_LIMIT`, `ACTIVE_PROCESS_LIMIT`. O shim lê a porta numa thread e grava os eventos num arquivo que o comando `events` acompanha.

**Conceito de SO:** notificação dirigida pelo kernel em vez de *polling*; portas de conclusão como fila de eventos. Permite, por exemplo, mostrar no `ps` "Finalizado (limite de memória)" em vez de só um código de saída.

## 4. Criação suspensa (`CREATE_SUSPENDED`)

**O que é:** fechar a janela entre `Process.Start` e a atribuição ao Job Object (ver [ETAPA-1.md, seção 5](ETAPA-1.md#5-uma-corrida-que-não-deu-para-fechar)).

**Como fazer:** chamar `CreateProcess` por P/Invoke com `CREATE_SUSPENDED`. O processo nasce com a thread principal suspensa, é atribuído ao job e só então liberado com `ResumeThread`. Ele não executa nenhuma instrução fora do grupo. É o que o Docker faz.

**Conceito de SO:** o contador de suspensão de threads — enquanto ele for maior que zero, o escalonador não executa a thread. Exige montar à mão o que a classe `Process` esconde: o bloco de ambiente, os pipes de saída herdáveis e `STARTUPINFO`.

## 5. Confinamento do sistema de arquivos

**O que é:** impedir que o container escreva fora do rootfs. Hoje ele roda com a identidade de quem o lançou e escreve onde essa pessoa escreveria.

**Como fazer:** rodar o processo com **nível de integridade baixo** — o mecanismo do modo protegido dos navegadores. Duplicar o token do usuário, baixar o rótulo para *Low* (`SetTokenInformation(TokenIntegrityLevel)`), marcar o rootfs como *Low* e criar o processo com `CreateProcessAsUser`. A regra *no-write-up* do Windows faz o resto: um processo *Low* não escreve em objetos *Medium*, que é o padrão de praticamente todo o disco.

**Conceito de SO:** controle de acesso obrigatório (MAC) sobreposto ao discricionário (DAC/ACLs). A ACL diz que o usuário pode escrever; o rótulo de integridade diz que **este processo** do usuário não pode. Combinado com a criação suspensa, o processo já nasce rotulado, dentro do job.

**Limite honesto:** a regra padrão bloqueia escrita, não leitura — o container ainda **lê** fora do rootfs. Bloquear leitura exigiria rotular os objetos com *no-read-up*, o que não é viável para o disco inteiro.

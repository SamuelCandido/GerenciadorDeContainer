# Etapa 1 — Ciclo de vida e controle de recursos

Explicação dos conceitos de Sistemas Operacionais aplicados nesta entrega do MiniDocker.

## 1. O problema: o que é um container, afinal?

A intuição comum é que um container é "uma máquina virtual leve". Não é. Um container é **um processo comum do sistema operacional** com duas coisas aplicadas em cima dele:

| Mecanismo | O que faz | Linux | Windows |
|---|---|---|---|
| **Isolamento** | O processo enxerga uma visão reduzida do sistema | namespaces (PID, mount, rede, UTS) | Server Silos |
| **Controle de recursos** | O processo não consome além de um teto | cgroups | **Job Objects** |

Não existe instrução de CPU chamada "container". O kernel é quem sustenta a ilusão: quando um processo dentro de um container roda `ps` e vê só a si mesmo, é porque o namespace de PID está filtrando o que ele enxerga.

Esta etapa implementa a **segunda linha da tabela**: controle de recursos via Job Objects. A primeira linha está deliberadamente fora, pelo motivo explicado na [seção 6](#6-por-que-não-implementamos-isolamento).

## 2. Por que Job Objects

Um **Job Object** é um objeto do kernel do Windows que agrupa processos. Ele oferece exatamente as três capacidades que caracterizam controle de recursos:

1. **Agrupamento com herança** — todo processo criado por um membro do grupo nasce dentro do mesmo job. Não há como um processo-neto escapar.
2. **Limites impostos pelo kernel** — memória, número de processos, tempo e taxa de CPU. Não é o MiniDocker que verifica; é o kernel que recusa a operação.
3. **Contabilidade agregada** — o consumo somado de todos os processos do grupo, incluindo os que já morreram.

É o análogo direto dos cgroups do Linux. Os dois resolvem o mesmo problema com a mesma estratégia: agrupar processos e cobrar do grupo.

## 3. O que foi implementado

### 3.1 Limites impostos

Definidos em [`LimitesRecursos`](../src/MiniDocker.Nucleo/Modelos/LimitesRecursos.cs) e aplicados em [`ObjetoTrabalho`](../src/MiniDocker.Nucleo/Interop/ObjetoTrabalho.cs):

| Limite | Flag do Windows | O que acontece ao estourar |
|---|---|---|
| Memória | `JOB_OBJECT_LIMIT_PROCESS_MEMORY` | A alocação **falha** — `malloc`/`new` retorna erro |
| Nº de processos | `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` | `CreateProcess` **falha** — barra fork bombs |
| CPU | `JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP` | O **escalonador** não dá mais fatias ao grupo |
| Encerramento | `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` | Fechar o handle mata o grupo inteiro |

O ponto a enfatizar: **isto não é validação em C#**. O MiniDocker configura o job e sai do caminho. Quem recusa a alocação de memória é o gerenciador de memória do Windows; quem recusa o processo é o gerenciador de processos; quem limita a CPU é o escalonador.

### 3.2 Contabilidade correta

A versão anterior do código lia as métricas assim:

```csharp
conteiner.MemoriaUsadaBytes = processo.WorkingSet64;
conteiner.TempoCpuMs = processo.TotalProcessorTime.TotalMilliseconds;
```

Isso tem **dois defeitos**, e ambos são conceituais:

1. **Condição de corrida.** Essas propriedades só respondem enquanto o processo está vivo. Para um comando rápido como `echo`, o processo frequentemente já morreu quando a leitura acontece — o código antigo tinha um `try/catch` que zerava as métricas justamente por causa disso.
2. **Escopo errado.** Elas medem **um** processo. Se o comando criar filhos, o consumo deles não aparece.

A contabilidade do Job Object resolve os dois: ela é **do grupo** e **sobrevive ao término dos processos**, porque o job continua existindo enquanto o handle estiver aberto. Por isso `QueryInformationJobObject` é chamado **depois** do `WaitForExit`, sem corrida nenhuma.

| Métrica | De onde vem |
|---|---|
| Pico de memória | `PeakJobMemoryUsed` |
| Tempo de CPU | `TotalUserTime + TotalKernelTime` (unidades de 100 ns) |
| Total de processos | `TotalProcesses` — conta os netos também |
| Falhas de página | `TotalPageFaultCount` |

### 3.3 Sem processos órfãos

Sem `KILL_ON_JOB_CLOSE`, um comando como `start /b ping -n 100 127.0.0.1` deixaria o `ping` rodando solto depois que o container terminasse. Com a flag, fechar o handle do job encerra todo o grupo. É o comportamento que se espera de um container: quando ele morre, nada dele sobra.

## 4. Ciclo de vida

```text
run c1 --memoria 128M "comando"
    │
    ├─ 1. cria o diretório rootfs
    ├─ 2. cria o Job Object e aplica os limites      ← kernel passa a impor
    ├─ 3. inicia o cmd.exe com WorkingDirectory=rootfs
    ├─ 4. atribui o processo ao Job Object           ← descendentes herdam
    ├─ 5. aguarda o término
    ├─ 6. lê a contabilidade do grupo                ← sobrevive ao término
    └─ 7. fecha o Job Object                         ← mata o que sobrou
```

Estados: `Criado` → `Executando` → `Finalizado`.

O registro em `containers.json` funciona como um **PCB (Process Control Block) simplificado**: guarda identificador, estado, PID, código de saída, limites e contabilidade — os mesmos campos que o SO mantém na sua própria estrutura de controle de processo.

## 5. Uma corrida que não deu para fechar

Existe uma janela entre o passo 3 e o passo 4: o processo já está rodando antes de ser atribuído ao job.

O jeito correto de eliminá-la é criar o processo **suspenso** (`CREATE_SUSPENDED`), atribuí-lo ao job e só então liberá-lo — que é exatamente o que o Docker faz. A classe `Process` do .NET não expõe essa flag; fechar a janela exige chamar `CreateProcess` diretamente por P/Invoke.

Na prática a janela é de microssegundos contra os ~10 ms de inicialização do `cmd.exe`, então a atribuição vence. O código trata o caso sem quebrar: se o processo já terminou, não há mais o que limitar. Está documentado em [`ObjetoTrabalho.Atribuir`](../src/MiniDocker.Nucleo/Interop/ObjetoTrabalho.cs) e é candidato natural à 2ª entrega.

## 6. Por que não implementamos isolamento

Porque no Windows ele não está ao alcance de um projeto feito à mão.

No Linux, isolamento é acessível: `clone()` com as flags `CLONE_NEWPID | CLONE_NEWNS | CLONE_NEWUTS` cria namespaces novos, e `pivot_root` troca a raiz do sistema de arquivos. São algumas centenas de linhas.

No Windows não existe equivalente público. O mecanismo é o **Server Silo**, criado por APIs não documentadas (`NtCreateJobSet` e atributos de silo) e usado pelo **Host Compute System**, a camada sobre a qual o Docker for Windows roda. Usá-lo não seria construir um container — seria chamar o container da Microsoft.

Então a entrega é honesta sobre o limite: **impõe recursos, não isola namespaces**. Isso está registrado no README, não escondido.

### Verificação da ausência de isolamento

```bat
minidocker run sonda "cd & type C:\Windows\System32\drivers\etc\hosts & tasklist & whoami"
```

O container lê um arquivo fora do rootfs, lista os processos do host e reporta o usuário do host — nenhuma fronteira foi aplicada.

Esta mesma verificação foi feita na versão multiplataforma anterior do código, que usava `/bin/sh -c` no lugar de `cmd.exe /c` mantendo a mesma chamada a `Process.Start`. O processo do container rodou nos **namespaces iniciais do host**, com IDs idênticos aos do pai:

```text
container  mnt:[4026531832]   pid:[4026531836]
host       mnt:[4026531832]   pid:[4026531836]
```

## 7. Demonstração

```bat
:: 1. fork bomb barrada no limite de 3 processos
minidocker run bomba --processos 3 "for /L %i in (1,1,50) do start /b cmd /c ping -n 5 127.0.0.1"

:: 2. nenhum processo sobra: o ping morre junto com o container
minidocker run orfao "start /b ping -n 100 127.0.0.1"

:: 3. teto de CPU observável no Gerenciador de Tarefas
minidocker run cpu --cpu 10 "for /L %i in (1,1,100000000) do rem"

:: 4. contabilidade cobre os netos: PROCS mostra 2, não 1
minidocker run conta "ping -n 3 127.0.0.1"
minidocker ps
```

## 8. Perguntas prováveis

**"Isso é um container de verdade?"**
Não completo. Implementa controle de recursos, que é metade do que define um container, e não implementa isolamento, que é a outra metade. No Windows a outra metade exige o Host Compute System, que é o runtime da própria Microsoft.

**"Qual a diferença entre limitar e observar?"**
Observar é ler quanto o processo consumiu e anotar. Limitar é fazer o kernel recusar a operação que ultrapassa o teto. A entrega anterior só observava; esta impõe.

**"Por que não usar `Process.WorkingSet64`?"**
Porque mede um processo só e exige que ele esteja vivo — é uma corrida contra comandos rápidos. A contabilidade do job cobre o grupo inteiro e sobrevive ao término.

**"O que acontece se o comando criar mil processos?"**
Com `--processos N`, a criação do processo N+1 falha. Sem o limite, o grupo cresce, mas ainda é encerrado inteiro quando o job fecha.

**"Job Object é o mesmo que cgroup?"**
Em propósito, sim: agrupar processos e impor tetos ao grupo. Difere nos detalhes — cgroups são hierárquicos e expostos como sistema de arquivos; Job Objects são objetos do kernel manipulados por API e permitem aninhamento desde o Windows 8.

**"Por que os testes são pulados fora do Windows?"**
Porque dependem do `cmd.exe` e da API de Job Objects. Pular é mais honesto que falhar: deixa claro que não foram verificados naquele ambiente, em vez de fingir cobertura.

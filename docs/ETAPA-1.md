# Etapa 1 — Isolamento parcial e ciclo de vida

Explicação dos conceitos de Sistemas Operacionais aplicados nesta entrega do MiniDocker.

## 1. O problema: o que é um container, afinal?

A intuição comum é que um container é "uma máquina virtual leve". Não é. Um container é **um processo comum do sistema operacional** com duas coisas aplicadas em cima dele:

| Mecanismo | O que faz | Linux | Windows |
|---|---|---|---|
| **Isolamento** | O processo enxerga uma visão reduzida do sistema | namespaces (PID, mount, rede, UTS) | Server Silos |
| **Controle de recursos** | O processo não consome além de um teto | cgroups | **Job Objects** |

Não existe instrução de CPU chamada "container". O kernel é quem sustenta a ilusão: quando um processo dentro de um container roda `ps` e vê só a si mesmo, é porque o namespace de PID está filtrando o que ele enxerga.

Esta etapa usa **Job Objects** e tira deles o que eles oferecem nas duas linhas da tabela: o agrupamento e a contabilidade da segunda, e a parte da primeira que a API documentada alcança — o subsistema de janelas. O que fica de fora, e por quê, está na [seção 6](#6-o-que-não-está-isolado-e-por-quê).

### Por que "mini" Docker

| | VM | Docker | MiniDocker |
|---|---|---|---|
| Kernel próprio | sim | não — compartilha o do host | não — compartilha o do host |
| Empacota os arquivos do sistema (imagem) | disco inteiro | imagem com o userland (`ubuntu`, `nanoserver`) | **não** — usa os binários do host |
| Isolamento de namespaces | total | PID, rede, sistema de arquivos | só subsistema de janelas e ambiente |
| Controle de recursos | sim | cgroups / Job Objects | Job Objects |

Nem o Docker empacota o sistema operacional inteiro — quem faz isso é a VM. O Docker empacota os **arquivos** de um sistema, mas o kernel é sempre o do host. O MiniDocker não empacota nada: o container roda o `cmd.exe` do próprio `C:\Windows\System32`, e o rootfs é um diretório de trabalho vazio. O que os dois têm em comum é o mecanismo de base — o Docker for Windows também usa Job Objects por baixo.

## 2. Por que Job Objects

Um **Job Object** é um objeto do kernel do Windows que agrupa processos. Ele oferece:

1. **Agrupamento com herança** — todo processo criado por um membro do grupo nasce dentro do mesmo job. Não há como um processo-neto escapar.
2. **Restrições impostas pelo kernel** — de interface, de memória, de número de processos, de CPU. Não é o MiniDocker que verifica; é o kernel que recusa a operação.
3. **Contabilidade agregada** — o consumo somado de todos os processos do grupo, incluindo os que já morreram.

É o análogo direto dos cgroups do Linux. Os dois resolvem o mesmo problema com a mesma estratégia: agrupar processos e cobrar do grupo.

## 3. O que foi implementado

### 3.1 Restrições impostas pelo kernel

Aplicadas em [`ObjetoTrabalho`](../src/MiniDocker.Nucleo/Interop/ObjetoTrabalho.cs) a todo container:

| Restrição | Flag do Windows | Efeito |
|---|---|---|
| Atoms globais | `UILIMIT_GLOBALATOMS` | O grupo recebe uma **tabela de atoms própria** — um namespace privado |
| Handles de janela | `UILIMIT_HANDLES` | Não alcança janelas de processos de fora do grupo |
| Área de transferência | `UILIMIT_READCLIPBOARD` / `WRITECLIPBOARD` | Não lê nem escreve no clipboard do host |
| Desktop | `UILIMIT_DESKTOP` | Não cria nem troca de desktop |
| Configurações | `UILIMIT_SYSTEMPARAMETERS` / `DISPLAYSETTINGS` | Não altera parâmetros do sistema |
| Desligamento | `UILIMIT_EXITWINDOWS` | Não desliga nem reinicia o Windows |
| Encerramento | `LIMIT_KILL_ON_JOB_CLOSE` | Fechar o handle mata o grupo inteiro |

O ponto a enfatizar: **isto não é validação em C#**. O MiniDocker configura o job e sai do caminho. Quando o container tenta usar um desses recursos, quem nega é o Windows.

### 3.2 Ambiente próprio

O processo do container **não herda as variáveis de ambiente do host**. `ServicoConteiner.IsolarAmbiente` limpa o ambiente e passa só o mínimo para o `cmd.exe` funcionar — `SystemRoot`, `ComSpec`, `Path` — com `TEMP` e `TMP` apontando para o rootfs. Credenciais e caminhos pessoais do host não entram.

### 3.3 Contabilidade correta

Uma versão anterior do código lia as métricas assim:

```csharp
conteiner.MemoriaUsadaBytes = processo.WorkingSet64;
conteiner.TempoCpuMs = processo.TotalProcessorTime.TotalMilliseconds;
```

Isso tem **dois defeitos**, e ambos são conceituais:

1. **Condição de corrida.** Essas propriedades só respondem enquanto o processo está vivo. Para um comando rápido como `echo`, o processo frequentemente já morreu quando a leitura acontece.
2. **Escopo errado.** Elas medem **um** processo. Se o comando criar filhos, o consumo deles não aparece.

A contabilidade do Job Object resolve os dois: ela é **do grupo** e **sobrevive ao término dos processos**, porque o job continua existindo enquanto o handle estiver aberto. Por isso `QueryInformationJobObject` é chamado **depois** do `WaitForExit`, sem corrida nenhuma.

| Métrica | De onde vem |
|---|---|
| Pico de memória | `PeakJobMemoryUsed` |
| Tempo de CPU | `TotalUserTime + TotalKernelTime` (unidades de 100 ns) |
| Total de processos | `TotalProcesses` — conta os netos também |
| Falhas de página | `TotalPageFaultCount` |

### 3.4 Sem processos órfãos

Sem `KILL_ON_JOB_CLOSE`, um comando como `start /b ping -n 100 127.0.0.1` deixaria o `ping` rodando solto depois que o container terminasse. Com a flag, fechar o handle do job encerra todo o grupo. É o comportamento que se espera de um container: quando ele morre, nada dele sobra.

### 3.5 Limites de recursos — prontos no núcleo, fora da CLI

O núcleo já sabe impor limites de memória (`JOB_OBJECT_LIMIT_PROCESS_MEMORY`), de número de processos (`JOB_OBJECT_LIMIT_ACTIVE_PROCESS`) e de CPU (`JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP`), definidos em [`LimitesRecursos`](../src/MiniDocker.Nucleo/Modelos/LimitesRecursos.cs). Eles são aceitos por `ServicoConteiner.Executar` e validados, mas **a CLI ainda não expõe as flags** — isso entra na 2ª entrega.

## 4. Ciclo de vida

```text
run c1 "comando"
    │
    ├─ 1. cria o diretório rootfs
    ├─ 2. cria o Job Object e aplica as restrições  ← kernel passa a impor
    ├─ 3. monta um ambiente mínimo, sem herdar o host
    ├─ 4. inicia o cmd.exe com WorkingDirectory=rootfs
    ├─ 5. atribui o processo ao Job Object          ← descendentes herdam
    ├─ 6. aguarda o término
    ├─ 7. lê a contabilidade do grupo               ← sobrevive ao término
    └─ 8. fecha o Job Object                        ← mata o que sobrou
```

Estados: `Criado` → `Executando` → `Finalizado`. Se a execução falhar no meio, o container também termina em `Finalizado`, com `CodigoSaida` nulo — nunca fica preso em `Executando`.

O registro em `containers.json` funciona como um **PCB (Process Control Block) simplificado**: guarda identificador, estado, PID, código de saída, limites e contabilidade — os mesmos campos que o SO mantém na sua própria estrutura de controle de processo.

## 5. Uma corrida que não deu para fechar

Existe uma janela entre o passo 4 e o passo 5: o processo já está rodando antes de ser atribuído ao job.

O jeito correto de eliminá-la é criar o processo **suspenso** (`CREATE_SUSPENDED`), atribuí-lo ao job e só então liberá-lo — que é exatamente o que o Docker faz. A classe `Process` do .NET não expõe essa flag; fechar a janela exige chamar `CreateProcess` diretamente por P/Invoke.

Na prática a janela é de microssegundos contra os ~10 ms de inicialização do `cmd.exe`, então a atribuição vence. O código trata o caso sem quebrar: se o processo já terminou, não há mais o que limitar. Está documentado em [`ObjetoTrabalho.Atribuir`](../src/MiniDocker.Nucleo/Interop/ObjetoTrabalho.cs) e é candidato natural à 2ª entrega.

## 6. O que não está isolado, e por quê

### Sistema de arquivos e privilégios — 2ª entrega

O container roda com a **identidade do usuário que o lançou**, então tem os mesmos direitos que ele: lê e escreve fora do rootfs. Uma ACL sozinha não resolve isso. Confinar a escrita ao rootfs e derrubar privilégios exige um **token restrito** (`CreateRestrictedToken` + `CreateProcessAsUser`).

### Namespace de processos e rede — fora de alcance

No Linux, isolamento é acessível: `clone()` com as flags `CLONE_NEWPID | CLONE_NEWNS | CLONE_NEWUTS` cria namespaces novos, e `pivot_root` troca a raiz do sistema de arquivos. São algumas centenas de linhas.

No Windows não existe equivalente público. O mecanismo é o **Server Silo**, criado por APIs não documentadas e usado pelo **Host Compute System**, a camada sobre a qual o Docker for Windows roda. Usá-lo não seria construir um container — seria chamar o container da Microsoft.

Então a entrega é honesta sobre o limite: **isola o subsistema de janelas e o ambiente, não isola processos, rede nem sistema de arquivos**.

### Verificação

```bat
minidocker run sonda "cd & type C:\Windows\System32\drivers\etc\hosts & tasklist & whoami"
```

O container lê um arquivo fora do rootfs, lista os processos do host e reporta o usuário do host — essas fronteiras não foram aplicadas.

## 7. Demonstração

```bat
:: 1. o ambiente do host nao entra no container: imprime a referencia literal
set SEGREDO_DO_HOST=senha-123
minidocker run amb "echo [%SEGREDO_DO_HOST%]"

:: 2. nenhum processo sobra: o ping morre junto com o container
minidocker run orfao "start /b ping -n 100 127.0.0.1"

:: 3. contabilidade cobre os netos: PROCS mostra 2, nao 1
minidocker run conta "ping -n 3 127.0.0.1"
minidocker ps

:: 4. o que ainda NAO esta isolado
minidocker run sonda "cd & type C:\Windows\System32\drivers\etc\hosts & tasklist & whoami"
```

## 8. Perguntas prováveis

**"Isso é um container de verdade?"**
Parcial. Tem o grupo de processos, as restrições impostas pelo kernel, o ambiente próprio e o ciclo de vida. Não tem imagem nem isolamento de processos, rede e sistema de arquivos — no Windows, esses últimos exigem o Host Compute System, que é o runtime da própria Microsoft.

**"Qual a diferença para o Docker?"**
O Docker empacota uma imagem com os arquivos do sistema e isola namespaces. O MiniDocker usa os binários do próprio host e isola só o subsistema de janelas e o ambiente. Nenhum dos dois empacota o kernel — isso é VM.

**"Qual a diferença entre limitar e observar?"**
Observar é ler quanto o processo consumiu e anotar. Limitar é fazer o kernel recusar a operação que ultrapassa o teto. As restrições de interface já são impostas; os limites de memória, CPU e processos estão prontos no núcleo e chegam à CLI na 2ª entrega.

**"Por que não usar `Process.WorkingSet64`?"**
Porque mede um processo só e exige que ele esteja vivo — é uma corrida contra comandos rápidos. A contabilidade do job cobre o grupo inteiro e sobrevive ao término.

**"O que acontece se o comando criar mil processos?"**
Hoje o grupo cresce, mas é encerrado inteiro quando o job fecha. Com o limite de processos exposto na 2ª entrega, a criação do processo N+1 falha.

**"Job Object é o mesmo que cgroup?"**
Em propósito, sim: agrupar processos e impor tetos ao grupo. Difere nos detalhes — cgroups são hierárquicos e expostos como sistema de arquivos; Job Objects são objetos do kernel manipulados por API e permitem aninhamento desde o Windows 8.

**"Por que os testes são pulados fora do Windows?"**
Porque dependem do `cmd.exe` e da API de Job Objects. Pular é mais honesto que falhar: deixa claro que não foram verificados naquele ambiente, em vez de fingir cobertura.

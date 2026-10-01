# Passeio pelo código

Explicação arquivo por arquivo do MiniDocker. Este documento cobre **o código**; os conceitos de Sistemas Operacionais por trás dele estão em [ETAPA-1.md](ETAPA-1.md).

## 1. Arquitetura em camadas

```text
┌─────────────────────────────────────────────┐
│  MiniDocker.Cli                             │  interpreta argumentos, imprime
│    Program.cs                               │
└───────────────────┬─────────────────────────┘
                    │ depende de
┌───────────────────▼─────────────────────────┐
│  MiniDocker.Nucleo                          │
│                                             │
│   Servicos/      orquestra o ciclo de vida  │
│       │                                     │
│       ├──> Modelos/       dados             │
│       ├──> Repositorios/  persistência JSON │
│       └──> Interop/       API do Windows    │
└───────────────────┬─────────────────────────┘
                    │ P/Invoke
┌───────────────────▼─────────────────────────┐
│  kernel32.dll  →  kernel do Windows         │
└─────────────────────────────────────────────┘
```

A regra é que as setas só apontam para baixo. O `Cli` não conhece Job Object; o `Nucleo` não sabe que existe uma linha de comando. Isso é o que permite os testes exercitarem o serviço sem passar pelo CLI.

**Por que dois projetos?** Porque a lógica de container não deveria depender de como ela é acionada. Trocar o CLI por uma interface gráfica ou por uma API web não exigiria tocar no `Nucleo`.

## 2. O percurso de um comando

Vale decorar este caminho — é a espinha da explicação:

```text
minidocker run c1 "echo ola"
        │
        ▼
Program.cs                    junta o resto dos argumentos no comando
        │
        ▼
ServicoConteiner.Executar     valida, cria o registro
        │
        ├──► ObjetoTrabalho    cria o Job Object e aplica as restrições
        │         │
        │         └──► NativeMethods ──► kernel32.dll ──► kernel
        │
        ├──► IsolarAmbiente    monta o ambiente mínimo, sem herdar o host
        ├──► Process.Start     lança o cmd.exe
        ├──► Atribuir          põe o processo dentro do job
        ├──► WaitForExit       aguarda
        ├──► Contabilizar      lê o consumo do grupo
        │
        ▼
RepositorioConteinerArquivo   grava em containers.json
```

O diagrama de classes completo está em [plantuml/uml.txt](plantuml/uml.txt).

## 3. Camada de modelos

### `Modelos/Conteiner.cs`

A representação de um container. Funciona como um **PCB (Process Control Block) simplificado** — a estrutura que o próprio SO mantém para controlar cada processo.

| Campo | Papel |
|---|---|
| `Id` | 6 bytes aleatórios em hexadecimal, no estilo do Docker |
| `Nome` | Identificador escolhido pelo usuário, único |
| `Comando` | O que executar dentro do container |
| `Estado` | Onde está no ciclo de vida |
| `CriadoEm` / `IniciadoEm` / `FinalizadoEm` | Marcas de tempo do ciclo |
| `CaminhoRootFs` | Diretório do container |
| `IdProcesso` | O PID real atribuído pelo SO |
| `CodigoSaida` | Código de retorno do processo |
| `Limites` | Os tetos aplicados ao grupo |
| `MemoriaUsadaBytes`, `TempoCpuMs`, `TotalProcessos`, `FalhasDePagina` | Contabilidade lida do Job Object |

Os campos de contabilidade são anuláveis (`long?`) de propósito: `null` significa "ainda não medido", que é diferente de "medido e deu zero".

### `Modelos/EstadoConteiner.cs`

```csharp
public enum EstadoConteiner { Criado, Executando, Parado, Finalizado }
```

O ciclo usado hoje é `Criado → Executando → Finalizado`. **`Parado` ainda não é usado** — ele existe para a 2ª entrega, quando `stop` passa a interromper um container em segundo plano. Vale dizer isso em vez de deixar o professor descobrir.

### `Modelos/LimitesRecursos.cs`

Um `record` imutável com os três tetos, todos opcionais:

```csharp
public long? MemoriaMaximaBytes { get; init; }
public int?  ProcessosMaximos   { get; init; }
public int?  PercentualMaximoCpu { get; init; }
```

`null` significa "sem limite para este recurso". `LimitesRecursos.Nenhum` é a instância compartilhada sem nenhum limite — e é a que a CLI usa hoje, já que as flags de limite só chegam na 2ª entrega. O núcleo já aceita e aplica os limites; falta só expô-los na linha de comando.

`Validar()` recusa valores impossíveis — memória ou processos ≤ 0, CPU fora de 1–100 — lançando `ArgumentOutOfRangeException`. **É chamado no começo do `Executar`**, antes de criar diretório ou registro, para que um limite inválido não deixe um container pela metade no repositório.

O arquivo também define `ContabilidadeTrabalho`, o `record` que carrega o resultado da medição do grupo.

## 4. Camada de interop — a parte que fala com o Windows

Esta é a camada nova da entrega, e provavelmente onde o professor vai focar.

### `Interop/NativeMethods.cs`

Declarações **P/Invoke**: o mecanismo do .NET para chamar funções escritas em C dentro de DLLs nativas. O runtime faz o *marshalling*, ou seja, traduz os tipos do C# para o layout binário que a função em C espera.

```csharp
[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static extern bool AssignProcessToJobObject(SafeJobHandle trabalho, IntPtr processo);
```

Três detalhes que valem explicar:

- **`SetLastError = true`** — manda o runtime guardar o código de erro do Windows logo após a chamada, para que `Marshal.GetLastWin32Error()` possa lê-lo. Sem isso, o erro se perde.
- **`[return: MarshalAs(UnmanagedType.Bool)]`** — o `BOOL` do Win32 tem 4 bytes; o `bool` do C# tem 1. O atributo diz ao marshaller como converter.
- **`extern`** — não há corpo. A implementação está na DLL.

**As `struct`s** espelham byte a byte as estruturas do Win32. `[StructLayout(LayoutKind.Sequential)]` proíbe o compilador de reordenar os campos, o que ele faria por otimização. Se a ordem divergisse da definição em C, os dados chegariam embaralhados.

Um caso especial é a estrutura de CPU, que em C é uma **união** — o mesmo espaço de memória interpretado de formas diferentes:

```csharp
[StructLayout(LayoutKind.Explicit)]
internal struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
{
    [FieldOffset(0)] public uint ControlFlags;
    [FieldOffset(4)] public uint CpuRate;
    [FieldOffset(4)] public uint Weight;   // mesmo offset: é união
}
```

**`SafeJobHandle`** merece destaque. Um handle do Windows é um recurso que precisa ser liberado com `CloseHandle`. Guardá-lo num `IntPtr` cru arriscaria vazá-lo. Herdando de `SafeHandle`, o .NET garante a liberação mesmo se uma exceção interromper o fluxo, e o coletor de lixo não pode liberar o objeto enquanto a chamada nativa estiver em andamento.

O tipo `nuint` aparece nos campos de memória porque o `SIZE_T` do Windows tem o tamanho da palavra da máquina — 8 bytes em 64 bits, 4 em 32.

### `Interop/ObjetoTrabalho.cs`

A classe que embrulha o Job Object e esconde o P/Invoke do resto do sistema. Quatro operações:

**Construtor** — cria o job e aplica as restrições:

```csharp
handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
AplicarLimitesEstendidos(limites);
AplicarLimiteCpu(limites);
AplicarRestricoesUI();
```

Em `AplicarLimitesEstendidos`, cada limite acende um bit em `LimitFlags`. O `KILL_ON_JOB_CLOSE` é aceso **sempre**, independente do que o usuário pediu — é ele que garante que nenhum processo sobreviva ao container.

`AplicarRestricoesUI` é aplicado a todo container e é o isolamento documentado mais forte da API: combina as flags de `RestricoesUI` (atoms globais próprios, sem clipboard, sem handles de janela de fora, sem desktop, sem alterar configurações, sem desligar o Windows) e entrega ao kernel numa `JOBOBJECT_BASIC_UI_RESTRICTIONS`.

**`Atribuir(Process)`** — move o processo para dentro do grupo. Retorna `bool` em vez de lançar exceção quando o processo já terminou, pelo motivo explicado na [seção 7](#7-a-corrida-que-não-deu-para-fechar).

**`Contabilizar()`** — duas consultas: `ContabilidadeBasica` traz tempo de CPU, total de processos e falhas de página; `LimitesEstendidos` traz o pico de memória. O tempo vem em unidades de 100 ns, daí a divisão por `UnidadesPorMilissegundo = 10_000`.

**`Encerrar()`** — `TerminateJobObject` mata o grupo inteiro de uma vez. Ainda não é usado; entra na 2ª entrega, quando `stop` virar real.

## 5. Camada de persistência

### `Repositorios/IRepositorioConteiner.cs` e `RepositorioConteinerArquivo.cs`

A interface define quatro operações — `ObterTodos`, `ObterPorNome`, `Salvar`, `Remover` — e a implementação guarda tudo num único `containers.json`.

A estratégia é simples: **toda operação carrega o arquivo inteiro, altera a lista em memória e reescreve o arquivo inteiro**. Para dezenas de containers num trabalho didático, isso é adequado; não escalaria para milhares.

Um ponto que rende discussão de SO:

```csharp
private readonly object sincronizador = new();
lock (sincronizador) { ... }
```

O `lock` é **exclusão mútua dentro de um processo**. Ele protege contra duas threads do mesmo `minidocker`, mas **não** contra duas instâncias do `minidocker` rodando ao mesmo tempo — nesse caso as duas abririam o mesmo arquivo e uma sobrescreveria a outra. Resolver isso exigiria um mutex nomeado do sistema ou trava de arquivo. É uma limitação conhecida, e admitir isso demonstra que você entende a diferença entre concorrência intra e interprocesso.

**Por que existe a interface?** Para desacoplar. O `ServicoConteiner` aceita um `IRepositorioConteiner` no construtor, então dá para trocar JSON por banco de dados sem tocar na lógica de container.

## 6. Camada de serviço

### `Servicos/ServicoConteiner.cs`

O coração do projeto. `Executar` faz, em ordem:

```csharp
ValidarTexto(nome);  ValidarTexto(comando);     // 1. entrada válida?
limites.Validar();                              // 2. limites válidos?
if (repositorio.ObterPorNome(nome) is not null) // 3. nome já existe?
    throw ...;
Directory.CreateDirectory(caminhoRootFs);       // 4. cria o rootfs
repositorio.Salvar(conteiner);                  // 5. registra

try
{
    using var trabalho = new ObjetoTrabalho(limites); // 6. job ANTES do processo
    using var processo = CriarProcesso(...);          //    com ambiente isolado

    processo.Start();                               // 7. lança
    var noGrupo = trabalho.Atribuir(processo);      // 8. põe no grupo
    processo.WaitForExit();                         // 9. aguarda

    var contabilidade = trabalho.Contabilizar();    // 10. mede o grupo
    conteiner.CodigoSaida = processo.ExitCode;
}
finally
{
    conteiner.Estado = EstadoConteiner.Finalizado;
    repositorio.Salvar(conteiner);                  // 11. grava o resultado
}
```

O `finally` garante que o container nunca fica preso em `Executando`. Sem ele, uma exceção no meio da execução deixaria o registro nesse estado para sempre — e como `Remover` recusa apagar um container em execução, ele não sairia mais do `ps`. Uma falha aparece como `Finalizado` com `CodigoSaida` nulo.

A ordem dos passos **6, 7 e 8** é o ponto técnico da entrega. O job existe antes do processo, e a atribuição acontece imediatamente após o lançamento, porque é ela que faz os limites valerem para todos os descendentes.

O `using` do passo 6 importa: ao sair do método, o handle do job é fechado, e como `KILL_ON_JOB_CLOSE` está aceso, qualquer processo que ainda esteja vivo morre ali.

`CriarProcesso` monta o `ProcessStartInfo`:

```csharp
FileName = "cmd.exe",
WorkingDirectory = caminhoRootFs,
UseShellExecute = false
```

`UseShellExecute = false` cria o processo diretamente pelo SO em vez de pedir ao Windows Explorer, que é o necessário para controlar o processo e para que a atribuição ao job funcione.

`IsolarAmbiente` limpa o ambiente herdado (`Environment.Clear()`) e passa só `SystemRoot`, `ComSpec` e `Path`, com `TEMP`/`TMP` apontando para o rootfs. Sem isso, toda variável do host — inclusive credenciais — entraria no container.

`Parar` ainda lança exceção: com execução síncrona, não há processo vivo quando o comando retorna. `Remover` recusa apagar um container em execução e, fora isso, remove o registro e o diretório.

## 7. A corrida que não deu para fechar

Entre `processo.Start()` e `trabalho.Atribuir(processo)` existe uma janela em que o processo já roda **fora** do job.

O jeito correto de eliminá-la é criar o processo suspenso (`CREATE_SUSPENDED`), atribuí-lo e só então liberá-lo — é o que o Docker faz. A classe `Process` do .NET não expõe essa flag, e fechar a janela exigiria chamar `CreateProcess` diretamente por P/Invoke.

Na prática a janela é de microssegundos contra os ~10 ms de inicialização do `cmd.exe`. O código trata o caso sem quebrar: se a atribuição falhar **e** o processo já tiver terminado, `Atribuir` devolve `false` e o serviço registra contabilidade zerada — afinal, um processo que já morreu não tem mais o que ser limitado.

## 8. CLI

### `Cli/Program.cs`

Usa *top-level statements*, então não há `class Program` nem `Main` explícito. Um `switch` sobre `args[0]` despacha os comandos.

No `run`, `args[1]` é o nome e **tudo dali em diante é o comando**, unido por espaços:

```text
minidocker run c1 echo ola
               └┘ └──────┘
              nome comando
```

A CLI ainda não aceita flags de limite — `Executar` é chamado sem `LimitesRecursos`, então vale `LimitesRecursos.Nenhum`. As flags `--memoria`, `--processos` e `--cpu` entram na 2ª entrega.

Toda exceção vinda do núcleo é capturada num único `catch`, impressa em `stderr`, e o programa sai com código 1.

## 9. Testes

`tests/MiniDocker.Testes/` tem 15 casos de teste: **8 rodam em qualquer sistema** e **7 exigem Windows**.

O atributo `FatoWindowsAttribute` herda de `FactAttribute` e preenche `Skip` quando não está no Windows:

```csharp
if (!OperatingSystem.IsWindows())
    Skip = "Requer Windows: o container roda via cmd.exe dentro de um Job Object.";
```

Assim o teste é **ignorado** em vez de **falhar** fora do Windows. A diferença é de honestidade: um teste pulado deixa explícito que aquilo não foi verificado naquele ambiente, enquanto uma suíte vermelha esconde os problemas reais no meio do ruído.

Os 8 independentes de plataforma cobrem validação — nome vazio, limites inválidos, `stop` de container inexistente e o caso de um limite inválido não deixar container pela metade. Os 7 do Windows cobrem execução, contabilidade do grupo, ambiente isolado e ciclo de vida.

O desenvolvimento foi feito em Linux, onde os 7 testes de Windows são pulados; eles foram executados numa máquina Windows e passaram, o que valida o P/Invoke de fato.

## 10. Decisões que podem ser questionadas

**"Por que `cmd.exe` e não executar o binário direto?"**
Para que o comando aceite sintaxe de shell — pipes, redirecionamento, `&`. O custo é um processo intermediário, que aparece na contagem do grupo.

**"Por que o rootfs é só `WorkingDirectory`?"**
Porque o Windows não tem `chroot`. Sem Server Silos, não há como trocar a raiz do sistema de arquivos de um processo. O diretório serve como ponto de partida e como lugar para os dados do container.

**"Por que `record` para os limites e `class` para o container?"**
`LimitesRecursos` é um valor imutável, definido na criação e nunca alterado — `record` dá igualdade estrutural de graça. `Conteiner` muda de estado ao longo do ciclo de vida, então é uma `class` mutável.

**"Por que a contabilidade é lida depois do `WaitForExit`?"**
Porque ela sobrevive ao término dos processos. Ler antes seria uma corrida; ler depois é determinístico. É justamente a vantagem do Job Object sobre `Process.WorkingSet64`.

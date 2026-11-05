# MiniDocker

Gerenciador de containers didático em C#/.NET 10 para Windows, feito para a disciplina de Sistemas Operacionais.

Cada container é um **grupo de processos** criado dentro de um [Job Object](https://learn.microsoft.com/windows/win32/procthread/job-objects) do Windows, com restrições impostas pelo kernel, ambiente próprio, sistema de arquivos copiado de uma **imagem** e ciclo de vida completo: criar, iniciar, parar, reiniciar, executar comandos dentro, ver logs e remover.

> **O isolamento é parcial — e não é simulado.** As restrições implementadas são aplicadas pelo kernel do Windows: quando o container tenta usar um recurso bloqueado, quem nega é o sistema operacional. O que **não** está isolado, e o motivo técnico de cada caso, está em [Isolamento](#isolamento). Os conceitos estão em [docs/ETAPA-1.md](docs/ETAPA-1.md) e [docs/ETAPA-2.md](docs/ETAPA-2.md), e o passeio pelo código em [docs/CODIGO.md](docs/CODIGO.md).

## Índice

- [Requisitos](#requisitos)
- [Início rápido](#início-rápido)
- [A loja por dentro: como o exemplo web funciona](#a-loja-por-dentro-como-o-exemplo-web-funciona)
- [Comandos](#comandos)
- [Imagens e o MiniDockerfile](#imagens-e-o-minidockerfile)
- [Como funciona](#como-funciona)
- [Isolamento](#isolamento)
- [Estrutura do projeto](#estrutura-do-projeto)
- [Entregas](#entregas)

## Requisitos

- Windows — os containers rodam via `cmd.exe /c` dentro de um Job Object
- .NET 10 SDK

O código compila em qualquer sistema operacional, mas só executa containers no Windows. Fora do Windows, os testes que dependem do `cmd.exe` e da API de Job Objects são **ignorados** em vez de falharem; os de imagem, build sem `RUN`, criação e validação rodam em qualquer sistema.

## Início rápido

```bat
dotnet build
dotnet test

:: um alias para encurtar os exemplos
doskey minidocker=dotnet run --project src\MiniDocker.Cli -- $*

:: container descartavel, anexado ao terminal
minidocker run --rm scratch echo ola
```

### Exemplos: um job e um site

Para apresentar, siga o roteiro em [exemplos/README.md](exemplos/README.md): cada passo traz o comando, a saída esperada e o que explicar.

Há dois exemplos, um de cada tipo de container:

| Imagem | Tipo | O que faz |
|---|---|---|
| [`exemplos/relatorio`](exemplos/relatorio) | **Roda e termina** | Lê um CSV de vendas, soma por região, grava o relatório e sai com `0` (ou `2`, se os dados forem inválidos). Como um job agendado, um ETL, um passo de CI |
| [`exemplos/web`](exemplos/web) | **Fica no ar** | Site com contador de visitas, `/saude` e `/info`, que fica rodando em segundo plano até alguém dar `stop`. Como um servidor web, uma API |

```bat
minidocker build -t relatorio exemplos\relatorio
minidocker run --name relatorio-outubro relatorio  :: imprime o relatorio e termina
minidocker ps -a                                   :: relatorio-outubro: Finalizado (0)
```

O site guarda as visitas **num arquivo dentro do próprio container**. É isso que mostra que o `start` volta o mesmo container:

```bat
minidocker build -t web exemplos\web               :: 1. a imagem: o molde
minidocker run -d --name loja web                  :: 2. o container nasce da imagem e fica no ar
                                                   ::    abra http://localhost:8080 e recarregue
minidocker stop loja                               :: 3. o site cai...
minidocker ps -a                                   ::    ...mas a loja continua existindo: "Parado (137)"

minidocker start loja                              :: 4. o MESMO container volta, sem novo run
                                                   ::    as visitas continuam de onde pararam

minidocker run -d --name filial -e PORTA=8081 web  :: 5. outro container da mesma imagem
                                                   ::    http://localhost:8081 comeca do zero

minidocker rm -f loja filial                       :: 6. agora sim deixam de existir
```

Os três conceitos que a demonstração separa:

| | O que é | Analogia |
|---|---|---|
| **Imagem** | Molde só-leitura: arquivos + comando padrão | A receita do bolo |
| **Container** | Uma cópia da imagem com estado próprio (seus arquivos, seu log) | Um bolo feito da receita |
| **Processo** | O programa rodando dentro do container | O bolo no forno |

E o que cada comando faz com eles:

| Comando | Processos | Arquivos do container | Container |
|---|---|---|---|
| `stop` | Morrem | Ficam | Continua existindo, `Parado` |
| `start` | Nascem de novo, do zero | Os mesmos de antes | O mesmo |
| `rm` | — | Apagados | Deixa de existir |
| `run` | Nascem | Cópia nova da imagem | Um container **novo** |

## A loja por dentro: como o exemplo web funciona

O exemplo [`exemplos/web`](exemplos/web) é um site de verdade rodando dentro de um container: você abre `http://localhost:8080` no navegador, e quem responde é um PowerShell preso no Job Object do container.

### Os arquivos

```text
exemplos\web\
├── MiniDockerfile     a receita da imagem
├── servidor.bat       o ponto de entrada: o CMD da imagem chama este arquivo
├── servidor.ps1       o servidor web (uns 70 linhas)
└── site\
    └── index.html     o modelo da página, com marcadores {{NOME}}, {{VISITAS}}...
```

### A receita, linha a linha

| Linha do `MiniDockerfile` | O que faz |
|---|---|
| `FROM scratch` | Começa de uma pasta vazia |
| `COPY servidor.ps1 .` e `COPY servidor.bat .` | Põe o código do servidor na raiz da imagem |
| `COPY site site` | Copia a pasta `site` inteira (a página) |
| `ENV PORTA=8080` | Porta padrão. O `-e PORTA=8081` do `run` sobrescreve |
| `ENV NOME_SITE=Loja do Grupo` | Nome padrão. O `-e "NOME_SITE=Filial Centro"` sobrescreve |
| `RUN echo 1.0.0 (build ...) > VERSAO.txt` | Roda **no build**, num container temporário. O `VERSAO.txt` gerado vira parte da imagem, e o site mostra a versão |
| `CMD servidor.bat` | O que roda quando alguém der `run` sem informar comando |

### O que nasce no `run -d --name loja web`

```text
MiniDocker.Cli.exe __shim loja          o shim: dono do container, segura o handle do job
└── Job Object "Local\MiniDocker-<id>"  limites (-m, --cpus) e restrições de interface
    └── cmd.exe /c servidor.bat         pasta atual = %USERPROFILE%\.minidocker\containers\loja
        ├── conhost.exe                 o console que o Windows cria para o cmd
        └── powershell.exe -File servidor.ps1     ← o site
```

1. O `create` copia a imagem para `containers\loja` e grava o ambiente: o mínimo do sistema mais `PORTA` e `NOME_SITE`.
2. O shim cria o Job Object e lança `cmd.exe /c servidor.bat`, com a pasta do container como diretório de trabalho.
3. O `servidor.bat` chama o PowerShell, que nasce **dentro do mesmo job**, porque filhos herdam o grupo.
4. O `servidor.ps1` abre a porta e fica esperando requisições. O shim lê a saída dele e grava no log.

### Por que existe um `.bat` no meio?

```bat
@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File servidor.ps1
exit /b %ERRORLEVEL%
```

| Parte | Por quê |
|---|---|
| Um `.bat` como `CMD` | O MiniDocker executa todo comando com `cmd.exe /c`. Um script de entrada deixa o `CMD` curto e faz o `ps` mostrar `"servidor.bat"` em vez de uma linha gigante. É o papel do `docker-entrypoint.sh` em imagens reais |
| Caminho completo do PowerShell | O container recebe um ambiente **mínimo**: o `Path` só tem `System32` e a pasta do Windows. O `powershell.exe` fica em `System32\WindowsPowerShell\v1.0`, fora do `Path`. O `%SystemRoot%` funciona porque está no ambiente mínimo |
| `-NoProfile` | Não carrega o perfil do usuário do host. O container não deve depender da configuração de quem o roda |
| `-ExecutionPolicy Bypass` | O Windows bloqueia scripts `.ps1` por padrão. A liberação vale **só para este processo**, sem mudar nada no host |
| `exit /b %ERRORLEVEL%` | Repassa o código de saída do PowerShell para o `cmd.exe`, e dele para o MiniDocker. É por isso que um site que não conseguiu abrir a porta aparece como `Finalizado (1)` no `ps` |

### O `servidor.ps1`, passo a passo

| Trecho | O que faz |
|---|---|
| `$env:PORTA`, `$env:NOME_SITE` | Lê a configuração das **variáveis de ambiente**. A mesma imagem vira sites diferentes só trocando o `-e` |
| `Get-Content VERSAO.txt` | Lê o arquivo que o `RUN` gerou no build |
| `dados\visitas.txt` | Carrega a contagem de visitas. O caminho é relativo à pasta atual, que é a pasta do container, então cada container tem a própria contagem |
| `HttpListener` em `http://localhost:PORTA/` | Abre o servidor HTTP. Se a porta estiver ocupada, registra o erro e sai com `exit 1` |
| `while ($true) { $servidor.GetContext() ... }` | O laço principal. O `GetContext()` **bloqueia** até chegar uma requisição; enquanto isso o processo dorme e não gasta CPU (o `stats` mostra 0%) |
| `switch ($caminho)` | As rotas: `/` (página + 1 visita, gravada no arquivo na hora), `/saude` (`ok`), `/info` (JSON) e o resto (404) |
| `site\index.html` | O modelo da página; os marcadores `{{NOME}}`, `{{VISITAS}}`, `{{VERSAO}}`, `{{PID}}` e `{{PORTA}}` são trocados a cada acesso |
| `Log` → `[Console]::WriteLine` | Escreve na saída padrão, que é um **pipe** lido pelo shim e gravado em `logs\<id>.log`. É o que o `minidocker logs loja` mostra |

### Uma requisição, do navegador ao log

```text
navegador ──TCP localhost:8080──► http.sys (driver HTTP do kernel do Windows)
                                      │ entrega ao processo que registrou a URL
                                      ▼
                         powershell.exe (dentro do job)
                            GetContext() acorda → monta a resposta → Log "GET / 200"
                                      │ stdout (pipe)
                                      ▼
                         shim → %USERPROFILE%\.minidocker\logs\<id>.log → minidocker logs loja
```

O `HttpListener` não abre a porta sozinho: ele registra a URL no **http.sys**, o driver HTTP que roda no kernel. Quem escuta a porta é o driver, que repassa cada requisição ao processo dono da URL. Por isso dois containers na mesma porta conflitam (o driver recusa o segundo registro), e por isso, quando o `stop` mata o PowerShell, o registro some e o navegador recebe "conexão recusada".

### Onde ficam os dados

```text
%USERPROFILE%\.minidocker\containers\loja\
├── servidor.bat, servidor.ps1, site\    ← copiados da imagem no create
├── VERSAO.txt                           ← veio da imagem (gerado pelo RUN)
└── dados\visitas.txt                    ← criado pelo site; só deste container
```

| Comando | `visitas.txt` |
|---|---|
| `stop` + `start` | Continua lá, e a contagem segue de onde parou |
| `run` de outro container (`filial`) | O outro container tem o próprio arquivo, começando do zero |
| Rebuild da imagem | Não muda nada: a loja tem a própria cópia. Só containers **novos** pegam a imagem nova |
| `rm` | Apagado junto com a pasta do container |

### Conceitos de SO que o exemplo mostra

| No exemplo | Conceito |
|---|---|
| `cmd` → `powershell` nascem dentro do job | Árvore de processos; filhos herdam o grupo |
| `GetContext()` esperando requisição com 0% de CPU | E/S bloqueante: o processo fica no estado **bloqueado**, fora da fila do escalonador, até o kernel acordá-lo |
| `PORTA` e `NOME_SITE` vindos do `-e` | Bloco de ambiente passado na criação do processo |
| Saída do site aparecendo no `logs` | Pipes entre processos (stdout redirecionado) |
| `Finalizado (1)` quando a porta está ocupada | Código de saída do processo, repassado do PowerShell ao `cmd` e ao MiniDocker |
| Visitas preservadas no `stop`/`start` | Estado em arquivo sobrevive ao processo; estado em memória, não |
| Duas lojas na mesma porta conflitam | Rede **não isolada**: os containers dividem as portas com o host |
| `stop` derruba o site na hora | `TerminateJobObject` mata o grupo; o kernel libera os recursos do processo, inclusive o registro no http.sys |
| `-m 20m` impede o PowerShell de subir | Limite de memória imposto pelo kernel |

### Variações para testar

```bat
:: outra loja, outra porta, outro nome
minidocker run -d --name filial -e PORTA=8081 -e "NOME_SITE=Filial Centro" web

:: muda a pagina e reconstroi: so containers NOVOS pegam a versao nova
notepad exemplos\web\site\index.html
minidocker build -t web exemplos\web
minidocker run -d --name nova -e PORTA=8082 web
```

O site só atende em `localhost`, ou seja, só a própria máquina acessa.

## Comandos

A sintaxe segue a do Docker. Containers e imagens podem ser referidos pelo nome ou por um prefixo do id.

### Containers

| Comando | O que faz |
|---|---|
| `run [opções] IMAGEM [COMANDO]` | Cria e executa. Sem `COMANDO`, usa o `CMD` da imagem |
| `create [opções] IMAGEM [COMANDO]` | Cria sem executar (estado `Criado`) |
| `start [-a] CONTAINER...` | Inicia em segundo plano; `-a` anexa ao terminal |
| `stop CONTAINER...` | Encerra o grupo inteiro (`TerminateJobObject`); código de saída 137 |
| `restart CONTAINER...` | `stop` + `start` |
| `rm [-f] CONTAINER...` | Remove registro, rootfs e log; `-f` para antes se estiver rodando |
| `ps [-a] [-q]` | Lista os em execução; `-a` inclui os parados |
| `logs [-f] CONTAINER` | Saída do container; `-f` acompanha ao vivo |
| `exec CONTAINER COMANDO` | Executa mais um processo **dentro do mesmo Job Object** |
| `stats [CONTAINER...]` | CPU %, pico de memória e processos, lidos ao vivo do Job Object |
| `inspect NOME...` | Registro completo em JSON (container ou imagem) |

Opções de `run`/`create`:

| Opção | Efeito | Mecanismo |
|---|---|---|
| `-d` | Segundo plano; imprime o id | Processo shim (ver [Como funciona](#como-funciona)) |
| `-it` | Interativo, ligado ao terminal (sem log) | Processo herda o console |
| `--name NOME` | Nome do container; sem ele, um nome é gerado | |
| `-e CHAVE=valor` | Variável de ambiente; `-e CHAVE` copia a do host | Sobrescreve o `ENV` da imagem |
| `-m 256m` | Limite de memória | `JOB_OBJECT_LIMIT_PROCESS_MEMORY` / `JOB_MEMORY` |
| `--cpus 0.5` | Limite de CPU, em núcleos | `JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP` |
| `--pids-limit 10` | Máximo de processos vivos | `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` |
| `--rm` | Remove o container ao terminar | |

### Imagens

| Comando | O que faz |
|---|---|
| `build -t NOME[:TAG] [-f ARQUIVO] [CONTEXTO]` | Constrói a partir do `MiniDockerfile` do contexto |
| `images [-q]` | Lista as imagens |
| `rmi IMAGEM...` | Remove imagens |
| `commit CONTAINER NOME[:TAG]` | Congela o rootfs atual de um container numa imagem |
| `save -o ARQUIVO.zip IMAGEM` | Exporta para um zip, para levar a outra máquina |
| `load -i ARQUIVO.zip` | Importa |

A imagem `scratch` é embutida e vazia.

## Imagens e o MiniDockerfile

Uma imagem é um **rootfs base** mais os padrões de execução (`CMD` e `ENV`). Criar um container **copia** o rootfs da imagem para o container: cada container altera a própria cópia sem afetar a imagem nem os vizinhos.

O `MiniDockerfile` é um subconjunto do Dockerfile:

```dockerfile
FROM scratch                       # ou outra imagem local
COPY servidor.ps1 .                # do contexto de build para o rootfs
ENV PORTA=8080                     # herdada pelos containers
RUN echo 1.0.0 > VERSAO.txt        # roda num container de build isolado
CMD servidor.bat                   # comando padrão do run
```

O `RUN` não roda no host: roda num **container temporário**, com o mesmo Job Object, as mesmas restrições e o mesmo ambiente isolado, tendo o rootfs em construção como diretório de trabalho. O que ele gravar ali vira parte da imagem. `COPY` não aceita caminhos que saiam do contexto (`..\`).

O Docker evita a cópia usando **camadas** e um sistema de arquivos de união (overlayfs no Linux, wcifs no Windows): a imagem é só-leitura e cada container ganha uma camada gravável por cima. No MiniDocker as camadas ficam "achatadas" — cada `FROM` e cada container é uma cópia completa. O custo é disco e tempo de criação; o comportamento observável (o container não altera a imagem) é o mesmo.

## Como funciona

```text
minidocker run -d web
    │
    ├─ create: copia o rootfs da imagem, grava o registro (Criado)
    │
    └─ lança o shim em segundo plano e sai ─────────┐
                                                    ▼
                          minidocker __shim <nome>   (dono do container)
                              ├─ 1. cria o Job Object com nome    ← trava: um start por vez
                              ├─ 2. aplica restrições e limites   ← kernel passa a impor
                              ├─ 3. marca Executando
                              ├─ 4. inicia o cmd.exe no rootfs, com ambiente mínimo
                              ├─ 5. atribui o processo ao job     ← descendentes herdam
                              ├─ 6. grava stdout/stderr no log
                              ├─ 7. processo principal termina    ← encerra os netos
                              ├─ 8. lê a contabilidade do grupo
                              └─ 9. marca Finalizado (ou Parado, se foi um stop)
```

O **shim** cumpre o papel do `containerd-shim` do Docker: é ele, e não o CLI, que segura o Job Object, então o terminal pode ser fechado sem matar o container. No modo anexado (sem `-d`), o próprio CLI é o dono, e Ctrl+C equivale a `stop`.

O Job Object recebe um **nome** no namespace da sessão (`Local\MiniDocker-<id>`). É assim que outros processos encontram o container:

| Comando | O que faz com o nome |
|---|---|
| `stop` | `OpenJobObject` + `TerminateJobObject` — mata o grupo inteiro de uma vez |
| `exec` | `OpenJobObject` + `AssignProcessToJobObject` — o processo novo herda limites e restrições |
| `stats` | `OpenJobObject` + `QueryInformationJobObject` — contabilidade ao vivo |
| `ps` | Se o registro diz `Executando` mas o nome não existe mais no kernel, o dono morreu: o registro é corrigido |

### Estados

```text
           start                  processo termina sozinho
 Criado ─────────► Executando ────────────────────────────► Finalizado
                     │   ▲                                      │
                stop │   │ start / restart                      │
                     ▼   │                                      │
                   Parado ◄─────────────────────────────────────┘ (start também vale daqui)
```

Qualquer estado aceita `rm`, menos `Executando`, que exige `rm -f`.

### Dados persistidos

Em `%USERPROFILE%\.minidocker\`:

| Caminho | Conteúdo |
|---|---|
| `containers.json` | Registro de cada container — um PCB simplificado |
| `containers\<nome>\` | Rootfs de cada container |
| `logs\<id>.log` | Saída de cada container; acumula entre restarts |
| `images.json` | Registro das imagens |
| `images\<id>\rootfs\` | Rootfs de cada imagem |

O CLI e os shims gravam no mesmo JSON ao mesmo tempo. A exclusão mútua entre processos é feita com um arquivo de trava aberto com `FileShare.None`, e cada gravação lê o registro mais recente antes de alterar.

## Isolamento

### Imposto pelo kernel

| Restrição | Efeito |
|---|---|
| `UILIMIT_GLOBALATOMS` | O grupo recebe uma **tabela de atoms global própria** — um namespace privado |
| `UILIMIT_HANDLES` | Não alcança handles de janela de processos de fora do grupo |
| `UILIMIT_READCLIPBOARD` / `WRITECLIPBOARD` | Não lê nem escreve na área de transferência do host |
| `UILIMIT_DESKTOP` | Não cria nem troca de desktop |
| `UILIMIT_SYSTEMPARAMETERS` / `DISPLAYSETTINGS` | Não altera configurações do sistema |
| `UILIMIT_EXITWINDOWS` | Não desliga nem reinicia o Windows |
| Limites de memória, CPU e processos | Ultrapassar faz a alocação ou a criação de processo falhar |
| Ambiente próprio | Não herda nenhuma variável do host; só entra o que a imagem ou o `-e` definem |
| `LIMIT_KILL_ON_JOB_CLOSE` | Nenhum processo sobrevive ao fim do container |

### Não implementado

| Recurso | O que falta |
|---|---|
| Sistema de arquivos | O rootfs é só o diretório de trabalho: o container ainda lê e escreve fora dele. Confinar a escrita exige um token restrito (`CreateRestrictedToken` ou nível de integridade baixo + `CreateProcessAsUser`). |
| Privilégios | Mesmo mecanismo. |
| Imagem com userland própria | O container roda o `cmd.exe` do host. A imagem empacota os **arquivos da aplicação**, não um Windows inteiro. |

### Fora de alcance

| Recurso | Por quê |
|---|---|
| Namespace de processos | O container enxerga o host via `tasklist`. Filtrar isso exige **Server Silos**, APIs não documentadas usadas pelo Host Compute System — seria embrulhar o runtime da Microsoft em vez de construir o próprio. |
| Rede | Mesma pilha e mesmas portas do host, pelo mesmo motivo. |

No Linux essa divisão é mais clara: **isolamento** vem de namespaces e **controle de recursos** vem de cgroups. O Job Object do Windows corresponde aos cgroups e cobre só uma parte dos namespaces — a do subsistema de janelas.

### Como verificar

```bat
:: o ambiente do host nao entra no container
:: (o set roda dentro do container; um echo %VAR% seria expandido pelo cmd do host antes)
set SEGREDO_DO_HOST=senha-123
minidocker run --rm scratch set
:: SEGREDO_DO_HOST nao aparece
minidocker run --rm -e SEGREDO_DO_HOST scratch set
:: agora aparece: entrou porque foi pedido explicitamente

:: o stop mata o grupo inteiro, netos inclusive
minidocker run -d --name orfao scratch "start /b ping -t 127.0.0.1 & ping -t 127.0.0.1"
minidocker stats orfao
minidocker stop orfao

:: o exec entra no mesmo grupo e respeita o limite de processos
minidocker run -d --name lim --pids-limit 2 scratch ping -t 127.0.0.1
minidocker exec lim ping -n 1 127.0.0.1
:: falha: o grupo ja tem cmd.exe + ping, o terceiro processo e recusado pelo kernel

:: o que ainda NAO esta isolado, honestamente
minidocker run --rm scratch "cd & type C:\Windows\System32\drivers\etc\hosts & tasklist & whoami"
```

## Estrutura do projeto

```text
src/MiniDocker.Nucleo/
  Modelos/        Conteiner, Imagem, EstadoConteiner, LimitesRecursos, OpcoesConteiner
  Interop/        P/Invoke da API de Job Objects do Windows
  Repositorios/   Persistência em JSON com trava entre processos
  Servicos/       Ciclo de vida do container, imagens, build, execução
src/MiniDocker.Cli/      Interface de linha de comando e shim
tests/MiniDocker.Testes/ Testes unitários
exemplos/                Contextos de build de exemplo
docs/ETAPA-1.md          Conceitos: Job Objects, isolamento, contabilidade
docs/ETAPA-2.md          Conceitos: segundo plano, job com nome, imagens
docs/ETAPA-3.md          Plano da etapa final
exemplos/README.md       Roteiro de demonstração passo a passo
docs/CODIGO.md           Passeio pelo código, arquivo por arquivo
```

## Entregas

| Entrega | Situação | O que contém |
|---|---|---|
| **1ª — isolamento e ciclo de vida** | Implementada | O container vira um grupo de processos num Job Object, com restrições de interface impostas pelo kernel, tabela de atoms própria, ambiente isolado do host e encerramento sem deixar órfãos. |
| **2ª — execução em segundo plano** | Implementada | Shim dono do container; Job Object com nome; `start`, `stop` real via `TerminateJobObject`, `restart`, `exec`, `logs`, `stats`; limites de memória, CPU e processos na CLI. |
| **3ª — imagens** | Implementada | `build` com MiniDockerfile (`RUN` em container isolado), `images`, `rmi`, `commit`, `save`/`load`; rootfs copiado da imagem para cada container. |
| **Final — controle fino e confinamento** | Planejada | Volumes, política de restart, eventos do kernel, criação suspensa e confinamento do sistema de arquivos — detalhes em [docs/ETAPA-3.md](docs/ETAPA-3.md). |

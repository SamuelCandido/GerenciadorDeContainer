# Etapa 2 — Segundo plano, ciclo de vida completo e imagens

Explicação dos conceitos de Sistemas Operacionais aplicados nesta entrega. A base — Job Objects, restrições, contabilidade — está em [ETAPA-1.md](ETAPA-1.md).

## 1. O problema: um container que sobrevive ao comando

Na Etapa 1 o `run` era síncrono: o CLI criava o Job Object, esperava o processo e saía. Como o job tem `KILL_ON_JOB_CLOSE`, o container **não podia viver mais que o CLI**. Não havia o que parar nem reiniciar.

Para ter `stop`, `start`, `logs` e `exec`, duas perguntas precisam de resposta:

1. **Quem segura o Job Object** enquanto o container roda, se o CLI já saiu?
2. **Como outro processo encontra o container** para pará-lo ou entrar nele?

## 2. O shim: um processo dono do container

O Docker resolve a primeira pergunta com o `containerd-shim`: um processo pequeno por container, que fica em segundo plano segurando o processo do container e a saída dele. O MiniDocker faz o mesmo: `run -d` e `start` lançam o próprio executável com o comando interno `__shim <nome>`, e saem.

```text
 terminal                         segundo plano
 ────────                         ─────────────
 minidocker run -d app
   ├─ cria o registro
   ├─ lança ──────────────────►  minidocker __shim c1
   ├─ espera o "Executando"         ├─ cria o Job Object
   └─ imprime o id e sai            ├─ cmd.exe ... (grupo)
                                    ├─ grava o log
 minidocker stop c1                 │
   └─ abre o job pelo nome ──────►  ├─ TerminateJobObject → o grupo morre
                                    └─ grava "Parado (137)" e sai
```

O shim é criado com `CreateNoWindow` e com os pipes padrão redirecionados e fechados: ele não depende do console nem dos handles do terminal, então fechar o terminal não leva o container junto.

No modo anexado (`run` sem `-d`, `start -a`) não há shim: o próprio CLI é o dono. O mecanismo é o mesmo — só muda qual processo segura o job.

## 3. Job Object com nome: um objeto do kernel compartilhado

A segunda pergunta é respondida pelo **namespace de objetos do kernel**. Todo Job Object pode ter um nome; com nome, qualquer processo da mesma sessão (e com permissão) consegue abri-lo com `OpenJobObject`. O MiniDocker usa `Local\MiniDocker-<id>`.

O nome dá três coisas de graça:

| Propriedade | Uso no MiniDocker |
|---|---|
| **Compartilhamento** — outros processos abrem o mesmo objeto | `stop` termina o grupo; `exec` coloca um processo novo nele; `stats` lê a contabilidade ao vivo |
| **Exclusividade** — o kernel não cria dois objetos com o mesmo nome | `CreateJobObject` devolve `ERROR_ALREADY_EXISTS` se o container já roda: dois `start` simultâneos não sobem dois grupos |
| **Tempo de vida ligado aos handles** — o nome some quando o último handle fecha | Se o registro diz `Executando` mas o nome não existe, o dono morreu (foi morto, a máquina reiniciou). O `ps` corrige o registro sozinho |

A terceira propriedade é a que torna o estado confiável. Um PID não serve para isso: PIDs são **reutilizados** pelo Windows, então "existe um processo com o PID 4321" não prova que é o nosso. O nome do job só existe enquanto o dono estiver vivo.

### Por que a ordem importa

O shim cria o Job Object **antes** de gravar `Executando`, e grava o estado final **antes** de fechar o job. Assim, quem lê `Executando` sempre encontra o job no kernel — e quem não encontra sabe que o dono morreu, sem confundir com "ainda não começou" ou "acabou de terminar".

## 4. Parar: sinal ou terminação?

No Linux, `docker stop` envia `SIGTERM`, espera alguns segundos e então envia `SIGKILL`. O Windows não tem sinais para processos de console sem janela; o equivalente mais próximo, `GenerateConsoleCtrlEvent`, só alcança processos do mesmo console.

O MiniDocker usa `TerminateJobObject`, que é o `SIGKILL` do grupo: **todos os processos do job morrem de uma vez**, netos inclusive, sem chance de escapar. O código de saída é 137 — o mesmo que o Docker mostra para um container morto por `SIGKILL` (128 + 9).

Para distinguir "parado" de "terminou sozinho", o `stop` marca `ParadaSolicitada` no registro **antes** de terminar o grupo. Quando o shim acorda com o processo morto, lê a marca e grava `Parado`.

## 5. Exclusão mútua entre processos

Agora há vários processos gravando o mesmo `containers.json`: o CLI, cada shim, cada `ps` que corrige um registro. Um `lock` do C# só protege threads do **mesmo** processo.

A solução é uma seção crítica que o SO conheça: um arquivo de trava aberto com `FileShare.None`. Enquanto um processo o mantém aberto, o kernel recusa a abertura pelos demais, que esperam e tentam de novo. Dentro da trava, cada gravação **lê o registro mais recente, altera e grava** (`Atualizar`), para não sobrescrever o que outro processo acabou de escrever. É o padrão ler-modificar-escrever atômico, o mesmo problema de condição de corrida dos livros de SO, só que entre processos.

## 6. Logs

O processo principal roda com stdout e stderr redirecionados para pipes. O dono lê os dois pipes (em threads separadas, por isso há uma trava para não misturar linhas) e grava em `logs\<id>.log`. O `logs -f` é um `tail -f`: lê o arquivo, e enquanto o container estiver vivo, espera e lê de novo.

Um detalhe de SO aparece aqui: pipes são **herdados** pelos filhos. Se o comando faz `start /b ping`, o `ping` herda a ponta de escrita do pipe e o mantém aberto mesmo depois que o `cmd.exe` morre — quem lê nunca recebe fim de arquivo. Por isso o dono espera **só o processo principal**, e em seguida encerra o resto do grupo, o que fecha todas as pontas do pipe. É também a semântica do Docker: o container acaba quando o processo principal acaba.

## 7. Imagens

### O que é uma imagem

Uma imagem é um **sistema de arquivos base** mais os padrões de execução (`CMD`, `ENV`). No Docker, ela empacota o *userland* de um sistema (`ubuntu`, `nanoserver`); o kernel é sempre o do host.

No MiniDocker, a imagem empacota os **arquivos da aplicação** — scripts, dados, configuração. O `cmd.exe` continua vindo do host, porque rodar um userland Windows separado exigiria o Host Compute System.

### Cópia em vez de camadas

Criar um container copia o rootfs da imagem para o diretório do container. O efeito é o que se espera de um container: ele altera a própria cópia, e a imagem e os outros containers não veem nada.

O Docker obtém o mesmo efeito sem copiar, com **copy-on-write**: a imagem é uma pilha de camadas só-leitura, e um sistema de arquivos de união (overlayfs) mostra ao container a soma delas com uma camada gravável em cima. Um arquivo só é copiado para a camada do container quando ele o modifica. É a mesma ideia do `fork()` com páginas copy-on-write na memória virtual, aplicada a arquivos.

| | Docker | MiniDocker |
|---|---|---|
| Armazenamento da imagem | Camadas só-leitura, compartilhadas | Um diretório por imagem |
| Criar um container | Instantâneo: só cria a camada gravável | Copia o rootfs inteiro |
| Container altera um arquivo | Copy-on-write para a camada do container | Altera a cópia dele |
| Isolamento entre containers | Igual | Igual |

### O build

O `MiniDockerfile` é lido linha a linha. `FROM` copia o rootfs e os padrões da imagem base; `COPY` traz arquivos do contexto; `ENV` e `CMD` mudam os padrões.

O `RUN` é o ponto mais interessante: ele **não roda no host**. Roda num container temporário — Job Object próprio, restrições de interface, ambiente isolado — cujo diretório de trabalho é o rootfs em construção. É o mesmo princípio do Docker: o build é uma sequência de containers, e a imagem é o que eles deixaram no sistema de arquivos.

`commit` faz o caminho inverso: transforma o rootfs atual de um container numa imagem. `save` e `load` empacotam uma imagem num zip para levá-la a outra máquina; a extração recusa entradas que tentem sair do diretório de destino (*zip slip*).

## 8. Limites pela CLI

Os limites que já existiam no núcleo agora têm flags no formato do Docker:

| Flag | Job Object | Observação |
|---|---|---|
| `-m 256m` | `JOB_OBJECT_LIMIT_PROCESS_MEMORY` e `JOB_MEMORY` | A alocação que ultrapassa o teto falha |
| `--cpus 0.5` | `CPU_RATE_CONTROL_HARD_CAP` | O Docker mede em núcleos; o Job Object, em % da máquina inteira. `0.5` numa máquina de 8 núcleos vira 6,25%, arredondado para 7% |
| `--pids-limit 10` | `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` | Criar o 11º processo falha — inclusive um `exec` |

O `stats` mostra o efeito ao vivo: o percentual de CPU sai de duas leituras do tempo de CPU acumulado do job, separadas por meio segundo.

## 9. Perguntas prováveis

**"Por que um processo shim, e não uma thread ou um serviço do Windows?"**
Uma thread morreria com o CLI. Um serviço (o equivalente do `dockerd`) funcionaria, mas exige instalação com privilégio de administrador. Um processo por container é o meio-termo do próprio Docker: o `containerd-shim` existe justamente para que o daemon possa reiniciar sem matar os containers.

**"O que acontece se alguém matar o shim pelo Gerenciador de Tarefas?"**
O último handle do job fecha, `KILL_ON_JOB_CLOSE` encerra o grupo inteiro, e o nome do job some do kernel. O próximo `ps` percebe que o registro diz `Executando` sem job correspondente e corrige para `Finalizado`.

**"Por que não guardar o PID e checar se o processo está vivo?"**
Porque o Windows reutiliza PIDs. Um processo qualquer pode receber o mesmo número depois que o container morrer. O nome do Job Object só existe enquanto o grupo existe.

**"O `exec` é isolado?"**
Tanto quanto o container: ele entra no mesmo Job Object, herda as restrições e os limites — inclusive o de processos — e morre junto num `stop`.

**"A imagem tem um Windows dentro?"**
Não. Tem os arquivos da aplicação. O `cmd.exe` e as DLLs vêm do host. Nem o Docker empacota o kernel; ele empacota o userland, o que no Windows exige o Host Compute System.

**"Por que copiar o rootfs em vez de usar camadas?"**
Porque a camada de união (overlayfs, ou o filtro `wcifs` no Windows) é um driver de sistema de arquivos no kernel — escrever um está fora do escopo. A cópia dá o mesmo isolamento entre containers a um custo de disco.

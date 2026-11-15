# Roteiro de demonstração

Passo a passo para mostrar o MiniDocker com três aplicações de uso real:

| Imagem | Tipo de container | O que faz |
|---|---|---|
| [`backup`](backup) | **Roda e termina** | Compacta uma pasta num `.zip` datado, guarda num volume e apaga os backups antigos. Como um job agendado |
| [`processador`](processador) | **Fica rodando** | Vigia uma pasta de fila; para cada log que chega, gera um relatório com linhas, erros e avisos. Como um worker de fila |
| [`painel`](painel) | **Serviço web** | Site com os relatórios do processador, em `http://localhost:8080`. Como uma API ou um site |

E o [`minicompose.ini`](minicompose.ini), que sobe o processador (3 réplicas) e o painel juntos.

> **Sem tempo?** Pule para o [passo 10](#10-tudo-junto-compose): um `compose up` mostra imagens, volumes, rede, réplicas e logs de uma vez.

As saídas abaixo são ilustrativas: ids, horários e nomes mudam a cada execução.

## Preparação

Na raiz do repositório, num `cmd`:

```bat
dotnet build
doskey minidocker=src\MiniDocker.Cli\bin\Debug\net10.0\MiniDocker.Cli.exe $*

:: dados de exemplo: uma pasta "de documentos" e a pasta da fila
mkdir C:\demo\documentos C:\demo\fila\entrada
echo Relatorio trimestral > C:\demo\documentos\relatorio.txt
echo nome,valor > C:\demo\documentos\planilha.csv
```

## 1. Imagem sem configuração nenhuma

A pasta `exemplos\backup` só tem o `backup.bat`. Não há arquivo de configuração: a pasta inteira vira a imagem, e o único programa dela vira o comando padrão.

```bat
minidocker build -t backup exemplos\backup
```

```text
Sem MiniDockerfile: a pasta inteira vira a imagem.
Passo 1/3 : FROM scratch
Passo 2/3 : COPY . .
Passo 3/3 : CMD backup.bat
Imagem 8c1f0e2a9b3d construida como backup:latest
Comando padrao: backup.bat
```

**O que explicar:** uma imagem é **uma pasta + o comando que roda**. A saída mostra os passos equivalentes, que são os mesmos de um Dockerfile. Para mudar o comando ou passar variáveis, sem arquivo nenhum: `--cmd` e `-e` no `build`.

## 2. Job que roda e termina: o backup

```bat
minidocker run --name bkp1 -v C:\demo\documentos:origem:ro -v backups:destino backup
minidocker ps -a
```

```text
[10:02:11] Compactando origem ...
[10:02:11] Criado backup-2026-11-15-100211.zip com 412 bytes.
[10:02:11] Backups guardados:
backup-2026-11-15-100211.zip

CONTAINER ID   IMAGEM          COMANDO        CRIADO   STATUS                 NOME
a1b2c3d4e5f6   backup:latest   "backup.bat"   ha 2 s   Finalizado (0) ha 1 s  bkp1
```

**O que explicar:** dois volumes. `C:\demo\documentos` entra como `origem` **só leitura** (`:ro`); `backups` é um volume nomeado que o MiniDocker guarda para nós. O processo terminou com 0 e o container ficou `Finalizado`.

## 3. Os dados sobrevivem ao container

```bat
minidocker rm bkp1
minidocker run --rm -v C:\demo\documentos:origem:ro -v backups:destino backup
minidocker volume ls
```

```text
[10:03:40] Backups guardados:
backup-2026-11-15-100340.zip
backup-2026-11-15-100211.zip

VOLUME    TAMANHO   USADO POR   CAMINHO
backups   824 B     -           C:\Users\...\.minidocker\volumes\backups
```

**O que explicar:** o `bkp1` foi apagado, mas o zip que ele criou continua lá — estava no volume, não no container. O volume é uma **junction NTFS** dentro do rootfs; o `rm` remove a junction sem entrar nela. Rodando o backup mais 4 vezes, a retenção apaga os mais antigos (`MANTER=5`; mude com `-e MANTER=10`).

## 4. Confinamento: o container não escreve fora da própria pasta

```bat
:: tentar alterar os documentos, que entraram como :ro
minidocker run --rm -v C:\demo\documentos:origem:ro backup "echo hackeado> origem\relatorio.txt"

:: tentar escrever em qualquer lugar do disco
minidocker run --rm backup "echo hackeado> C:\demo\invasao.txt"

:: o mesmo comando, privilegiado
minidocker run --rm --privileged backup "echo hackeado> C:\demo\invasao.txt"
dir C:\demo
```

```text
Acesso negado.
Acesso negado.

 Pasta de C:\demo
invasao.txt        <- só o privilegiado conseguiu
documentos
fila
```

**O que explicar:** o container roda com **nível de integridade baixo**, como as abas de um navegador. O disco inteiro é `Medium`; só o rootfs e os volumes graváveis são rotulados `Low`. O kernel aplica a regra *no-write-up*: um processo `Low` não escreve em objeto `Medium`, **mesmo que a ACL do usuário permita**. O `:ro` sai de graça: a pasta não é rotulada. Apague o `invasao.txt` depois.

## 5. Worker em segundo plano: o processador

Este usa um `MiniDockerfile`, para mostrar `ENV` e `RUN` (o `RUN` roda durante o build, num container isolado, e grava a data de construção dentro da imagem).

```bat
minidocker build -t processador exemplos\processador
minidocker run -d --name worker -v C:\demo\fila:fila processador
copy exemplos\processador\amostras\api-2026-11-14.log C:\demo\fila\entrada\
minidocker logs worker
type C:\demo\fila\saida\api-2026-11-14.relatorio.txt
```

```text
[10:10:01] worker iniciado. Imagem construida em 15/11/2026 10:09:58
[10:10:01] worker vigiando fila\entrada a cada 2 s.
[10:10:07] worker: api-2026-11-14.log: 9 linhas, 2 erros, 2 avisos. Total desta replica: 1

arquivo:    api-2026-11-14.log
tamanho:    604 bytes
linhas:     9
erros:      2
avisos:     2
analisado:  15/11/2026 10:10:07
por:        worker
```

**O que explicar:** o `-d` roda em segundo plano; quem mantém o container vivo é o processo shim, não o terminal. O host entrega trabalho colocando arquivos na fila, que é uma pasta compartilhada com o container.

## 6. Stop e start

```bat
minidocker stop worker
minidocker ps -a
minidocker start worker
minidocker stats worker
```

**O que explicar:** o `stop` chama `TerminateJobObject` e mata o grupo inteiro de uma vez (`cmd.exe`, `ping`, `find`); código 137. O container continua existindo e o `start` volta **o mesmo**. O `stats` lê o consumo direto do Job Object.

## 7. Escala: várias réplicas dividindo a fila

```bat
minidocker rm -f worker
minidocker run -d --replicas 3 --name worker -v C:\demo\fila:fila processador
copy exemplos\processador\amostras\*.log C:\demo\fila\entrada\
minidocker ps
minidocker logs worker
```

```text
CONTAINER ID   IMAGEM               COMANDO            CRIADO   STATUS             NOME
...            processador:latest   "processador.bat"  ha 5 s   Executando ha 5 s  worker-3
...            processador:latest   "processador.bat"  ha 5 s   Executando ha 5 s  worker-2
...            processador:latest   "processador.bat"  ha 5 s   Executando ha 5 s  worker-1

worker-1 | [10:20:03] worker-1: lote-01.log: 3 linhas, 1 erros, 1 avisos. Total desta replica: 1
worker-2 | [10:20:03] worker-2: lote-02.log: 3 linhas, 1 erros, 1 avisos. Total desta replica: 1
worker-3 | [10:20:04] worker-3: lote-03.log: 3 linhas, 1 erros, 1 avisos. Total desta replica: 1
worker-1 | [10:20:04] worker-1: lote-04.log: 3 linhas, 1 erros, 1 avisos. Total desta replica: 2
```

Mudando a escala com o sistema rodando:

```bat
minidocker scale worker=5
minidocker scale worker=1
```

**O que explicar:** cada réplica é um container completo — Job Object, rootfs e log próprios — e recebe o próprio nome em `%HOSTNAME%`. As três veem a **mesma** pasta e poderiam pegar o mesmo arquivo. Quem resolve é o sistema de arquivos: cada réplica "reivindica" um arquivo movendo-o para `fila\trabalhando\<nome>`. Um `move` no mesmo disco é um **rename atômico** no NTFS: se duas tentam ao mesmo tempo, só uma consegue, e a outra pula. É exclusão mútua entre containers sem nenhum semáforo — no relatório, `por:` mostra qual réplica processou cada arquivo.

O `logs worker` (o nome do **grupo**) junta as três réplicas numa saída só, cada uma com uma cor, intercaladas pela hora em que cada linha foi escrita. `-t` mostra essa hora; `-f` acompanha ao vivo.

## 8. Erros: o kernel avisa, o container se recupera

Uma configuração errada: a fila montada **só leitura**. O processador não consegue criar as pastas e termina com código 2. Com `--restart on-failure:3`, o dono tenta de novo, com espera crescente (1 s, 2 s, 4 s):

```bat
minidocker rm -f worker-1
minidocker run -d --name quebrado --restart on-failure:3 -v C:\demo\fila:fila:ro processador
minidocker events -f
```

```text
2026-11-15 10:30:00  quebrado         criado      imagem processador:latest
2026-11-15 10:30:00  quebrado         iniciado    processador.bat
2026-11-15 10:30:00  quebrado         reiniciando reinicio 1 apos terminou com erro (codigo 2)
2026-11-15 10:30:01  quebrado         reiniciando reinicio 2 apos terminou com erro (codigo 2)
2026-11-15 10:30:03  quebrado         reiniciando reinicio 3 apos terminou com erro (codigo 2)
2026-11-15 10:30:07  quebrado         falhou     terminou com erro (codigo 2)  <<<
```

```bat
minidocker ps -a
```

```text
CONTAINER ID   ...   STATUS                                                   NOME
...                  Finalizado (2: terminou com erro (codigo 2)) ha 3 s      quebrado
```

E um erro que **só o kernel vê**, porque o programa não cai — o limite de processos. Para analisar um arquivo, o processador cria um `cmd` e um `find`; com `--pids-limit 2`, o kernel recusa o `find`:

```bat
minidocker run -d --name apertado --pids-limit 2 -v C:\demo\fila:fila processador
copy exemplos\processador\amostras\worker-2026-11-14.log C:\demo\fila\entrada\
minidocker events --erros
minidocker logs apertado
```

```text
2026-11-15 10:31:12  apertado         erro       limite de processos atingido: criacao de processo recusada  <<<
...

[minidocker] kernel: limite de processos atingido: criacao de processo recusada
[10:31:12] apertado: worker-2026-11-14.log: 0 linhas, 0 erros, 0 avisos. Total desta replica: 1
```

**O que explicar:** o Job Object está ligado a uma **porta de conclusão de E/S**. Em vez de o MiniDocker ficar consultando o container, o kernel **posta uma mensagem** quando um processo do grupo morre com exceção, estoura a memória ou é barrado pelo limite de processos. Uma thread do shim dorme esperando essas mensagens e grava no `events` e no log. Aqui o container seguiu rodando com um relatório errado ("0 linhas") — sem o evento do kernel, ninguém saberia por quê. Quando o container termina por causa de um desses erros, o motivo aparece no `ps`. O restart é o papel de um supervisor de processos (o `systemd` do Linux).

## 9. Rede: o painel web

```bat
minidocker rm -f apertado
minidocker build -t painel exemplos\painel
minidocker run -d --name painel -p 8080:80 -v C:\demo\fila:fila painel
minidocker port painel
start http://localhost:8080
minidocker logs -f painel
```

```text
80/tcp -> 127.0.0.1:8080   (interna 51234)

[minidocker] rede: 127.0.0.1:8080->80 (o programa escuta em 127.0.0.1:51234, variavel PORT_80)
[10:40:02] painel escutando em 127.0.0.1:51234
[10:40:05] GET / HTTP/1.1 -> 200 (total 1)
[10:40:10] GET / HTTP/1.1 -> 200 (total 2)
```

O navegador mostra os relatórios analisados, com os totais de erros e avisos, e atualiza a cada 5 segundos.

**O que explicar:** o Windows não dá ao container uma pilha de rede própria, então ele usa a do host. O MiniDocker sorteia uma **porta interna** livre (51234) e a entrega ao programa na variável `PORT`; o programa não precisa saber qual é. O processo dono do container abre a porta **publicada** (8080) e repassa cada conexão para a interna — é um **proxy TCP**, o mesmo papel do `docker-proxy`. A porta 8080 existe só enquanto o container roda: depois de `minidocker stop painel`, o navegador não conecta mais.

## 10. Tudo junto: compose

```bat
minidocker rm -f painel
cd exemplos
type minicompose.ini
minidocker compose up
copy processador\amostras\*.log fila\entrada\
start http://localhost:8080
minidocker compose logs -f
```

```text
Projeto relatorios: processador -> painel
[processador] construindo a imagem processador de ...\exemplos\processador
[processador] 3 container(s) criado(s)
[painel] construindo a imagem painel de ...\exemplos\painel
[painel] 1 container(s) criado(s)
[processador] processador-1 iniciado
[processador] processador-2 iniciado
[processador] processador-3 iniciado
[painel] painel-1 iniciado

CONTAINER ID   IMAGEM               ...   STATUS             PORTAS                 NOME
...            processador:latest         Executando ha 1 s                         processador-1
...            processador:latest         Executando ha 1 s                         processador-2
...            processador:latest         Executando ha 1 s                         processador-3
...            painel:latest              Executando ha 0 s  127.0.0.1:8080->80     painel-1
```

Depois:

```bat
minidocker scale processador=5     :: mais workers, com o sistema rodando
minidocker compose ps
minidocker compose down            :: remove os 6 containers; a fila e as imagens ficam
cd ..
```

**O que explicar:** o `minicompose.ini` é um INI — uma seção por serviço, as mesmas opções do `run`. O `depende = processador` faz o painel subir depois dos workers: o compose ordena os serviços como um grafo (ordenação topológica) e recusa dependências circulares. Caminhos como `./fila` são relativos ao arquivo, então o projeto funciona em qualquer máquina. `compose config` mostra o arquivo interpretado.

## 11. Limpeza

```bat
minidocker rm -f quebrado
minidocker volume rm backups
rmdir /s /q C:\demo exemplos\fila
```

## Resumo para fechar

**Imagem = receita. Container = bolo. Processo = bolo no forno. Volume = a forma, que sobra.**

| Comando | Processos | Arquivos do container | Volumes | Container |
|---|---|---|---|---|
| `run` | Nascem | Cópia nova da imagem | Montados | Um container **novo** |
| `stop` | Morrem | Ficam | Ficam | Continua existindo (`Parado`) |
| `start` | Nascem de novo, do zero | Os mesmos de antes | Os mesmos | O mesmo |
| `rm` | — | Apagados | **Ficam** | Deixa de existir |
| `scale` | Nascem ou morrem | Cópias novas / apagadas | Compartilhados | Réplicas criadas ou removidas |
| `compose up` | Nascem, na ordem das dependências | Cópias novas da imagem | Montados | Um grupo por serviço |
| `compose down` | Morrem | Apagados | **Ficam** | Os do projeto deixam de existir |

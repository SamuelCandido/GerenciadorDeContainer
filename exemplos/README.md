# Roteiro de demonstração

Passo a passo para mostrar o MiniDocker funcionando com duas aplicações reais, uma de cada tipo de container. Como o site funciona por dentro (o `.bat`, o `.ps1`, o caminho de uma requisição) está explicado em [A loja por dentro](../README.md#a-loja-por-dentro-como-o-exemplo-web-funciona), no README principal.

| Imagem | Tipo de container | O que faz | No mundo real |
|---|---|---|---|
| [`relatorio`](relatorio) | **Roda e termina** (job) | Lê um CSV de vendas, valida, soma por região, grava o relatório e sai com um código de saída | Job agendado, ETL, migração de banco, passo de CI |
| [`web`](web) | **Fica no ar** (serviço) | Site com contador de visitas, health check (`/saude`) e API (`/info`), com log de acesso | Servidor web, API, banco de dados |

As duas rodam em PowerShell **dentro** do container. Os horários, ids e PIDs mudam a cada execução.

**Versão curta (uns 5 minutos):** passos 1, 2, 4, 6, 7, 8 e 11. A ideia central é o site da loja ficar no ar em segundo plano, no navegador, enquanto você mostra o resto. Os passos marcados como opcionais servem para quando houver tempo ou perguntas.

## Preparação

Na raiz do repositório:

```bat
dotnet build
```

Crie o apelido `minidocker`. No **cmd**:

```bat
doskey minidocker=src\MiniDocker.Cli\bin\Debug\net10.0\MiniDocker.Cli.exe $*
```

No **PowerShell**, onde o `doskey` não funciona:

```powershell
Set-Alias minidocker "$PWD\src\MiniDocker.Cli\bin\Debug\net10.0\MiniDocker.Cli.exe"
```

Para fazer requisições, use o `curl.exe`, que já vem no Windows 10/11 (no PowerShell, escreva `curl.exe`, porque `curl` sozinho é outro comando), ou abra os endereços no navegador.

## 1. Build: do código à imagem

```bat
minidocker build -t relatorio exemplos\relatorio
minidocker build -t web exemplos\web
minidocker images
```

```text
Passo 1/8 : FROM scratch
Passo 2/8 : COPY servidor.ps1 .
Passo 3/8 : COPY servidor.bat .
Passo 4/8 : COPY site site
Passo 5/8 : ENV PORTA=8080
Passo 6/8 : ENV NOME_SITE=Loja do Grupo
Passo 7/8 : RUN echo 1.0.0 (build %DATE% %TIME:~0,8%) > VERSAO.txt
Passo 8/8 : CMD servidor.bat
Imagem 6a307b584815 construida como web:latest

REPOSITORIO   TAG      IMAGE ID       CRIADA   TAMANHO
web           latest   6a307b584815   ha 1 s   3,4 KB
relatorio     latest   b6626a4408c4   ha 2 s   3,4 KB
```

**O que explicar:** a imagem é o código da aplicação mais a configuração (porta, nome do site, comando de início), ou seja, o molde. O `RUN` não roda no computador: roda num **container temporário, isolado**, e o arquivo que ele gera (`VERSAO.txt`) passa a fazer parte da imagem. Todo container criado dela sabe com qual versão foi construído.

## 2. Job: o relatório roda e termina

```bat
minidocker run --name relatorio-outubro relatorio
minidocker ps -a
```

```text
[16:35:23] Lendo dados\vendas.csv
RELATORIO DE VENDAS - gerado em 05/11/2026 16:35:23
Fonte: dados\vendas.csv (15 vendas)

REGIAO        VENDAS     TOTAL (R$)
Sudeste            5      41,687.50
Sul                3      13,794.30
Nordeste           3      10,397.60
Centro-Oeste       2       6,297.90
Norte              2       2,347.50
TOTAL             15      74,524.80

Produto que mais faturou: Notebook
[16:35:23] Relatorio gravado em saida\relatorio-20261105-163523.txt. Fim do job.

CONTAINER ID   IMAGEM             COMANDO           CRIADO   STATUS                  NOME
f7fd8108d3ed   relatorio:latest   "relatorio.bat"   ha 2 s   Finalizado (0) ha 1 s   relatorio-outubro
```

O relatório gravado fica **dentro do container**, que no disco é só uma pasta:

```bat
dir %USERPROFILE%\.minidocker\containers\relatorio-outubro\saida
```

**O que explicar:** é um job, então faz o trabalho e termina. O código de saída 0 diz que deu certo. O container continua registrado, com seus arquivos e o log.

## 3. (Opcional) Job que falha: o código de saída importa

O mesmo job, apontando para um arquivo com dados ruins (a variável `ARQUIVO` vem da imagem e o `-e` a sobrescreve):

```bat
minidocker run --name relatorio-erro -e ARQUIVO=dados\vendas-com-erro.csv relatorio
minidocker ps -a
```

```text
[16:35:25] Lendo dados\vendas-com-erro.csv
[16:35:25] ERRO: linha 3 invalida: quantidade='cinco' preco='1199.00'
[16:35:25] Relatorio NAO gerado.

CONTAINER ID   IMAGEM             COMANDO           CRIADO   STATUS                  NOME
677103948f04   relatorio:latest   "relatorio.bat"   ha 1 s   Finalizado (2) ha 0 s   relatorio-erro
```

**O que explicar:** o processo terminou com código **2**, e o MiniDocker registrou isso. É assim que um agendador ou um pipeline de CI sabe que o job falhou e precisa avisar alguém, sem ler o log.

## 4. Serviço: o site fica no ar

```bat
minidocker run -d --name loja -m 256m --cpus 0.5 web
minidocker ps
```

Abra **http://localhost:8080** no navegador (recarregue algumas vezes) ou:

```bat
curl.exe http://localhost:8080/saude
curl.exe http://localhost:8080/info
minidocker logs loja
```

```text
ok
{"visitas":3,"site":"Loja do Grupo","pid":12844,"porta":"8080","versao":"1.0.0 (build 05/11/2026 16:35:55)"}

[16:35:58] Loja do Grupo v1.0.0 (build 05/11/2026 16:35:55) no ar em http://localhost:8080/ (PID 12844, 0 visita(s) ate agora)
[16:35:59] GET / 200
[16:35:59] GET / 200
[16:35:59] GET /saude 200
[16:35:59] GET /info 200
```

**O que explicar:** o `-d` roda em segundo plano, então o terminal fica livre e o site continua no ar. Quem segura o container é um processo auxiliar do MiniDocker (o papel do `containerd-shim` no Docker), não o terminal. O `/saude` é o *health check* que um orquestrador consultaria. O `-m 256m` e o `--cpus 0.5` são limites impostos **pelo kernel** ao grupo de processos do container.

## 5. (Opcional) Stats: consumo medido pelo kernel

Gere carga em outro terminal (ou recarregue o navegador várias vezes) e veja o consumo:

```bat
for /l %i in (1,1,300) do @curl.exe -s -o nul http://localhost:8080/
```

```bat
minidocker stats
```

```text
CONTAINER ID   NOME   CPU %   MEM PICO / LIMITE    PROCS / LIMITE   PROCS CRIADOS
3c71bd61237c   loja   9,4%    82,9 MB / 256,0 MB   3 / -            3
```

**O que explicar:** os números vêm direto do Job Object: é o kernel quem soma o consumo de todos os processos do container. O limite de 256 MB aparece ao lado do pico.

## 6. Stop: o site sai do ar, o container fica

```bat
minidocker stop loja
curl.exe http://localhost:8080/saude
minidocker ps -a
```

```text
loja
curl: (7) Failed to connect to localhost port 8080: Connection refused

CONTAINER ID   IMAGEM       COMANDO          CRIADO    STATUS                NOME
3c71bd61237c   web:latest   "servidor.bat"   ha 20 s   Parado (137) ha 2 s   loja
```

**O que explicar:** o `stop` chama `TerminateJobObject`, que mata **todos** os processos do grupo de uma vez (o `cmd.exe` e o PowerShell). O código 137 é o que o Docker mostra para um container morto à força. O site caiu, mas o container `loja` **continua existindo**, com os seus arquivos.

## 7. Start: o mesmo container volta, com os dados

```bat
minidocker start loja
curl.exe http://localhost:8080/info
```

```text
loja
{"visitas":304,"site":"Loja do Grupo","pid":23216,"porta":"8080",...}
```

**O que explicar:** não foi um novo `run`, foi o **mesmo** container. As 304 visitas foram preservadas porque ficam num arquivo dentro do container (`dados\visitas.txt`), e esse arquivo sobreviveu ao `stop`. O PID mudou: o processo é novo, os dados são os mesmos.

## 8. Outro container da mesma imagem

```bat
minidocker run -d --name filial -e PORTA=8081 -e "NOME_SITE=Filial Centro" web
curl.exe http://localhost:8081/info
```

```text
{"visitas":0,"site":"Filial Centro","pid":22588,"porta":"8081",...}
```

**O que explicar:** cada `run` cria uma **cópia nova** da imagem. A filial começa com 0 visitas: um container não enxerga os arquivos do outro. O `-e` trocou a porta e o nome sem reconstruir a imagem, que é exatamente para isso que servem as variáveis de ambiente.

E se dois containers tentarem a mesma porta?

```bat
minidocker run -d --name loja2 web
minidocker logs loja2
```

```text
[16:36:25] ERRO: nao foi possivel abrir a porta 8080. Ela ja esta em uso?
```

**O que explicar:** a **rede não é isolada**: os containers dividem as portas com o host, como no `--network host` do Docker. Isolar a rede no Windows exige o Host Compute System (ver o README principal).

## 9. Exec: olhando dentro do container

```bat
minidocker exec loja dir
minidocker exec loja type dados\visitas.txt
```

**O que explicar:** o `exec` roda mais um comando **dentro** do container: entra no mesmo grupo de processos, herda os limites e enxerga a pasta dele, com o `visitas.txt` que o site gravou.

## 10. (Opcional) Limites: quem recusa é o kernel

Com pouca memória, o PowerShell nem consegue subir:

```bat
minidocker run -d --name apertado -m 20m -e PORTA=8082 web
minidocker ps -a
```

```text
7cfc2f1b02e9   web:latest   "servidor.bat"   ha 5 s   Finalizado (-65536) ha 5 s   apertado
```

O `logs apertado` mostra a falha de memória: *“O arquivo de paginação é muito pequeno para que esta operação seja concluída”*. O texto sai espaçado porque o PowerShell grava esse erro em UTF-16.

Com limite de processos, o site funciona, mas o `exec` é barrado:

```bat
minidocker run -d --name travado --pids-limit 2 -e PORTA=8083 web
minidocker exec travado dir
```

```text
Erro: Nao foi possivel atribuir o processo ao Job Object.
```

**O que explicar:** não é o MiniDocker conferindo nada: o Windows recusou a memória e recusou o processo extra. A saída do `dir` ainda pode aparecer antes do erro, porque o processo roda por um instante antes de ser colocado no grupo. A criação suspensa (`CREATE_SUSPENDED`), planejada na Etapa 3, fecha essa janela.

## 11. Rm: agora sim deixa de existir

```bat
minidocker rm -f loja filial loja2 apertado travado relatorio-outubro relatorio-erro
minidocker ps -a
minidocker images
```

**O que explicar:** o `rm` apaga o registro, a pasta e o log de cada container, incluindo as 304 visitas e o relatório gerado. As **imagens continuam**: dá para criar containers novos delas a qualquer momento.

## Resumo para fechar

**Imagem = o molde (código + configuração). Container = uma instância com seus próprios dados. Processo = o programa rodando agora.**

| Comando | Processos | Arquivos do container | Container |
|---|---|---|---|
| `run` | Nascem | Cópia nova da imagem | Um container **novo** |
| `stop` | Morrem | Ficam (as visitas continuam lá) | Continua existindo (`Parado`) |
| `start` | Nascem de novo, do zero (PID novo) | Os mesmos de antes | O mesmo |
| `rm` | — | Apagados | Deixa de existir |

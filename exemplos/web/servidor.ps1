# Servidor web minimo. Roda DENTRO do container: a pasta atual e o rootfs dele.
#
#   /        pagina do site; cada acesso conta uma visita
#   /saude   health check, como os que um orquestrador consulta
#   /info    dados do container em JSON
#
# As visitas ficam em dados\visitas.txt, no rootfs do container: sobrevivem a
# um stop/start, mas somem no rm. Cada container tem a propria contagem.
$ErrorActionPreference = 'Stop'

$porta = $env:PORTA
$nomeSite = $env:NOME_SITE
$versao = (Get-Content VERSAO.txt).Trim()
$arquivoVisitas = 'dados\visitas.txt'

function Log([string] $mensagem) {
    [Console]::WriteLine(('[{0:HH:mm:ss}] {1}' -f (Get-Date), $mensagem))
}

New-Item -ItemType Directory -Force dados | Out-Null
$visitas = 0
if (Test-Path $arquivoVisitas) { $visitas = [int](Get-Content $arquivoVisitas) }

$servidor = New-Object System.Net.HttpListener
$servidor.Prefixes.Add("http://localhost:$porta/")
try {
    $servidor.Start()
} catch {
    Log "ERRO: nao foi possivel abrir a porta $porta. Ela ja esta em uso?"
    exit 1
}

Log "$nomeSite v$versao no ar em http://localhost:$porta/ (PID $PID, $visitas visita(s) ate agora)"
$modelo = Get-Content site\index.html -Raw -Encoding UTF8

while ($true) {
    $contexto = $servidor.GetContext()
    $caminho = $contexto.Request.Url.AbsolutePath
    $status = 200

    switch ($caminho) {
        '/' {
            $visitas++
            Set-Content $arquivoVisitas $visitas
            $tipo = 'text/html'
            $corpo = $modelo.Replace('{{NOME}}', $nomeSite).Replace('{{VISITAS}}', "$visitas").
                Replace('{{VERSAO}}', $versao).Replace('{{PID}}', "$PID").Replace('{{PORTA}}', $porta)
        }
        '/saude' {
            $tipo = 'text/plain'
            $corpo = 'ok'
        }
        '/info' {
            $tipo = 'application/json'
            $corpo = @{ site = $nomeSite; versao = $versao; porta = $porta; pid = $PID; visitas = $visitas } |
                ConvertTo-Json -Compress
        }
        default {
            $status = 404
            $tipo = 'text/plain'
            $corpo = 'nao encontrado'
        }
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes($corpo)
    $contexto.Response.StatusCode = $status
    $contexto.Response.ContentType = "$tipo; charset=utf-8"
    $contexto.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $contexto.Response.Close()
    Log "GET $caminho $status"
}

# Servidor HTTP minimo: um TcpListener e o protocolo escrito a mao, sem
# depender de IIS nem de permissao de administrador.
#   /                 pagina com os relatorios de fila\saida
#   /api/relatorios   os mesmos dados em JSON
#   /saude            "ok" (para checar se o container esta de pe)

$ErrorActionPreference = 'Stop'
$porta  = [int]$env:PORT
$nome   = if ($env:HOSTNAME) { $env:HOSTNAME } else { 'painel' }
$titulo = if ($env:TITULO) { $env:TITULO } else { 'Relatorios de logs' }
$pasta  = Join-Path (Get-Location) 'fila\saida'
$atendidas = 0

function Hora { (Get-Date).ToString('HH:mm:ss') }

function Ler-Relatorios {
    if (-not (Test-Path $pasta)) { return @() }
    $lista = foreach ($arquivo in Get-ChildItem $pasta -Filter '*.relatorio.txt' | Sort-Object LastWriteTime -Descending) {
        $campos = @{}
        foreach ($linha in Get-Content $arquivo.FullName) {
            $partes = $linha -split ':', 2
            if ($partes.Count -eq 2) { $campos[$partes[0].Trim()] = $partes[1].Trim() }
        }
        [pscustomobject]@{
            arquivo   = $campos['arquivo']
            linhas    = [int]$campos['linhas']
            erros     = [int]$campos['erros']
            avisos    = [int]$campos['avisos']
            por       = $campos['por']
            analisado = $campos['analisado']
        }
    }
    return @($lista)
}

function Html([string]$texto) { [System.Net.WebUtility]::HtmlEncode($texto) }

function Pagina {
    $relatorios = Ler-Relatorios
    $erros  = ($relatorios | Measure-Object erros -Sum).Sum
    $avisos = ($relatorios | Measure-Object avisos -Sum).Sum
    $linhas = foreach ($r in $relatorios) {
        $classe = if ($r.erros -gt 0) { ' class="ruim"' } else { '' }
        "<tr$classe><td>$(Html $r.arquivo)</td><td>$($r.linhas)</td><td>$($r.erros)</td><td>$($r.avisos)</td><td>$(Html $r.por)</td><td>$(Html $r.analisado)</td></tr>"
    }
    if (-not $linhas) { $linhas = '<tr><td colspan="6">Nenhum relatorio ainda. Coloque arquivos .log em fila\entrada.</td></tr>' }
@"
<!doctype html>
<html lang="pt-br"><head><meta charset="utf-8"><meta http-equiv="refresh" content="5">
<title>$(Html $titulo)</title>
<style>
 body{font-family:Segoe UI,sans-serif;margin:2rem;background:#f6f7f9;color:#1d2330}
 h1{margin:0 0 .25rem} .sub{color:#667;margin-bottom:1.5rem}
 .cards{display:flex;gap:1rem;margin-bottom:1.5rem} .card{background:#fff;border-radius:8px;padding:1rem 1.5rem;box-shadow:0 1px 3px #0002}
 .card b{display:block;font-size:1.8rem} table{border-collapse:collapse;width:100%;background:#fff;box-shadow:0 1px 3px #0002}
 th,td{padding:.5rem .75rem;border-bottom:1px solid #e3e5ea;text-align:left} th{background:#eef0f4} tr.ruim td:nth-child(3){color:#b42318;font-weight:600}
</style></head><body>
<h1>$(Html $titulo)</h1>
<div class="sub">Servido pelo container <b>$(Html $nome)</b> &middot; atualiza a cada 5 s</div>
<div class="cards">
 <div class="card"><b>$($relatorios.Count)</b>arquivos analisados</div>
 <div class="card"><b>$([int]$erros)</b>erros</div>
 <div class="card"><b>$([int]$avisos)</b>avisos</div>
</div>
<table><tr><th>Arquivo</th><th>Linhas</th><th>Erros</th><th>Avisos</th><th>Analisado por</th><th>Quando</th></tr>
$($linhas -join "`n")
</table></body></html>
"@
}

function Responder($fluxo, [int]$status, [string]$tipo, [string]$corpo) {
    $texto = @{ 200 = 'OK'; 404 = 'Not Found'; 500 = 'Internal Server Error' }[$status]
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($corpo)
    $cabecalho = "HTTP/1.1 $status $texto`r`nContent-Type: $tipo; charset=utf-8`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n"
    $inicio = [System.Text.Encoding]::ASCII.GetBytes($cabecalho)
    $fluxo.Write($inicio, 0, $inicio.Length)
    $fluxo.Write($bytes, 0, $bytes.Length)
    $fluxo.Flush()
}

$ouvinte = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $porta)
$ouvinte.Start()
Write-Host "[$(Hora)] $nome escutando em 127.0.0.1:$porta"

while ($true) {
    $cliente = $ouvinte.AcceptTcpClient()
    try {
        $fluxo = $cliente.GetStream()
        $leitor = [System.IO.StreamReader]::new($fluxo, [System.Text.Encoding]::ASCII, $false, 4096, $true)
        $pedido = $leitor.ReadLine()
        if (-not $pedido) { continue }
        while ($true) { $cab = $leitor.ReadLine(); if ([string]::IsNullOrEmpty($cab)) { break } }

        $caminho = ($pedido -split ' ')[1]
        $status = 200
        switch -Regex ($caminho) {
            '^/saude'          { Responder $fluxo 200 'text/plain' "ok $nome"; break }
            '^/api/relatorios' { Responder $fluxo 200 'application/json' (ConvertTo-Json -InputObject @(Ler-Relatorios) -Depth 3); break }
            '^/(\?.*)?$'       { Responder $fluxo 200 'text/html' (Pagina); break }
            default            { $status = 404; Responder $fluxo 404 'text/plain' 'nao encontrado' }
        }
        $atendidas++
        Write-Host "[$(Hora)] $pedido -> $status (total $atendidas)"
    }
    catch {
        Write-Host "[$(Hora)] ERRO ao atender: $($_.Exception.Message)"
        try { Responder $fluxo 500 'text/plain' 'erro interno' } catch { }
    }
    finally {
        $cliente.Close()
    }
}

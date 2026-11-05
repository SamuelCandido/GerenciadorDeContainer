# Job de relatorio. Roda DENTRO do container: a pasta atual e o rootfs dele.
#
# Le o CSV indicado em ARQUIVO, valida cada linha, soma as vendas por regiao e
# grava saida\relatorio-<data>.txt. O codigo de saida diz como foi:
#   0  sucesso
#   2  dados invalidos (o container fica "Finalizado (2)", como um job que falhou)
$ErrorActionPreference = 'Stop'
$cultura = [Globalization.CultureInfo]::InvariantCulture

function Log([string] $mensagem) {
    [Console]::WriteLine(('[{0:HH:mm:ss}] {1}' -f (Get-Date), $mensagem))
}

$arquivo = $env:ARQUIVO
if (-not (Test-Path $arquivo)) {
    Log "ERRO: arquivo '$arquivo' nao existe no container."
    exit 2
}

Log "Lendo $arquivo"
$vendas = @(Import-Csv $arquivo)

$linha = 1
foreach ($venda in $vendas) {
    $linha++
    $quantidade = 0
    $preco = [decimal]0
    if (-not [int]::TryParse($venda.quantidade, [ref] $quantidade) -or $quantidade -le 0 -or
        -not [decimal]::TryParse($venda.preco_unitario, [Globalization.NumberStyles]::Number, $cultura, [ref] $preco)) {
        Log "ERRO: linha $linha invalida: quantidade='$($venda.quantidade)' preco='$($venda.preco_unitario)'"
        Log 'Relatorio NAO gerado.'
        exit 2
    }
    $venda | Add-Member total ($quantidade * $preco)
}

$porRegiao = $vendas | Group-Object regiao | ForEach-Object {
    [pscustomobject]@{
        Regiao = $_.Name
        Vendas = $_.Count
        Total  = ($_.Group | Measure-Object total -Sum).Sum
    }
} | Sort-Object Total -Descending

$totalGeral = ($vendas | Measure-Object total -Sum).Sum
$maisVendido = $vendas | Group-Object produto |
    Sort-Object { ($_.Group | Measure-Object total -Sum).Sum } -Descending | Select-Object -First 1

$relatorio = New-Object System.Collections.Generic.List[string]
$relatorio.Add("RELATORIO DE VENDAS - gerado em $(Get-Date -Format 'dd/MM/yyyy HH:mm:ss')")
$relatorio.Add("Fonte: $arquivo ($($vendas.Count) vendas)")
$relatorio.Add('')
$colunas = '{0,-12} {1,7} {2,14}'
$relatorio.Add(($colunas -f 'REGIAO', 'VENDAS', 'TOTAL (R$)'))
foreach ($r in $porRegiao) {
    $relatorio.Add(($colunas -f $r.Regiao, $r.Vendas, $r.Total.ToString('N2', $cultura)))
}
$relatorio.Add(($colunas -f 'TOTAL', $vendas.Count, $totalGeral.ToString('N2', $cultura)))
$relatorio.Add('')
$relatorio.Add("Produto que mais faturou: $($maisVendido.Name)")

$destino = 'saida\relatorio-{0:yyyyMMdd-HHmmss}.txt' -f (Get-Date)
$relatorio | Set-Content $destino

$relatorio | ForEach-Object { [Console]::WriteLine($_) }
Log "Relatorio gravado em $destino. Fim do job."
exit 0

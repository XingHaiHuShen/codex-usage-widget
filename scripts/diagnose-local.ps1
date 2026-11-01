param([Parameter(Mandatory)][string]$CodexPath, [string]$WorkingDirectory = (Get-Location).Path)
$ErrorActionPreference = 'Stop'
$si = [Diagnostics.ProcessStartInfo]::new($CodexPath)
$si.ArgumentList.Add('app-server')
$si.UseShellExecute = $false
$si.CreateNoWindow = $true
$si.RedirectStandardInput = $true
$si.RedirectStandardOutput = $true
$si.RedirectStandardError = $true
$proxy = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
if ($proxy.ProxyEnable -eq 1 -and $proxy.ProxyServer -match '^[^=]+:\d+$') {
    $si.Environment['HTTPS_PROXY'] = 'http://' + $proxy.ProxyServer
    $si.Environment['HTTP_PROXY'] = 'http://' + $proxy.ProxyServer
}
$proc = [Diagnostics.Process]::Start($si)
$errors = $proc.StandardError.ReadToEndAsync()
$script:rpcId = 0
function Request($method, $parameters) {
    $script:rpcId++
    $id = $script:rpcId
    $proc.StandardInput.WriteLine((@{id=$id;method=$method;params=$parameters}|ConvertTo-Json -Depth 8 -Compress))
    $proc.StandardInput.Flush()
    do {
        $pending = $proc.StandardOutput.ReadLineAsync()
        if (-not $pending.Wait(20000)) { throw "$method timed out" }
        $line = $pending.Result
        if ($null -eq $line) { throw 'app-server closed' }
        $reply = $line | ConvertFrom-Json
    } until ($reply.id -eq $id)
    if ($reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }
    return $reply.result
}
try {
    $init = Request initialize @{clientInfo=@{name='codex_usage_widget_diagnostic';version='1.0'};capabilities=@{experimentalApi=$true}}
    $proc.StandardInput.WriteLine('{"method":"initialized"}')
    $proc.StandardInput.Flush()
    [pscustomobject]@{Stage='initialize';Result=$init} | ConvertTo-Json -Depth 5 -Compress
    foreach ($method in @('account/rateLimits/read','hooks/list','thread/list')) {
        try {
            $params = switch ($method) { 'hooks/list' { @{cwds=@($WorkingDirectory)} } 'thread/list' { @{limit=3;sortKey='updated_at'} } default { @{} } }
            $r = Request $method $params
            if ($method -eq 'thread/list') {
                $r.data | ForEach-Object { [pscustomobject]@{Stage='thread';Id=$_.id;Status=$_.status;Source=$_.source} } | ConvertTo-Json -Depth 5 -Compress
                $threadId = $r.data[0].id
                $turns = Request 'thread/turns/list' @{threadId=$threadId;limit=2;sortDirection='desc';itemsView='notLoaded'}
                $turns.data | ForEach-Object { [pscustomobject]@{Stage='turn';Id=$_.id;Status=$_.status;CompletedAt=$_.completedAt} } | ConvertTo-Json -Compress
            } elseif ($method -eq 'hooks/list') {
                $r.data | ForEach-Object { $_.hooks } | ForEach-Object {
                    [pscustomobject]@{Stage=$method;Event=$_.eventName;Enabled=$_.enabled;Trust=$_.trustStatus}
                } | ConvertTo-Json -Compress
            } else { [pscustomobject]@{Stage=$method;Success=$true} | ConvertTo-Json -Compress }
        } catch { [pscustomobject]@{Stage=$method;Error=$_.Exception.Message} | ConvertTo-Json -Compress }
    }
} finally {
    $proc.StandardInput.Close()
    if (-not $proc.WaitForExit(2000)) { $proc.Kill($true) }
    $proc.Dispose()
}

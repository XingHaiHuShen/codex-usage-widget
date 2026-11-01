[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [switch]$UpdateHooks
)
$ErrorActionPreference = 'Stop'
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$installRoot = (Resolve-Path -LiteralPath $InstallDirectory).Path
if ((Get-Item -LiteralPath $installRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The installation directory must not be a directory link.'
}
$newExecutable = Join-Path $publishRoot 'CodexUsageWidget.exe'
$installedExecutable = Join-Path $installRoot 'CodexUsageWidget.exe'
if (-not (Test-Path -LiteralPath $newExecutable -PathType Leaf)) { throw 'Published executable not found.' }
$backup = Join-Path $installRoot ('backup\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($name in @('CodexUsageWidget.exe','README.md','LICENSE')) {
    $existing = Join-Path $installRoot $name
    if (Test-Path -LiteralPath $existing -PathType Leaf) { Copy-Item -LiteralPath $existing -Destination $backup }
}
$codexRoot = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex' }
$hooksFile = Join-Path $codexRoot 'hooks.json'
if ($UpdateHooks -and (Test-Path -LiteralPath $hooksFile -PathType Leaf)) {
    Copy-Item -LiteralPath $hooksFile -Destination (Join-Path $backup 'hooks.json')
}
Get-Process -Name 'CodexUsageWidget' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Path -eq $installedExecutable) {
        Stop-Process -Id $_.Id
        $_.WaitForExit()
    }
}
try {
    foreach ($name in @('CodexUsageWidget.exe','README.md','LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $publishRoot $name) -Destination (Join-Path $installRoot $name)
    }
    if ($UpdateHooks) {
        $info = [Diagnostics.ProcessStartInfo]::new($installedExecutable)
        $info.ArgumentList.Add('--install-activity-hooks')
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $installer = [Diagnostics.Process]::Start($info)
        try {
            $output = $installer.StandardOutput.ReadToEndAsync()
            $errors = $installer.StandardError.ReadToEndAsync()
            $installer.StandardInput.WriteLine('y')
            $installer.StandardInput.Close()
            if (-not $installer.WaitForExit(30000)) { throw 'Hook installation timed out.' }
            [IO.File]::WriteAllText((Join-Path $backup 'hooks-install-review.txt'), $output.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult())
            if ($installer.ExitCode -ne 0) { throw 'Hook installation failed; see the backup review file.' }
        } finally { $installer.Dispose() }
    }
    $app = Start-Process -FilePath $installedExecutable -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
    [pscustomobject]@{Installed=$installedExecutable;ProcessId=$app.Id;Backup=$backup;HookTrustRequired=[bool]$UpdateHooks}
} catch {
    # Keep the backup for explicit rollback. Do not overwrite a concurrently edited hook file.
    throw "Installation did not finish. Backup: $backup. $($_.Exception.Message)"
}

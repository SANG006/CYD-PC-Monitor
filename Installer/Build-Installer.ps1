param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
if (-not $CompilerPath) {
    $workspaceDirectory = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
    $compilerCandidates = @(
        (Join-Path $workspaceDirectory 'work\installer\inno\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
    )
    $CompilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $CompilerPath) { throw 'Inno Setup compiler not found. Pass -CompilerPath with the path to ISCC.exe.' }
& $CompilerPath (Join-Path $PSScriptRoot 'CYD-PC-Monitor.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }

$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$windowsPath = Join-Path $packageRoot 'Windows'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$PSScriptRoot\Monitor.ico" "/out:$windowsPath\CYD-PC-Monitor.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/reference:$windowsPath\LibreHardwareMonitorLib.dll" "$PSScriptRoot\PCMonitor.cs" "$PSScriptRoot\FirmwareUpload.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

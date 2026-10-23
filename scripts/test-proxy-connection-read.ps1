$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$taskCompiler = (& $taskVswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/Roslyn/csc.exe' | Select-Object -First 1)
if (-not $taskCompiler) { throw 'Framework compiler is required.' }
$taskDirectory = New-Item -ItemType Directory -Path (Join-Path $taskRoot ('BarrageGrab/bin/Debug/ProxyConnectionRead-' + [Guid]::NewGuid().ToString('N')))
foreach ($taskPlatform in @('x86','x64')) {
  $taskExe = Join-Path $taskDirectory.FullName ("Read-$taskPlatform.exe")
  & $taskCompiler /nologo /target:exe "/platform:$taskPlatform" "/out:$taskExe" (Join-Path $PSScriptRoot 'ProxyConnectionReadOnly.cs') (Join-Path $taskRoot 'BarrageGrab/Utility/WindowsProxyConnection.cs')
  if ($LASTEXITCODE -ne 0) { throw 'Native reader fixture compilation failed.' }
  Write-Host $taskPlatform
  & $taskExe
  if ($LASTEXITCODE -ne 0) { throw 'Read-only WinINet query failed.' }
}

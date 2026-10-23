$ErrorActionPreference = 'Stop'
$taskBackendRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$taskCompiler = (& $taskVswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/Roslyn/csc.exe' | Select-Object -First 1)
$taskDir = New-Item -ItemType Directory -Path (Join-Path $taskBackendRoot ('BarrageGrab/bin/Debug/OwnershipRegression-' + [Guid]::NewGuid().ToString('N')))
$taskNewtonsoft = Join-Path $taskBackendRoot 'BarrageGrab/bin/Debug/Newtonsoft.Json.dll'
Copy-Item -LiteralPath $taskNewtonsoft -Destination $taskDir.FullName
& $taskCompiler /nologo /nowarn:0436 /target:exe "/reference:$taskNewtonsoft" "/out:$($taskDir.FullName)/OwnershipRegression.exe" (Join-Path $PSScriptRoot 'OwnershipRegression.cs') (Join-Path $taskBackendRoot 'BarrageGrab/Utility/OwnedProxySettings.cs') (Join-Path $taskBackendRoot 'BarrageGrab/Utility/WindowsProxyConnection.cs') (Join-Path $taskBackendRoot 'BarrageGrab/Utility/OwnedCompanionFile.cs')
if ($LASTEXITCODE -ne 0) { throw 'Ownership compilation failed' }
& (Join-Path $taskDir.FullName 'OwnershipRegression.exe')
if ($LASTEXITCODE -ne 0) { throw 'Ownership regression failed' }

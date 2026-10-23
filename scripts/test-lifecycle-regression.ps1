$ErrorActionPreference = 'Stop'
$taskBackendRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$taskCompiler = (& $taskVswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/Roslyn/csc.exe' | Select-Object -First 1)
$taskDir = New-Item -ItemType Directory -Path (Join-Path $taskBackendRoot ('BarrageGrab/bin/Debug/LifecycleRegression-' + [Guid]::NewGuid().ToString('N')))
& $taskCompiler /nologo /target:exe "/out:$($taskDir.FullName)/LifecycleRegression.exe" (Join-Path $PSScriptRoot 'LifecycleRegression.cs') (Join-Path $taskBackendRoot 'BarrageGrab/Server/BoundedRoomCache.cs') (Join-Path $taskBackendRoot 'BarrageGrab/Server/FixedMessageWindow.cs') (Join-Path $taskBackendRoot 'BarrageGrab/Proxy/RoomRequestCoordinator.cs')
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle compilation failed' }
& (Join-Path $taskDir.FullName 'LifecycleRegression.exe')
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle regression failed' }

param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output = Join-Path $backendRoot ("BarrageGrab\bin\$Configuration")
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$compiler = (& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1)
if (-not $compiler) { throw 'Visual Studio Roslyn compiler is required.' }
$testDir = New-Item -ItemType Directory -Path (Join-Path $output ('TransportRegression-' + [Guid]::NewGuid().ToString('N')))
& $compiler /nologo /target:exe "/out:$($testDir.FullName)\TransportRegression.exe" (Join-Path $PSScriptRoot 'TransportRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Transport regression compilation failed.' }
& (Join-Path $testDir.FullName 'TransportRegression.exe') (Join-Path $output 'DanmakuBackend.exe')
if ($LASTEXITCODE -ne 0) { throw 'Transport regression failed.' }

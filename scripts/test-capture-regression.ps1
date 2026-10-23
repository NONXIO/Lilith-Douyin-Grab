param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output = Join-Path $backendRoot ("BarrageGrab\bin\$Configuration")
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$compiler = (& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1)
if (-not $compiler) { throw 'Visual Studio Roslyn compiler is required.' }
$testDir = New-Item -ItemType Directory -Path (Join-Path $output ('CaptureRegression-' + [Guid]::NewGuid().ToString('N')))
$compileArgs = @('/nologo', '/target:exe', "/out:$($testDir.FullName)\CaptureRegression.exe")
if ($Configuration -eq 'Release') { $compileArgs += '/checked+' }
foreach ($name in @('DanmakuBackend.exe', 'Titanium.Web.Proxy.dll', 'Newtonsoft.Json.dll')) {
  $source = Join-Path $output $name
  if (-not (Test-Path -LiteralPath $source)) { throw "Missing build dependency: $source" }
  $compileArgs += "/r:$source"
  Copy-Item -LiteralPath $source -Destination $testDir.FullName
}
foreach ($name in @('WebSocketStreamDecoder.cs', 'BoundedCapturePayload.cs', 'UpstreamCompatibility.cs', 'CaptureSelfTest.cs')) {
  $compileArgs += Join-Path $backendRoot ("BarrageGrab\Proxy\$name")
}
$compileArgs += Join-Path $PSScriptRoot 'CaptureRegression.cs'
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Capture regression compilation failed.' }
& (Join-Path $testDir.FullName 'CaptureRegression.exe')
if ($LASTEXITCODE -ne 0) { throw 'Capture regression failed.' }

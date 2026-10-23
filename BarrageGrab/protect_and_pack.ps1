param([string]$OutputDirectory = '')
$ErrorActionPreference = "Stop"

# --- CONFIGURATION ---
# PLEASE SET THIS TO YOUR CONFUSER.CLI.EXE PATH
$ConfuserCliPath = Join-Path $PSScriptRoot "..\ConfuserEx-CLI\Confuser.CLI.exe"
if (-not (Test-Path $ConfuserCliPath)) {
    # Fallback or check for env var if needed, mostly for local dev overrides
    if ($env:CONFUSER_CLI_PATH) {
        $ConfuserCliPath = $env:CONFUSER_CLI_PATH
    }
}
# ---------------------

$ScriptDir = $PSScriptRoot
$BinDir = Join-Path $ScriptDir "bin\Release"
$OutputDir = Join-Path $BinDir "Confused"
if ($OutputDirectory) { $OutputDir = [IO.Path]::GetFullPath($OutputDirectory) }
$ResolvedBinDir = [IO.Path]::GetFullPath($BinDir)
if (-not ([IO.Path]::GetFullPath($OutputDir)).StartsWith($ResolvedBinDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Protection output must stay inside bin/Release.'
}
$MainExe = "DanmakuBackend.exe"
$MainExePath = Join-Path $BinDir $MainExe

Write-Host "Checking for built files in $BinDir..." -ForegroundColor Cyan

if (-not (Test-Path $MainExePath)) {
    Write-Host "Error: Main executable not found at $MainExePath" -ForegroundColor Red
    Write-Host "Please build the project in RELEASE mode first!" -ForegroundColor Yellow
    exit 1
}

# Generate Confuser.crproj
Write-Host "Generating confuser.crproj..." -ForegroundColor Cyan

$xmlOutputDir = [Security.SecurityElement]::Escape($OutputDir)
$xmlBinDir = [Security.SecurityElement]::Escape($BinDir)
$xmlContent = @"
<project outputDir="$xmlOutputDir" baseDir="$xmlBinDir" xmlns="http://confuser.codeplex.com">
  <rule pattern="true" preset="none" />
  <rule pattern="match('$MainExe')" preset="maximum" />
  <packer id="compressor" />
  <module path="$MainExe" />
"@

# Add all DLLs in the folder
$Dlls = Get-ChildItem -Path $BinDir -Filter "*.dll"
foreach ($dll in $Dlls) {
    # Skip common localized resources or non-essential files if needed
    # For now, include all DLLs as requested
    $xmlContent += "  <module path=""$($dll.Name)"" />`n"
}

$xmlContent += "</project>"
$CrprojPath = Join-Path $ScriptDir "confuser.crproj"
if ($OutputDirectory) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    $CrprojPath = Join-Path $OutputDir 'confuser.crproj'
}
$xmlContent | Out-File -FilePath $CrprojPath -Encoding UTF8

Write-Host "Project file created at $CrprojPath" -ForegroundColor Green

# Check and Run ConfuserEx
if (Test-Path $ConfuserCliPath) {
    Write-Host "Running ConfuserEx..." -ForegroundColor Cyan
    # -n argument prevents ConfuserEx from pausing at the end
    $origErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $ConfuserCliPath -n $CrprojPath
    }
    finally {
        $ErrorActionPreference = $origErrorActionPreference
    }
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Protection and Packing Complete!" -ForegroundColor Green
        # exe.config 包含 binding redirects（如 System.Memory 4.0.1.x -> 4.0.5.0），
        # 必须随包发布，否则部分机器会因无法解析程序集版本而启动失败。
        # 用户配置仍在 config.json，exe.config 仅含 .NET 运行时基础设施。
        $ConfigName = "$MainExe.config"
        $SourceConfig = Join-Path $BinDir $ConfigName
        $DestConfig = Join-Path $OutputDir $ConfigName
        if (Test-Path $SourceConfig) {
            Copy-Item $SourceConfig $DestConfig -Force
            Write-Host "Copied runtime config (binding redirects) to $DestConfig" -ForegroundColor Green
        }
        else {
            Write-Host "Warning: Config file not found at $SourceConfig" -ForegroundColor Yellow
        }

        # Cleanup PDB files
        Get-ChildItem -LiteralPath $OutputDir -Filter "*.pdb" -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        Write-Host "Cleaned up PDB files." -ForegroundColor Green
        Write-Host "Output is available at: $OutputDir\$MainExe" -ForegroundColor Green
    }
    else {
        throw "ConfuserEx failed (exit code $LASTEXITCODE); do not publish its output."
        # Note: ConfuserEx usually pauses or returns 0 even on partial errors, check output log
    }
}
else {
    throw "Confuser.CLI.exe not found at '$ConfuserCliPath'. Set CONFUSER_CLI_PATH."
}
if (-not (Test-Path -LiteralPath (Join-Path $OutputDir $MainExe))) {
    throw 'Protected Backend executable was not generated.'
}

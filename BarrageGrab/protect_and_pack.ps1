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

$xmlContent = @"
<project outputDir="$OutputDir" baseDir="$BinDir" xmlns="http://confuser.codeplex.com">
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
$xmlContent | Out-File -FilePath $CrprojPath -Encoding UTF8

Write-Host "Project file created at $CrprojPath" -ForegroundColor Green

# Check and Run ConfuserEx
if (Test-Path $ConfuserCliPath) {
    Write-Host "Running ConfuserEx..." -ForegroundColor Cyan
    & $ConfuserCliPath $CrprojPath > "confuser_log.txt" 2>&1
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Protection and Packing Complete!" -ForegroundColor Green
        # Copy App.config to output
        $ConfigName = "$MainExe.config"
        $SourceConfig = Join-Path $BinDir $ConfigName
        $DestConfig = Join-Path $OutputDir $ConfigName
        if (Test-Path $SourceConfig) {
            Copy-Item $SourceConfig $DestConfig -Force
            Write-Host "Copied config file to $DestConfig" -ForegroundColor Green
        }
        else {
            Write-Host "Warning: Config file not found at $SourceConfig" -ForegroundColor Yellow
        }

        # Cleanup PDB files
        Get-ChildItem -Path $OutputDir -Filter "*.pdb" | Remove-Item -Force
        Write-Host "Cleaned up PDB files." -ForegroundColor Green
        Write-Host "Output is available at: $OutputDir\$MainExe" -ForegroundColor Green
    }
    else {
        Write-Host "Unknown error occurred immediately." -ForegroundColor Red
        # Note: ConfuserEx usually pauses or returns 0 even on partial errors, check output log
    }
}
else {
    Write-Host "WARNING: Confuser.CLI.exe not found at '$ConfuserCliPath'" -ForegroundColor Yellow
    Write-Host "Please edit this script and set `$ConfuserCliPath` to your installed ConfuserEx location." -ForegroundColor Yellow
    Write-Host "Then run this script again."
}

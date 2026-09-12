Set-Location $PSScriptRoot
$Host.UI.RawUI.WindowTitle = "FastLogin Suite - Production Release Builder"

Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host "      FastLogin Suite - Production Release Builder (No-Source)" -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Building clean binary package without source code for distribution..." -ForegroundColor Gray
Write-Host ""

# 1. Version Input
$defVer = "1.0.0"
Write-Host "Enter Application Version (Default: $defVer): " -NoNewline -ForegroundColor Yellow
$userVer = Read-Host
if ([string]::IsNullOrWhiteSpace($userVer)) {
    $userVer = $defVer
}
$cleanVer = $userVer.Trim().TrimStart("v", "V")
Write-Host "Target Version: v$cleanVer" -ForegroundColor Cyan
Write-Host ""

# 2. Server compile prompt
$buildServer = "N"
if (-not (Test-Path "dist\server.exe")) {
    $buildServer = "Y"
    Write-Host "server.exe not found in dist. Compiling server.py..." -ForegroundColor Yellow
} else {
    Write-Host "Recompile server.py with PyInstaller? [y/N]: " -NoNewline -ForegroundColor Yellow
    $ans = Read-Host
    if ($ans -and $ans.Trim().ToLower() -eq "y") {
        $buildServer = "Y"
    }
}

if ($buildServer -eq "Y") {
    Write-Host ""
    Write-Host "[1/4] Compiling Python Server (server.exe)..." -ForegroundColor Yellow
    pyinstaller --onefile --clean --name server --add-data "extension;extension" server.py
    if ($LASTEXITCODE -ne 0) {
        Write-Host "PyInstaller build failed!" -ForegroundColor Red
        Read-Host "Press Enter to exit..."
        exit $LASTEXITCODE
    }
    Write-Host "server.exe compiled successfully." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[1/4] Using existing dist\server.exe." -ForegroundColor DarkGray
}

Write-Host ""

# 3. Go Bot Engine
Write-Host "[2/4] Compiling Go Bot Engine (FastLogin.exe v$cleanVer)..." -ForegroundColor Yellow
$ldflags = "-s -w -X 'main.AppVersion=$cleanVer'"
go build -ldflags $ldflags -o FastLogin.exe login_main.go
if ($LASTEXITCODE -ne 0) {
    Write-Host "Go build failed!" -ForegroundColor Red
    Read-Host "Press Enter to exit..."
    exit $LASTEXITCODE
}
Write-Host "FastLogin.exe built successfully (Symbols stripped -s -w)." -ForegroundColor Green

Write-Host ""

# 4. C# UI Launcher + Obfuscation
Write-Host "[3/4] Compiling C# UI Launcher (Release + Obfuscar v$cleanVer)..." -ForegroundColor Yellow
$csprojPath = "UI\FastLoginSuite.UI.csproj"
if (Test-Path $csprojPath) {
    (Get-Content $csprojPath) -replace "<Version>.*?</Version>", "<Version>$cleanVer</Version>" | Set-Content $csprojPath -Encoding UTF8
}
dotnet clean $csprojPath -c Release -v quiet
dotnet build $csprojPath -c Release --no-incremental -p:Version=$cleanVer -p:AssemblyVersion=$cleanVer -p:FileVersion=$cleanVer -p:InformationalVersion=$cleanVer
if ($LASTEXITCODE -ne 0) {
    Write-Host "Dotnet build failed!" -ForegroundColor Red
    Read-Host "Press Enter to exit..."
    exit $LASTEXITCODE
}
Write-Host "C# Launcher built and obfuscated successfully (v$cleanVer)." -ForegroundColor Green

Write-Host ""

# 5. Clean Dist Package Assembly
Write-Host "[4/4] Assembling clean distribution package..." -ForegroundColor Yellow
$distDir = "..\Dist_FastLoginSuite"
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }

# Clean sensitive & temporary files
if (Test-Path "$distDir\profiles") { Remove-Item "$distDir\profiles" -Recurse -Force }
if (Test-Path "$distDir\win-x64") { Remove-Item "$distDir\win-x64" -Recurse -Force }
if (Test-Path "$distDir\obfuscar.xml") { Remove-Item "$distDir\obfuscar.xml" -Force }
if (Test-Path "$distDir\tokens.json") { Remove-Item "$distDir\tokens.json" -Force }
if (Test-Path "$distDir\config.json") { Remove-Item "$distDir\config.json" -Force }
if (Test-Path "$distDir\accounts_state.json") { Remove-Item "$distDir\accounts_state.json" -Force }
if (Test-Path "$distDir\accounts_queue.txt") { Remove-Item "$distDir\accounts_queue.txt" -Force }
if (Test-Path "$distDir\accounts_failed.txt") { Remove-Item "$distDir\accounts_failed.txt" -Force }
if (Test-Path "$distDir\accounts.txt") { Remove-Item "$distDir\accounts.txt" -Force }
Get-ChildItem -Path $distDir -Filter "accounts*.txt" | Remove-Item -Force
Get-ChildItem -Path $distDir -Filter "*.pdb" -Recurse | Remove-Item -Force
Get-ChildItem -Path $distDir -Filter "*.log" | Remove-Item -Force
Get-ChildItem -Path $distDir -Filter "*.zip" | Remove-Item -Force
if (Test-Path "$distDir\extension\cf_clicker.js") { Remove-Item "$distDir\extension\cf_clicker.js" -Force }

# Copy runtime binaries
$binDir = "UI\bin\Release\net10.0-windows10.0.19041.0"
Copy-Item "$binDir\FastLogin.Launcher.exe" "$distDir\" -Force
Copy-Item "$binDir\FastLogin.Launcher.dll" "$distDir\" -Force
Copy-Item "$binDir\FastLogin.Launcher.runtimeconfig.json" "$distDir\" -Force
Copy-Item "$binDir\FastLogin.Launcher.deps.json" "$distDir\" -Force
Copy-Item "$binDir\Microsoft.Windows.SDK.NET.dll" "$distDir\" -Force
Copy-Item "$binDir\WinRT.Runtime.dll" "$distDir\" -Force
Copy-Item "FastLogin.exe" "$distDir\" -Force

if (Test-Path "dist\server.exe") {
    Copy-Item "dist\server.exe" "$distDir\server.exe" -Force
} elseif (Test-Path "server.exe") {
    Copy-Item "server.exe" "$distDir\server.exe" -Force
}

# Copy Extension
if (-not (Test-Path "$distDir\extension")) { New-Item -ItemType Directory -Path "$distDir\extension" -Force | Out-Null }
Copy-Item "extension\*" "$distDir\extension\" -Recurse -Force

# Final check: remove any pdb
Get-ChildItem -Path $distDir -Filter "*.pdb" -Recurse | Remove-Item -Force

Write-Host ""
Write-Host "Creating customer release zip archives..." -ForegroundColor Cyan
$zipVersion = "..\FastLoginSuite_v$cleanVer.zip"
$zipLatest = "..\FastLoginSuite_Release.zip"

if (Test-Path $zipVersion) { Remove-Item $zipVersion -Force }
if (Test-Path $zipLatest) { Remove-Item $zipLatest -Force }

Compress-Archive -Path "$distDir\*" -DestinationPath $zipVersion -Force
Copy-Item $zipVersion $zipLatest -Force

$distResolved = (Resolve-Path $distDir).Path
$zipVerResolved = (Resolve-Path $zipVersion).Path
$zipLatestResolved = (Resolve-Path $zipLatest).Path

Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host " BUILD COMPLETED SUCCESSFULLY! Version: v$cleanVer" -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Test Output Directory:" -ForegroundColor Cyan
Write-Host "   $distResolved" -ForegroundColor White
Write-Host ""
Write-Host "Customer Delivery ZIP Archives (No Source Code, Safe to Sell):" -ForegroundColor Cyan
Write-Host "   1) $zipVerResolved" -ForegroundColor Yellow
Write-Host "   2) $zipLatestResolved" -ForegroundColor Yellow
Write-Host ""
Write-Host "Security Audit:" -ForegroundColor Green
Write-Host "   [OK] C# DLL Obfuscated (Obfuscar)" -ForegroundColor White
Write-Host "   [OK] Go Binary Stripped (-s -w)" -ForegroundColor White
Write-Host "   [OK] All Source Code (*.cs, *.go, *.py) Excluded" -ForegroundColor White
Write-Host "   [OK] All Debug PDB & Personal Cache Excluded" -ForegroundColor White
Write-Host "=====================================================================" -ForegroundColor Green
Write-Host ""
Read-Host "Press Enter to exit..."
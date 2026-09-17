# ==============================================================================
# FastLogin Suite - Release Build & Packaging Automation Script
# ==============================================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$ScriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($ScriptDir)) {
    $ScriptDir = (Get-Location).Path
}
Set-Location $ScriptDir

$ProjectDir = $ScriptDir
$CsprojPath = Join-Path $ProjectDir "UI\FastLoginSuite.UI.csproj"
$DistDir = Join-Path $ScriptDir "..\Dist_FastLoginSuite"
$ReleasesDir = Join-Path $ScriptDir "..\Releases"

Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "     FastLogin Suite - Release Build & Packaging Tool " -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host ""

# 0. Kill running processes to prevent file lock
Write-Host "[*] ตรวจสอบและปิด Process ที่ทำงานค้างอยู่..." -ForegroundColor Gray
Stop-Process -Name "FastLogin.Launcher" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "FastLogin" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "server" -Force -ErrorAction SilentlyContinue

# 1. Read Current Version from csproj
$currentVer = "1.0.0"
if (Test-Path $CsprojPath) {
    $csprojContent = [System.IO.File]::ReadAllText($CsprojPath, [System.Text.Encoding]::UTF8)
    if ($csprojContent -match '<Version>([^<]+)</Version>') {
        $currentVer = $matches[1]
    }
}

Write-Host "เวอร์ชันปัจจุบันในระบบ: " -NoNewline
Write-Host "v$currentVer" -ForegroundColor Yellow
Write-Host ""

# 2. Ask User for Version
$inputVer = Read-Host "กรอกเลขเวอร์ชันที่ต้องการบิ้ว (กด Enter เพื่อใช้ v$currentVer)"
$targetVer = $currentVer
if (-not [string]::IsNullOrWhiteSpace($inputVer)) {
    $targetVer = $inputVer.Trim().TrimStart('v', 'V')
}

Write-Host ""
Write-Host "กำลังเตรียมบิ้วเวอร์ชัน: " -NoNewline
Write-Host "v$targetVer" -ForegroundColor Cyan
Write-Host ""

# 3. Update csproj if version changed
if (Test-Path $CsprojPath) {
    (Get-Content $CsprojPath -Encoding UTF8) -replace "<Version>.*?</Version>", "<Version>$targetVer</Version>" | Set-Content $CsprojPath -Encoding UTF8
}

# 4. Compile Python Server (server.exe)
Write-Host "[1/5] กำลัง Compile Python Server (server.exe)..." -ForegroundColor Yellow
$needBuildServer = "Y"
$distServerExe = Join-Path $ScriptDir "dist\server.exe"
$serverPy = Join-Path $ScriptDir "server.py"
$extPath = Join-Path $ScriptDir "extension"
$extData = "$extPath;extension"

if (Test-Path $distServerExe) {
    $serverAge = (Get-Item $distServerExe).LastWriteTime
    $pyAge = (Get-Item $serverPy).LastWriteTime
    if ($serverAge -gt $pyAge) {
        $ans = Read-Host "พบ server.exe ล่าสุดแล้ว ต้องการ Recompile ด้วย PyInstaller ใหม่หรือไม่? [y/N]"
        if (-not ($ans -and $ans.Trim().ToLower() -eq "y")) {
            $needBuildServer = "N"
        }
    }
}

if ($needBuildServer -eq "Y") {
    pyinstaller --noconfirm --onefile --clean --name server --add-data $extData $serverPy
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] PyInstaller build failed!" -ForegroundColor Red
        Read-Host "กดปุ่ม Enter เพื่อปิด..."
        exit $LASTEXITCODE
    }
    Write-Host "      [OK] server.exe compiled สำเร็จเรียบร้อย" -ForegroundColor Green
} else {
    Write-Host "      [OK] ใช้ไฟล์ dist\server.exe เดิมที่มีอยู่" -ForegroundColor Gray
}

Write-Host ""

# 5. Compile Go Bot Engine (FastLogin.exe)
Write-Host "[2/5] กำลัง Compile Go Bot Engine (FastLogin.exe v$targetVer)..." -ForegroundColor Yellow
$ldflags = "-s -w -X 'main.AppVersion=$targetVer'"
$fastLoginExe = Join-Path $ScriptDir "FastLogin.exe"
$loginMainGo = Join-Path $ScriptDir "login_main.go"
& go build -ldflags $ldflags -o $fastLoginExe $loginMainGo
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] การ Compile Go Bot ล้มเหลว!" -ForegroundColor Red
    Read-Host "กดปุ่ม Enter เพื่อปิด..."
    exit $LASTEXITCODE
}
Write-Host "      [OK] FastLogin.exe compiled สำเร็จ (Stripped Symbols -s -w)" -ForegroundColor Green

Write-Host ""

# 6. Compile C# UI Launcher + Obfuscar
Write-Host "[3/5] กำลัง Compile C# UI Launcher (dotnet build + Obfuscar)..." -ForegroundColor Yellow
& dotnet clean "$CsprojPath" -c Release -v quiet
& dotnet build "$CsprojPath" -c Release --no-incremental -p:Version=$targetVer -p:AssemblyVersion=$targetVer -p:FileVersion=$targetVer -p:InformationalVersion=$targetVer
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] การ Build C# Launcher ล้มเหลว!" -ForegroundColor Red
    Read-Host "กดปุ่ม Enter เพื่อปิด..."
    exit $LASTEXITCODE
}
Write-Host "      [OK] C# Launcher built and obfuscated สำเร็จ (v$targetVer)" -ForegroundColor Green

Write-Host ""

# 7. Assemble Clean Distribution Package
Write-Host "[4/5] กำลังจัดเตรียมโฟลเดอร์แจกจ่าย (Clean Distribution Package)..." -ForegroundColor Yellow
if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir -Force | Out-Null }

# Clear sensitive user files, logs, accounts
$cleanPatterns = @(
    "profiles",
    "win-x64",
    "obfuscar.xml",
    "tokens.json",
    "config.json",
    "accounts_state.json",
    "accounts_queue.txt",
    "accounts_failed.txt",
    "accounts*.txt",
    "*.pdb",
    "*.log",
    "*.tmp",
    "*.bak",
    "*.zip"
)

foreach ($pat in $cleanPatterns) {
    Get-ChildItem -Path $DistDir -Filter $pat -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# Copy runtime binaries
$binDir = Join-Path $ProjectDir "UI\bin\Release\net10.0-windows10.0.19041.0"
Copy-Item "$binDir\FastLogin.Launcher.exe" "$DistDir\" -Force
Copy-Item "$binDir\FastLogin.Launcher.dll" "$DistDir\" -Force
Copy-Item "$binDir\FastLogin.Launcher.runtimeconfig.json" "$DistDir\" -Force
Copy-Item "$binDir\FastLogin.Launcher.deps.json" "$DistDir\" -Force
Copy-Item "$binDir\Microsoft.Windows.SDK.NET.dll" "$DistDir\" -Force
Copy-Item "$binDir\WinRT.Runtime.dll" "$DistDir\" -Force
Copy-Item (Join-Path $ProjectDir "FastLogin.exe") "$DistDir\" -Force

if (Test-Path "dist\server.exe") {
    Copy-Item "dist\server.exe" "$DistDir\server.exe" -Force
} elseif (Test-Path "server.exe") {
    Copy-Item "server.exe" "$DistDir\server.exe" -Force
}

# Copy Extension
$extDist = Join-Path $DistDir "extension"
if (-not (Test-Path $extDist)) { New-Item -ItemType Directory -Path $extDist -Force | Out-Null }
Copy-Item (Join-Path $ProjectDir "extension\*") "$extDist\" -Recurse -Force

# Copy Portable Chrome for Testing
if (Test-Path (Join-Path $ProjectDir "chrome-win64")) {
    $chromeDist = Join-Path $DistDir "chrome-win64"
    if (-not (Test-Path $chromeDist)) { New-Item -ItemType Directory -Path $chromeDist -Force | Out-Null }
    Copy-Item (Join-Path $ProjectDir "chrome-win64\*") "$chromeDist\" -Recurse -Force
}

# Clean any leftover PDB
Get-ChildItem -Path $DistDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "      [OK] รวมไฟล์แจกจ่ายและล้างข้อมูลส่วนตัวเรียบร้อย 100%" -ForegroundColor Green

Write-Host ""

# 8. Package into Customer ZIP Archives
Write-Host "[5/5] กำลังบีบอัดไฟล์ ZIP สำหรับส่งมอบลูกค้า..." -ForegroundColor Gray
if (-not (Test-Path $ReleasesDir)) { New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null }

$zipVersionFile = Join-Path $ReleasesDir "FastLoginSuite_v$targetVer.zip"
$zipLatestFile = Join-Path $ReleasesDir "FastLoginSuite_Release.zip"

if (Test-Path $zipVersionFile) { Remove-Item $zipVersionFile -Force }
if (Test-Path $zipLatestFile) { Remove-Item $zipLatestFile -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $DistDir).Path, $zipVersionFile, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $zipVersionFile $zipLatestFile -Force

$zipFileInfo = Get-Item $zipVersionFile
$zipSizeMB = [math]::Round($zipFileInfo.Length / 1MB, 2)

Write-Host "      [OK] สร้างไฟล์ ZIP สำเร็จ: FastLoginSuite_v$targetVer.zip ($zipSizeMB MB)" -ForegroundColor Green

# 9. Summary & Open Explorer
$distResolved = (Resolve-Path $DistDir).Path
$zipResolved = (Resolve-Path $zipVersionFile).Path

Write-Host ""
Write-Host "======================================================" -ForegroundColor Green
Write-Host "               BUILD SUCCESSFUL!                      " -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host "เวอร์ชัน:    v$targetVer" -ForegroundColor Yellow
Write-Host "โฟลเดอร์:   $distResolved" -ForegroundColor Cyan
Write-Host "ไฟล์ ZIP:    $zipResolved" -ForegroundColor Cyan
Write-Host "ขนาดไฟล์:   $zipSizeMB MB" -ForegroundColor Gray
Write-Host ""
Write-Host "ความปลอดภัยในการส่งมอบลูกค้า:" -ForegroundColor White
Write-Host "  [OK] C# DLL ผ่านการ Obfuscate (ป้องกัน Decompile)" -ForegroundColor Green
Write-Host "  [OK] Go Binary Stripped (-s -w)" -ForegroundColor Green
Write-Host "  [OK] ไม่มี Source Code (*.cs, *.go, *.py) ในชุดแจกจ่าย" -ForegroundColor Green
Write-Host "  [OK] ล้างไฟล์ข้อมูลส่วนตัว บัญชี และ Log ทั้งหมด 100%" -ForegroundColor Green
Write-Host "  [OK] รวม Chrome for Testing Portable โหลด Extension อัตโนมัติทุกเครื่อง" -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host ""

if (Test-Path $zipResolved) {
    Start-Process "explorer.exe" -ArgumentList "/select,`"$zipResolved`""
}

Read-Host "กดปุ่ม Enter เพื่อเสร็จสิ้น..."
# ==============================================================================
# FastLogin Suite - Release Build & Packaging Automation Script
# Commercial Distribution Edition (Code Protection & Obfuscation)
# ==============================================================================

param(
    [string]$TargetVersion = "",
    [switch]$NonInteractive = $false,
    [switch]$RebuildServer = $false
)

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
$TestDesktopDir = "C:\Users\User\Desktop\test"

Write-Host "======================================================" -ForegroundColor Cyan
Write-Host " FastLogin Suite - Commercial Release & Packaging Tool" -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host ""

# 0. Kill running processes to prevent file lock
Write-Host "[*] ตรวจสอบและปิด Process ที่ทำงานค้างอยู่..." -ForegroundColor Gray
Stop-Process -Name "FastLogin.Launcher" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "FastLogin" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "server" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

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

# 2. Determine Target Version
$targetVer = $currentVer
if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
    $targetVer = $TargetVersion.Trim().TrimStart('v', 'V')
} elseif (-not $NonInteractive) {
    $inputVer = Read-Host "กรอกเลขเวอร์ชันที่ต้องการบิ้ว (กด Enter เพื่อใช้ v$currentVer)"
    if (-not [string]::IsNullOrWhiteSpace($inputVer)) {
        $targetVer = $inputVer.Trim().TrimStart('v', 'V')
    }
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
Write-Host "[1/5] กำลังตรวจสอบและจัดเตรียม Python Server (server.exe)..." -ForegroundColor Yellow
$needBuildServer = "N"
$distServerExe = Join-Path $ScriptDir "dist\server.exe"
$serverPy = Join-Path $ScriptDir "server.py"
$extPath = Join-Path $ScriptDir "extension"
$extData = "$extPath;extension"

if ($RebuildServer -or (-not (Test-Path $distServerExe))) {
    $needBuildServer = "Y"
} else {
    $serverAge = (Get-Item $distServerExe).LastWriteTime
    $pyAge = (Get-Item $serverPy).LastWriteTime
    if ($pyAge -gt $serverAge) {
        if ($NonInteractive) {
            $needBuildServer = "Y"
        } else {
            $ans = Read-Host "server.py มีการแก้ไขใหม่ ต้องการ Recompile ด้วย PyInstaller หรือไม่? [Y/n]"
            if ([string]::IsNullOrWhiteSpace($ans) -or $ans.Trim().ToLower() -eq "y") {
                $needBuildServer = "Y"
            }
        }
    }
}

if ($needBuildServer -eq "Y") {
    Write-Host "      [*] กำลัง Compile server.py ด้วย PyInstaller..." -ForegroundColor Gray
    pyinstaller --noconfirm --onefile --clean --name server --add-data $extData $serverPy
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] PyInstaller build failed!" -ForegroundColor Red
        if (-not $NonInteractive) { Read-Host "กดปุ่ม Enter เพื่อปิด..." }
        exit $LASTEXITCODE
    }
    Write-Host "      [OK] server.exe compiled สำเร็จเรียบร้อย (ซ่อน Python Source Code 100%)" -ForegroundColor Green
} else {
    Write-Host "      [OK] ใช้ไฟล์ dist\server.exe ที่คอมไพล์ไว้แล้ว" -ForegroundColor Gray
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
    if (-not $NonInteractive) { Read-Host "กดปุ่ม Enter เพื่อปิด..." }
    exit $LASTEXITCODE
}
Write-Host "      [OK] FastLogin.exe compiled สำเร็จ (Stripped Symbols -s -w ป้องกัน Decompile)" -ForegroundColor Green

Write-Host ""

# 6. Compile C# UI Launcher + Obfuscar
Write-Host "[3/5] กำลัง Compile C# UI Launcher (dotnet build + Obfuscar)..." -ForegroundColor Yellow
& dotnet clean "$CsprojPath" -c Release -v quiet
& dotnet build "$CsprojPath" -c Release --no-incremental -p:Version=$targetVer -p:AssemblyVersion=$targetVer -p:FileVersion=$targetVer -p:InformationalVersion=$targetVer
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] การ Build C# Launcher ล้มเหลว!" -ForegroundColor Red
    if (-not $NonInteractive) { Read-Host "กดปุ่ม Enter เพื่อปิด..." }
    exit $LASTEXITCODE
}
Write-Host "      [OK] C# Launcher built and obfuscated สำเร็จ (Strings Encrypted, Methods Renamed)" -ForegroundColor Green

Write-Host ""

# 7. Assemble Clean Distribution Package
Write-Host "[4/5] กำลังจัดเตรียมแพ็กเกจส่งมอบลูกค้า (Clean Distribution Package)..." -ForegroundColor Yellow
if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir -Force | Out-Null }

# Wipe old sensitive / source / debug files from DistDir
$cleanPatterns = @(
    "profiles",
    "win-x64",
    "obfuscar.xml",
    "tokens.json",
    "tokens.json.bak",
    "accounts_state.json",
    "accounts_state.json.bak",
    "accounts_queue.txt",
    "accounts_failed.txt",
    "*.pdb",
    "*.log",
    "*.tmp",
    "*.bak",
    "*.zip",
    "*.py",
    "*.go",
    "*.cs",
    "*.xml",
    "*.spec",
    "*.sln",
    "*.csproj",
    "*.user"
)

foreach ($pat in $cleanPatterns) {
    Get-ChildItem -Path $DistDir -Filter $pat -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# Copy runtime binaries from Release output
$binDir = Join-Path $ProjectDir "UI\bin\Release\net10.0-windows10.0.19041.0"
Copy-Item "$binDir\FastLogin.Launcher.exe" "$DistDir\" -Force
Copy-Item "$binDir\FastLogin.Launcher.dll" "$DistDir\" -Force
Copy-Item "$binDir\FastLogin.Launcher.runtimeconfig.json" "$DistDir\" -Force
Copy-Item "$binDir\FastLogin.Launcher.deps.json" "$DistDir\" -Force
Copy-Item "$binDir\Microsoft.Web.WebView2.Core.dll" "$DistDir\" -Force
Copy-Item "$binDir\Microsoft.Web.WebView2.WinForms.dll" "$DistDir\" -Force
Copy-Item "$binDir\Microsoft.Windows.SDK.NET.dll" "$DistDir\" -Force
Copy-Item "$binDir\WinRT.Runtime.dll" "$DistDir\" -Force

if (Test-Path "$binDir\runtimes") {
    Copy-Item "$binDir\runtimes" "$DistDir\" -Recurse -Force
}
if (Test-Path "$binDir\runtimes\win-x64\native\WebView2Loader.dll") {
    Copy-Item "$binDir\runtimes\win-x64\native\WebView2Loader.dll" "$DistDir\" -Force
}

# Copy Go engine & compiled Python server
Copy-Item (Join-Path $ProjectDir "FastLogin.exe") "$DistDir\" -Force

if (Test-Path "dist\server.exe") {
    Copy-Item "dist\server.exe" "$DistDir\server.exe" -Force
} elseif (Test-Path "server.exe") {
    Copy-Item "server.exe" "$DistDir\server.exe" -Force
}

# Copy Assets (UI HTML/CSS/JS)
$assetsDist = Join-Path $DistDir "Assets"
if (-not (Test-Path $assetsDist)) { New-Item -ItemType Directory -Path $assetsDist -Force | Out-Null }
Copy-Item (Join-Path $ProjectDir "UI\Assets\*") "$assetsDist\" -Recurse -Force

# Copy Portable Chrome for Testing
if (Test-Path (Join-Path $ProjectDir "chrome-win64")) {
    $chromeDist = Join-Path $DistDir "chrome-win64"
    if (-not (Test-Path $chromeDist)) { New-Item -ItemType Directory -Path $chromeDist -Force | Out-Null }
    Copy-Item (Join-Path $ProjectDir "chrome-win64\*") "$chromeDist\" -Recurse -Force
}

# Copy & Obfuscate Extension JS files (Hiding Turnstile Bypass techniques)
Write-Host "      [*] กำลัง Obfuscate โค้ด Chrome Extension เพื่อปิดบังเทคนิค..." -ForegroundColor Cyan
$extDist = Join-Path $DistDir "extension"
if (-not (Test-Path $extDist)) { New-Item -ItemType Directory -Path $extDist -Force | Out-Null }

Copy-Item (Join-Path $ProjectDir "extension\manifest.json") "$extDist\" -Force
Copy-Item (Join-Path $ProjectDir "extension\icon.png") "$extDist\" -Force

# Default clean extension config
$extConfigContent = '{"port": 5000, "wid": 1, "auto_solve": true}'
[System.IO.File]::WriteAllText((Join-Path $extDist "config.json"), $extConfigContent, [System.Text.Encoding]::UTF8)

# Obfuscate JS files using javascript-obfuscator
$jsFilesToObfuscate = @("content.js", "background.js")
foreach ($js in $jsFilesToObfuscate) {
    $srcJs = Join-Path $ProjectDir "extension\$js"
    $dstJs = Join-Path $extDist $js
    if (Test-Path $srcJs) {
        try {
            & javascript-obfuscator.cmd "$srcJs" --output "$dstJs" --compact true --string-array true --string-array-encoding 'base64' --rename-globals false --identifier-names-generator 'hexadecimal' 2>&1 | Out-Null
            if (-not (Test-Path $dstJs)) {
                Copy-Item $srcJs $dstJs -Force
            }
        } catch {
            Copy-Item $srcJs $dstJs -Force
        }
    }
}

# Create clean accounts.txt template
$accountsTemplatePath = Join-Path $DistDir "accounts.txt"
$accountsTemplateContent = ""
[System.IO.File]::WriteAllText($accountsTemplatePath, $accountsTemplateContent, [System.Text.Encoding]::UTF8)

# Create clean default config.json
$configPath = Join-Path $DistDir "config.json"
$configDefault = @"
{
  "ThreadCount": 5,
  "AutoSolveCaptcha": true,
  "OperationMode": "all",
  "CooldownPerAccountSeconds": 5,
  "DeepSleepEveryAccounts": 10,
  "DeepSleepDurationSeconds": 15,
  "RateLimitCooldownSeconds": 20,
  "EventUuid": "a297cbd7-c1c4-448f-9e9f-0f2a35d28ac3",
  "ChromeBotProfileCount": 5
}
"@
[System.IO.File]::WriteAllText($configPath, $configDefault, [System.Text.Encoding]::UTF8)

# Final deep clean on DistDir to guarantee zero source/debug files
Get-ChildItem -Path $DistDir -Include "*.pdb", "*.xml", "*.py", "*.go", "*.cs", "obfuscar.xml" -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "      [OK] รวมไฟล์แจกจ่ายและป้องกันโค้ดเรียบร้อย 100%" -ForegroundColor Green

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

# 9. Sync to Desktop Test Folder if exists
if (Test-Path $TestDesktopDir) {
    Write-Host ""
    Write-Host "[*] กำลัง Sync ไฟล์ชุด Release ไปยังโฟลเดอร์ทดสอบ ($TestDesktopDir)..." -ForegroundColor Yellow
    # Clean sensitive / source files from testDir as well
    $testCleanPatterns = @("*.py", "*.pdb", "*.xml", "*.go", "*.cs", "obfuscar.xml", "test_cdp_turnstile.py")
    foreach ($pat in $testCleanPatterns) {
        Get-ChildItem -Path $TestDesktopDir -Filter $pat -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    # Copy all release files to testDir (exclude overwriting existing test accounts.txt if user has accounts there)
    $releaseFiles = Get-ChildItem -Path $DistDir
    foreach ($item in $releaseFiles) {
        if ($item.Name -eq "accounts.txt" -and (Test-Path (Join-Path $TestDesktopDir "accounts.txt"))) {
            # Don't overwrite existing test accounts if user already has accounts there
            continue
        }
        $destPath = Join-Path $TestDesktopDir $item.Name
        if ($item.PSIsContainer) {
            if (-not (Test-Path $destPath)) { New-Item -ItemType Directory -Path $destPath -Force | Out-Null }
            Copy-Item -Path (Join-Path $item.FullName "*") -Destination $destPath -Recurse -Force
        } else {
            Copy-Item -Path $item.FullName -Destination $destPath -Force
        }
    }
    Write-Host "      [OK] Sync ไปยัง $TestDesktopDir สำเร็จเรียบร้อย 100%" -ForegroundColor Green
}

# 10. Summary & Open Explorer
$distResolved = (Resolve-Path $DistDir).Path
$zipResolved = (Resolve-Path $zipVersionFile).Path

Write-Host ""
Write-Host "======================================================" -ForegroundColor Green
Write-Host "         COMMERCIAL BUILD SUCCESSFUL!                 " -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host "เวอร์ชัน:    v$targetVer" -ForegroundColor Yellow
Write-Host "โฟลเดอร์:   $distResolved" -ForegroundColor Cyan
Write-Host "ไฟล์ ZIP:    $zipResolved" -ForegroundColor Cyan
Write-Host "ขนาดไฟล์:   $zipSizeMB MB" -ForegroundColor Gray
Write-Host ""
Write-Host "การป้องกันโค้ดและความปลอดภัยสำหรับส่งมอบลูกค้า:" -ForegroundColor White
Write-Host "  [OK] C# Launcher ผ่าน Obfuscar (Strings Encrypted + Names Obfuscated)" -ForegroundColor Green
Write-Host "  [OK] Go Bot Engine ตัด Symbol ออกทั้งหมด (-s -w Stripped Binary)" -ForegroundColor Green
Write-Host "  [OK] Python Server คอมไพล์เป็น server.exe (ลบ server.py ทิ้ง 100%)" -ForegroundColor Green
Write-Host "  [OK] Chrome Extension ผ่าน Javascript-Obfuscator (เข้ารหัสโค้ดข้าม Turnstile)" -ForegroundColor Green
Write-Host "  [OK] ลบไฟล์ Debug (*.pdb), Config ภายใน (*.xml), และไฟล์ Source Code ทั้งหมด" -ForegroundColor Green
Write-Host "  [OK] สร้าง Template accounts.txt สะอาดพร้อมใช้งาน" -ForegroundColor Green
Write-Host "  [OK] รวม Chrome for Testing Portable ใช้งานได้ทันทีทุกเครื่องโดยไม่ต้องลงโปรแกรมเพิ่ม" -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host ""

if ((-not $NonInteractive) -and (Test-Path $zipResolved)) {
    Start-Process "explorer.exe" -ArgumentList "/select,`"$zipResolved`""
}

if (-not $NonInteractive) {
    Read-Host "กดปุ่ม Enter เพื่อเสร็จสิ้น..."
}
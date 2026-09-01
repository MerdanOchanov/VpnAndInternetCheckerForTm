# build.ps1 - reproducible build for InternetChecker (no Visual Studio / WiX needed).
# Compiles the exe with the in-box .NET Framework compiler, then builds:
#   - InternetChecker.exe (portable)
#   - InternetChecker-portable.zip
#   - InternetChecker.msi   (per-machine installer, closes running old version)
param(
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$obj  = Join-Path $PSScriptRoot "obj"
New-Item -ItemType Directory -Force $obj | Out-Null

# --- 0) icon ---
$icon = Join-Path $root "icon.ico"
if (-not (Test-Path $icon)) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "make-icon.ps1") | Out-Null
}

# --- 1) compile (embed the icon as the application icon) ---
$csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
& $csc /nologo /target:winexe /platform:x86 /optimize+ `
    /win32icon:"$icon" `
    /out:"$root\InternetChecker.exe" `
    /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Core.dll `
    "$root\Program.cs"
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
Write-Host "compiled InternetChecker.exe (with icon)"

# --- 2) portable zip ---
$pkg = Join-Path $obj "portable"
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory $pkg | Out-Null
Copy-Item "$root\InternetChecker.exe","$root\internetchecker.cfg","$root\README.md" $pkg
$zip = Join-Path $root "InternetChecker-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$pkg\*" -DestinationPath $zip
Write-Host "built $zip"

# --- 3) cab (members must match File keys in build-msi.vbs) ---
$cab = Join-Path $obj "app.cab"
if (Test-Path $cab) { Remove-Item $cab -Force }
$ddf = Join-Path $obj "app.ddf"
@"
.OPTION EXPLICIT
.Set CabinetNameTemplate=app.cab
.Set DiskDirectoryTemplate=$obj
.Set Cabinet=on
.Set Compress=on
.Set CompressionType=MSZIP
.Set MaxDiskSize=0
.Set InfFileName=$obj\app.inf
.Set RptFileName=$obj\app.rpt
"$root\InternetChecker.exe" "iccexe"
"$root\internetchecker.cfg" "icccfg"
"$root\README.md" "iccrdme"
"@ | Set-Content -Encoding ASCII $ddf
& makecab.exe /f $ddf | Out-Null
if (-not (Test-Path $cab)) { throw "cab build failed" }
Write-Host "built cab"

# --- 4) msi ---
$msi = Join-Path $root "InternetChecker.msi"
cscript //nologo "$PSScriptRoot\build-msi.vbs" $msi $cab $root $Version $icon
if ($LASTEXITCODE -ne 0) { throw "msi build failed" }
Write-Host "built $msi"
Get-ChildItem "$root\InternetChecker.exe","$root\InternetChecker.msi","$zip" | Select-Object Name,Length

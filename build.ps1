# Builds DisplayTuner.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK or Visual Studio needed.
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $out | Out-Null
& $csc /nologo /target:winexe /platform:x64 /optimize `
    "/win32icon:$root\src\DisplayTuner.ico" "/out:$out\DisplayTuner.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:Microsoft.CSharp.dll /r:System.Core.dll `
    "$root\src\DisplayTuner.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built $out\DisplayTuner.exe"

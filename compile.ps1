$csc = "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
$fw  = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
$src = Join-Path $PSScriptRoot "ClaudePusher.cs"
$out = Join-Path $PSScriptRoot "ClaudePusher.exe"
$mp3 = Join-Path $PSScriptRoot "..\VoicePusher\mp3sharp_src\MP3Sharp"
$ico = Join-Path $PSScriptRoot "claude_icon.ico"

Remove-Item $out -Force -ErrorAction SilentlyContinue

$mp3src = Get-ChildItem -Path $mp3 -Recurse -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch "\\IO\\" } |
    ForEach-Object { $_.FullName }

$result = & $csc /target:winexe /langversion:latest `
    /r:"$fw\System.dll" `
    /r:"$fw\System.Core.dll" `
    /r:"$fw\System.Windows.Forms.dll" `
    /r:"$fw\System.Drawing.dll" `
    /r:"$fw\System.Web.Extensions.dll" `
    /win32icon:$ico `
    /out:$out $src @mp3src 2>&1

Write-Host $result

if ($result | Where-Object { $_ -match ": error" }) { Write-Host "FAILED" -ForegroundColor Red; exit 1 }
elseif (Test-Path $out) { Write-Host "SUCCESS: $out" -ForegroundColor Green }
else { Write-Host "FAILED: exe not created" -ForegroundColor Red; exit 1 }

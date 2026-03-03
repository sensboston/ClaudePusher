$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $PSScriptRoot "ClaudePusher.cs"
$out = Join-Path $PSScriptRoot "ClaudePusher.exe"

Remove-Item $out -Force -ErrorAction SilentlyContinue

$ico = Join-Path $PSScriptRoot "claude_icon.ico"

$result = & $csc /target:winexe `
    /r:System.Web.Extensions.dll `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    /win32icon:$ico `
    /out:$out $src 2>&1

$errors = $result | Where-Object { $_ -match ": error" }
Write-Host $result

if ($errors)      { Write-Host "FAILED"; exit 1 }
elseif (Test-Path $out) { Write-Host "SUCCESS: $out" }
else              { Write-Host "FAILED: exe not created"; exit 1 }

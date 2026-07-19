$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet publish (Join-Path $here 'WorkBuddyAutoClaim.csproj') -c Release -o (Join-Path $here 'release') --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
Copy-Item (Join-Path $here 'config.example.json') (Join-Path $here 'release\config.example.json') -Force
Write-Host 'Build complete:' (Join-Path $here 'release\WorkBuddyAutoClaim.exe')

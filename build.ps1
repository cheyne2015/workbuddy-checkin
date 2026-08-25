$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet publish (Join-Path $here 'WorkBuddyAutoClaim.csproj') -c Release -o (Join-Path $here 'release') --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
Copy-Item (Join-Path $here 'config.example.json') (Join-Path $here 'release\config.example.json') -Force

$releaseDirectory = Join-Path $here 'release'
$artifactDirectory = Join-Path $here 'artifacts'
$packageName = 'WorkBuddyAutoClaim-recovery-hardening'
$packagePath = Join-Path $artifactDirectory ($packageName + '.zip')
$temporaryPackagePath = Join-Path $artifactDirectory ($packageName + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$backupPackagePath = Join-Path $artifactDirectory ($packageName + '.' + [Guid]::NewGuid().ToString('N') + '.previous.tmp')
$rootFiles = @('install.cmd', 'uninstall.cmd', 'README.md')
$releaseFiles = @(
    'CommunityToolkit.WinUI.Notifications.dll',
    'config.example.json',
    'Microsoft.Windows.SDK.NET.dll',
    'WinRT.Runtime.dll',
    'workbuddy-ocr.ps1',
    'WorkBuddyAutoClaim.deps.json',
    'WorkBuddyAutoClaim.dll',
    'WorkBuddyAutoClaim.exe',
    'WorkBuddyAutoClaim.runtimeconfig.json'
)

foreach ($file in $releaseFiles) {
    $source = Join-Path $releaseDirectory $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required release file is missing: $source"
    }
}
foreach ($file in $rootFiles) {
    $source = Join-Path $here $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required package file is missing: $source"
    }
}

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $archive = [System.IO.Compression.ZipFile]::Open($temporaryPackagePath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $rootFiles) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, (Join-Path $here $file), "$packageName/$file",
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        foreach ($file in $releaseFiles) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, (Join-Path $releaseDirectory $file), "$packageName/release/$file",
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
    if (Test-Path -LiteralPath $packagePath -PathType Leaf) {
        [System.IO.File]::Replace($temporaryPackagePath, $packagePath, $backupPackagePath)
        try { Remove-Item -LiteralPath $backupPackagePath -Force }
        catch { Write-Warning "New package is valid, but the previous package backup could not be removed: $backupPackagePath" }
    }
    else {
        [System.IO.File]::Move($temporaryPackagePath, $packagePath)
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryPackagePath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPackagePath -Force
    }
}

Write-Host 'Build complete:' (Join-Path $releaseDirectory 'WorkBuddyAutoClaim.exe')
Write-Host 'Installable package:' $packagePath

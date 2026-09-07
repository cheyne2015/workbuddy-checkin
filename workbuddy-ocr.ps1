param(
    [string]$ImagePath,
    [string]$Language = 'profile',
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Media.Ocr, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]

$asTaskDefinition = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and
        $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } |
    Select-Object -First 1

function Await-WinRt([object]$Operation, [Type]$ResultType) {
    $task = $asTaskDefinition.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait()
    return $task.Result
}

function Invoke-ImageOcr([string]$Path, [string]$RequestedLanguage) {
    $file = Await-WinRt ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
    $stream = Await-WinRt ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $bitmap = $null
    try {
        $decoder = Await-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await-WinRt ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $actualLanguage = $RequestedLanguage
        $engine = if ($RequestedLanguage -eq 'profile') {
            [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        } else {
            [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($RequestedLanguage))
        }
        # The cropped numeric retry prefers en-US, but that OCR language pack is optional.
        # Fall back to the logged-in user's installed OCR languages before giving up.
        if ($null -eq $engine -and $RequestedLanguage -ne 'profile') {
            $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
            $actualLanguage = 'profile-fallback'
        }
        if ($null -eq $engine) { throw 'Windows OCR is unavailable for the current user profile.' }
        $result = Await-WinRt ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        $lines = @($result.Lines | ForEach-Object {
            $words = @($_.Words | ForEach-Object {
                [pscustomobject]@{
                    Text = $_.Text
                    X = [int]$_.BoundingRect.X
                    Y = [int]$_.BoundingRect.Y
                    Width = [int]$_.BoundingRect.Width
                    Height = [int]$_.BoundingRect.Height
                }
            })
            [pscustomobject]@{
                Text = ($words.Text -join '')
                Words = $words
            }
        })
        return [pscustomobject]@{
            Language = $actualLanguage
            Lines = $lines
        }
    }
    finally {
        if ($null -ne $bitmap) { $bitmap.Dispose() }
        $stream.Dispose()
    }
}

if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    $requests = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $payloadObject = @()
    foreach ($request in $requests) {
        try { $payloadObject += Invoke-ImageOcr $request.ImagePath $request.Language }
        catch { throw "OCR batch item failed ($($request.ImagePath)): $($_.Exception.GetBaseException().Message)" }
    }
} else {
    if ([string]::IsNullOrWhiteSpace($ImagePath)) { throw 'ImagePath or ManifestPath is required.' }
    $payloadObject = Invoke-ImageOcr $ImagePath $Language
}

$payload = ConvertTo-Json -InputObject $payloadObject -Depth 5 -Compress
# The parent process receives only ASCII. This avoids Windows PowerShell 5
# code-page conversion corrupting Chinese OCR text in a redirected pipe.
[Console]::WriteLine([Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($payload)))

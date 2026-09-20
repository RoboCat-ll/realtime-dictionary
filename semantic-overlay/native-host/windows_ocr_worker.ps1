param()

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
[void][Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime]
[void][Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
[void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
[void][Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]

$asTaskMethod = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq "AsTask" -and
        $_.IsGenericMethod -and
        $_.GetParameters().Count -eq 1
    } |
    Select-Object -First 1

function Wait-WinRtResult {
    param(
        [Parameter(Mandatory = $true)]$Operation,
        [Parameter(Mandatory = $true)][Type]$ResultType
    )

    $task = $asTaskMethod.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait()
    return $task.Result
}

$language = New-Object Windows.Globalization.Language("zh-Hans-CN")
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
if ($null -eq $engine) {
    throw "Simplified Chinese Windows OCR is unavailable."
}

[PSCustomObject]@{
    ok = $true
    ready = $true
} | ConvertTo-Json -Compress
[Console]::Out.Flush()

while ($null -ne ($requestLine = [Console]::In.ReadLine())) {
    if ([string]::IsNullOrWhiteSpace($requestLine)) {
        continue
    }

    $request = $null
    try {
        $request = $requestLine | ConvertFrom-Json
        $imagePathBytes = [Convert]::FromBase64String([string]$request.image_path_base64)
        $imagePath = [Text.Encoding]::UTF8.GetString($imagePathBytes)
        $resolvedPath = (Resolve-Path -LiteralPath $imagePath).Path
        $totalWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $file = Wait-WinRtResult (
            [Windows.Storage.StorageFile]::GetFileFromPathAsync($resolvedPath)
        ) ([Windows.Storage.StorageFile])
        $stream = Wait-WinRtResult (
            $file.OpenAsync([Windows.Storage.FileAccessMode]::Read)
        ) ([Windows.Storage.Streams.IRandomAccessStream])

        try {
            $decodeWatch = [System.Diagnostics.Stopwatch]::StartNew()
            $decoder = Wait-WinRtResult (
                [Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)
            ) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap = Wait-WinRtResult (
                $decoder.GetSoftwareBitmapAsync()
            ) ([Windows.Graphics.Imaging.SoftwareBitmap])
            $decodeWatch.Stop()

            try {
                $recognizeWatch = [System.Diagnostics.Stopwatch]::StartNew()
                $result = Wait-WinRtResult (
                    $engine.RecognizeAsync($bitmap)
                ) ([Windows.Media.Ocr.OcrResult])
                $recognizeWatch.Stop()

                $words = foreach ($line in $result.Lines) {
                    foreach ($word in $line.Words) {
                        [PSCustomObject]@{
                            text = $word.Text
                            x = [Math]::Round($word.BoundingRect.X, 2)
                            y = [Math]::Round($word.BoundingRect.Y, 2)
                            w = [Math]::Round($word.BoundingRect.Width, 2)
                            h = [Math]::Round($word.BoundingRect.Height, 2)
                        }
                    }
                }

                [PSCustomObject]@{
                    ok = $true
                    request_id = [string]$request.request_id
                    decode_ms = [int]$decodeWatch.ElapsedMilliseconds
                    recognize_ms = [int]$recognizeWatch.ElapsedMilliseconds
                    worker_ms = [int]$totalWatch.ElapsedMilliseconds
                    words = @($words)
                } | ConvertTo-Json -Compress -Depth 4
            }
            finally {
                if ($null -ne $bitmap) {
                    $bitmap.Dispose()
                }
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        [PSCustomObject]@{
            ok = $false
            request_id = if ($null -eq $request) { $null } else { [string]$request.request_id }
            error = $_.Exception.Message
        } | ConvertTo-Json -Compress -Depth 3
    }
    [Console]::Out.Flush()
}

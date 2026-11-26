#Requires -Version 5.1
param([string]$SevenZip = '7z')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('wrg-build-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp, $artifacts -Force | Out-Null
$package = Join-Path $temp 'wrg-win-x64'

function Get-PinnedFile($Uri, $Name, $Hash) {
    $path = Join-Path $temp $Name
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $path
    if ((Get-FileHash -Algorithm SHA256 -Path $path).Hash -ne $Hash) {
        throw "SHA-256-Prüfung fehlgeschlagen für $Name."
    }
    return $path
}

Push-Location $root
try {
    dotnet publish src -c Release -r win-x64 --self-contained true -o $package
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish ist fehlgeschlagen.' }

    $poppler = Get-PinnedFile 'https://github.com/oschwartz10612/poppler-windows/releases/download/v26.09.0-0/Release-26.09.0-0.zip' 'poppler.zip' '7a6f256a0ddf7536182246a5733331bf4677cbcc34f4663774947ad34556c8d0'
    $ocr = Get-PinnedFile 'https://github.com/UB-Mannheim/tesseract/releases/download/v5.4.0.20240606/tesseract-ocr-w64-setup-5.4.0.20240606.exe' 'tesseract.exe' 'c885fff6998e0608ba4bb8ab51436e1c6775c2bafc2559a19b423e18678b60c9'
    $deu = Get-PinnedFile 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/deu.traineddata' 'deu.traineddata' '19d219bbb6672c869d20a9636c6816a81eb9a71796cb93ebe0cb1530e2cdb22d'
    $eng = Get-PinnedFile 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/eng.traineddata' 'eng.traineddata' '7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2'

    $runtimeLicense = Get-PinnedFile 'https://raw.githubusercontent.com/dotnet/runtime/v10.0.15/LICENSE.TXT' 'LICENSE.txt' 'cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310'
    $runtimeNotices = Get-PinnedFile 'https://raw.githubusercontent.com/dotnet/runtime/v10.0.15/THIRD-PARTY-NOTICES.TXT' 'ThirdPartyNotices.txt' '66f1d4e44973185519bb4aa8a9718eb22fc7af2cc532e3ae9cfc4c127ee7fc54'
    Copy-Item -LiteralPath $runtimeLicense, $runtimeNotices -Destination $package

    $extract = Join-Path $temp 'poppler'
    Expand-Archive -LiteralPath $poppler -DestinationPath $extract
    $tools = Join-Path $package 'tools'
    New-Item -ItemType Directory -Path $tools | Out-Null
    Move-Item -LiteralPath (Join-Path $extract 'poppler-26.09.0') -Destination (Join-Path $tools 'poppler')
    $tesseract = Join-Path $tools 'tesseract'
    & $SevenZip x $ocr "-o$tesseract" 'tesseract.exe' '*.dll' 'doc/*' '-x!$PLUGINSDIR/*' -y
    # 7-Zip reports warning 1 for the NSIS installer despite extracting the payload.
    if ($LASTEXITCODE -gt 1 -or !(Test-Path (Join-Path $tesseract 'tesseract.exe')) -or
        !(Test-Path (Join-Path $tesseract 'libtesseract-5.dll'))) {
        throw 'Tesseract konnte nicht vollständig entpackt werden.'
    }
    $tessdata = Join-Path $tesseract 'tessdata'
    New-Item -ItemType Directory -Path $tessdata | Out-Null
    Copy-Item -LiteralPath $deu, $eng -Destination $tessdata
    Copy-Item -LiteralPath 'README.md', 'LIZENZHINWEISE.md', 'JSONL.md', 'PLAN.md' -Destination $package
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        dotnet run --project tests -c Release -- (Join-Path $package 'wrg.exe') tests/fixtures/gemischt.pdf
        if ($LASTEXITCODE -ne 0) { throw 'Der Windows-Pakettest ist fehlgeschlagen.' }
    }
    $zip = Join-Path $artifacts 'wrg-win-x64.zip'
    Compress-Archive -Path $package -DestinationPath $zip -Force
    Write-Output $zip
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $temp -Recurse -Force
}

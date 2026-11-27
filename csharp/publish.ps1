param(
    [string]$Destination = '',
    [string]$FfmpegDirectory = '',
    [switch]$Update,
    [switch]$NoRestore,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$bundleFiles = @{}
if ($FfmpegDirectory) {
    $ffmpegRoot = (Resolve-Path -LiteralPath $FfmpegDirectory).ProviderPath
    $ffmpegFiles = @{
        'ffmpeg.exe' = 'bin\ffmpeg.exe'
        'ffprobe.exe' = 'bin\ffprobe.exe'
        'FFmpeg-LICENSE.txt' = 'LICENSE'
        'FFmpeg-BUILD.txt' = 'README.txt'
    }
    foreach ($name in $ffmpegFiles.Keys) {
        $source = Join-Path $ffmpegRoot $ffmpegFiles[$name]
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Missing FFmpeg file: $source. Provide the root of an extracted Gyan Windows build."
        }
        $bundleFiles[$name] = $source
    }
    $ffmpegSource = Join-Path $repositoryRoot 'third_party\ffmpeg'
    if (-not (Test-Path -LiteralPath (Join-Path $ffmpegSource 'ffmpeg-469aad3897.tar.gz') -PathType Leaf)) {
        throw 'The bundled FFmpeg source archive is missing from third_party/ffmpeg.'
    }
    $expectedSourceHash = (Get-Content -LiteralPath (Join-Path $ffmpegSource 'SHA256SUMS') -TotalCount 1).Split(' ')[0]
    $actualSourceHash = (Get-FileHash -LiteralPath (Join-Path $ffmpegSource 'ffmpeg-469aad3897.tar.gz') -Algorithm SHA256).Hash
    if ($expectedSourceHash -ne $actualSourceHash) {
        throw 'The FFmpeg source archive does not match its recorded SHA256 checksum.'
    }
    if (-not (Select-String -LiteralPath $bundleFiles['FFmpeg-BUILD.txt'] -SimpleMatch '469aad3897' -Quiet)) {
        throw 'Update third_party/ffmpeg to match the selected FFmpeg build before publishing.'
    }
}
if (-not $Destination) {
    $releaseName = if ($FfmpegDirectory) { 'Wallpaper2Video-win-x64-with-ffmpeg' } else { 'Wallpaper2Video-win-x64' }
    $Destination = Join-Path (Join-Path $repositoryRoot 'dist') $releaseName
}
$destinationPath = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
$archivePath = "$destinationPath.zip"
if (Test-Path -LiteralPath $destinationPath) {
    if (-not $Update) { throw 'Publish destination already exists. Choose a new folder or use -Update.' }
    if (-not (Test-Path -LiteralPath (Join-Path $destinationPath 'Wallpaper2Video.Desktop.runtimeconfig.json') -PathType Leaf)) {
        throw 'Update destination is not a Wallpaper2Video release folder.'
    }
    if (-not $FfmpegDirectory -and (Test-Path -LiteralPath (Join-Path $destinationPath 'FFmpeg-BUILD.txt'))) {
        throw 'Use -FfmpegDirectory to update a bundled release, or choose a different destination.'
    }
}
if ($Zip -and (Test-Path -LiteralPath $archivePath) -and -not $Update) { throw 'Release archive already exists.' }

$dotnet = Join-Path $repositoryRoot '.dotnet\dotnet.exe'
if (Test-Path -LiteralPath $dotnet) {
    $env:DOTNET_CLI_HOME = Join-Path $repositoryRoot 'output\toolchain\home'
    $env:NUGET_PACKAGES = Join-Path $repositoryRoot 'output\toolchain\packages'
} else {
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$desktop = Join-Path $PSScriptRoot 'src\Wallpaper2Video.Desktop'
$projects = @(
    (Join-Path $desktop 'Wallpaper2Video.Desktop.csproj'),
    (Join-Path $PSScriptRoot 'src\Wallpaper2Video.Cli\Wallpaper2Video.Cli.csproj')
)
$publishOptions = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--disable-build-servers', '-m:1',
    '-p:DebugType=None', '-p:DebugSymbols=false', "-p:PathMap=$repositoryRoot=/_/wallpaper2video")
if ($NoRestore) { $publishOptions += '--no-restore' }
else { $publishOptions += @('--source', 'https://api.nuget.org/v3/index.json') }

$destinationParent = [System.IO.Path]::GetDirectoryName($destinationPath)
$stagingRoot = Join-Path $destinationParent ('.publish-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stagingRoot ([System.IO.Path]::GetFileName($destinationPath))
$stagedArchive = "$payload.zip"
New-Item -ItemType Directory -Path $payload | Out-Null

Push-Location -LiteralPath $PSScriptRoot
try {
    foreach ($project in $projects) {
        & $dotnet publish $project @publishOptions -o $payload
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $project" }
    }

    $assets = Get-Content -LiteralPath (Join-Path $desktop 'obj\project.assets.json') -Raw | ConvertFrom-Json
    $runtime = Get-Content -LiteralPath (Join-Path $payload 'Wallpaper2Video.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $packageName = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
        $packageRoot = foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder "$packageName\$($framework.version)"
            if (Test-Path -LiteralPath $candidate -PathType Container) { $candidate; break }
        }
        if (-not $packageRoot) { throw "Runtime package not found for $($framework.name) $($framework.version)." }
        $notices = Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Name -Match '^(LICENSE(?:\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$'
        if (-not $notices) { throw "Runtime license files not found for $($framework.name)." }
        foreach ($notice in $notices) {
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $payload "$($framework.name).$($notice.Name)")
        }
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RELEASE-README.md') -Destination (Join-Path $payload 'README.md')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE'), (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $desktop 'Assets\Logo.png'), (Join-Path $desktop 'Assets\Logo.ico') -Destination $payload
    foreach ($name in $bundleFiles.Keys) {
        Copy-Item -LiteralPath $bundleFiles[$name] -Destination (Join-Path $payload $name)
    }
    if ($FfmpegDirectory) {
        Copy-Item -LiteralPath $ffmpegSource -Destination (Join-Path $payload 'FFmpeg-source') -Recurse
    }
    if ($Zip) { Compress-Archive -LiteralPath $payload -DestinationPath $stagedArchive -CompressionLevel Optimal }

    if ($Update -and (Test-Path -LiteralPath $destinationPath -PathType Container)) {
        foreach ($file in Get-ChildItem -LiteralPath $payload -Force) {
            Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Recurse -Force
        }
        $legacyLauncher = Join-Path $destinationPath 'Start.cmd'
        if (Test-Path -LiteralPath $legacyLauncher -PathType Leaf) {
            $previousLauncher = @'
@echo off
cd /d "%~dp0"
start "" "%~dp0Wallpaper2Video.Desktop.exe" %*
'@
            if ((Get-Content -LiteralPath $legacyLauncher -Raw).Replace("`r", '').TrimEnd() -eq $previousLauncher.Replace("`r", '')) {
                Remove-Item -LiteralPath $legacyLauncher
            }
        }
    } else {
        [System.IO.Directory]::Move($payload, $destinationPath)
    }
    Write-Output "Published: $destinationPath"

    if ($Zip) {
        Move-Item -LiteralPath $stagedArchive -Destination $archivePath -Force:$Update
        Write-Output "Archive: $archivePath"
    }
}
finally {
    Pop-Location
    $resolved = (Resolve-Path -LiteralPath $stagingRoot -ErrorAction SilentlyContinue).ProviderPath
    if ($resolved -and [System.IO.Path]::GetDirectoryName($resolved) -eq $destinationParent -and
        [System.IO.Path]::GetFileName($resolved) -match '^\.publish-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

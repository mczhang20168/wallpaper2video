param([string]$Configuration = 'Release', [string]$OutputRoot = '', [switch]$TestWallpapers, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sdkPath = Join-Path $repositoryRoot '.dotnet\dotnet.exe'
if (Test-Path -LiteralPath $sdkPath) {
    $env:DOTNET_CLI_HOME = Join-Path $repositoryRoot 'output\toolchain\home'
    $env:NUGET_PACKAGES = Join-Path $repositoryRoot 'output\toolchain\packages'
} else {
    $sdkPath = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not $NoRestore) {
        & $sdkPath restore Wallpaper2Video.sln --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    }
    $buildOptions = @()
    if ($OutputRoot) { $OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot); $buildOptions += "-p:BaseOutputPath=$OutputRoot\" }
    & $sdkPath build Wallpaper2Video.sln -c $Configuration --no-restore --disable-build-servers @buildOptions
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $compatibilityTests = if ($OutputRoot) { Join-Path $OutputRoot "$Configuration\net10.0\Wallpaper2Video.CompatibilityTests.dll" } else { "tests\Wallpaper2Video.CompatibilityTests\bin\$Configuration\net10.0\Wallpaper2Video.CompatibilityTests.dll" }
    & $sdkPath $compatibilityTests
    if ($LASTEXITCODE -ne 0) { throw 'Compatibility checks failed.' }
    $workspaceTests = if ($OutputRoot) { Join-Path $OutputRoot "$Configuration\net10.0-windows\Wallpaper2Video.WorkspaceTests.dll" } else { "tests\Wallpaper2Video.WorkspaceTests\bin\$Configuration\net10.0-windows\Wallpaper2Video.WorkspaceTests.dll" }
    $testOptions = @((Join-Path $repositoryRoot 'output\workspace-checks'))
    if ($TestWallpapers) { $testOptions += '--wallpapers' }
    & $sdkPath $workspaceTests @testOptions
    if ($LASTEXITCODE -ne 0) { throw 'Workspace checks failed.' }
}
finally { Pop-Location }

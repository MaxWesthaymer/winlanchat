[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string] $Version,
    [string] $InnoSetupCompiler
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectRoot 'src\WinLanChat.App\WinLanChat.App.csproj'
if (-not $Version) {
    [xml] $projectXml = Get-Content -LiteralPath $appProject -Raw
    $versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $versionNode) { throw 'The application project must declare its Version.' }
    $Version = $versionNode.InnerText
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Invalid application version.' }
$outputDirectory = Join-Path $projectRoot 'artifacts\installer'
# Each build gets a fresh directory so files from older publishes cannot enter the installer.
$publishDirectory = Join-Path $projectRoot ('artifacts\publish\installer-' + [guid]::NewGuid().ToString('N'))

if (-not $InnoSetupCompiler) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $compilerCandidates = @(
        $(if ($compilerCommand) { $compilerCommand.Source })
        (Join-Path $projectRoot 'artifacts\tools\inno-setup\ISCC.exe')
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $InnoSetupCompiler = $compilerCandidates |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1
}
if (-not $InnoSetupCompiler -or -not (Test-Path -LiteralPath $InnoSetupCompiler -PathType Leaf)) {
    throw 'Install Inno Setup 6.3 or newer, or pass -InnoSetupCompiler with the full path to ISCC.exe. See installer/README.md.'
}
$InnoSetupCompiler = (Resolve-Path -LiteralPath $InnoSetupCompiler).Path

& dotnet publish $appProject -c Release -r win-x64 --self-contained true `
    '-p:UseAppHost=true' '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' `
    '-p:DebugType=None' '-p:DebugSymbols=false' "-p:Version=$Version" -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'WinLanChat.App.runtimeconfig.json') -Raw |
    ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.frameworks -or $runtimeConfig.runtimeOptions.framework -or
    -not $runtimeConfig.runtimeOptions.includedFrameworks) {
    throw 'The installer requires self-contained output with an embedded runtime.'
}
$desktopVersion = ($runtimeConfig.runtimeOptions.includedFrameworks |
    Where-Object name -EQ 'Microsoft.WindowsDesktop.App').version
$coreVersion = ($runtimeConfig.runtimeOptions.includedFrameworks |
    Where-Object name -EQ 'Microsoft.NETCore.App').version
if (-not $desktopVersion -or -not $coreVersion) { throw 'Required framework versions are missing.' }
foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $runtimeFile))) {
        throw "Missing embedded runtime file: $runtimeFile"
    }
}

& $InnoSetupCompiler "/DPublishDir=$publishDirectory" "/DAppVersion=$Version" `
    "/DOutputDir=$outputDirectory" (Join-Path $projectRoot 'installer\WinLanChat.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }

Get-Item -LiteralPath (Join-Path $outputDirectory "WinLanChat-$Version-win-x64-Setup.exe")

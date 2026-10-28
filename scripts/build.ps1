#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$BuildDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
$OutputDirectory = (Resolve-RepoBuildPath $OutputDirectory)
$BuildDirectory = (Resolve-RepoBuildPath $BuildDirectory)
$localModUi = Join-Path $RepoRoot 'vendor/LocalModUI'
$localModUiBuildScript = Join-Path $localModUi 'scripts/build.ps1'
if (-not (Test-Path -LiteralPath $localModUiBuildScript -PathType Leaf)) {
    throw 'LocalModUI submodule is missing. Run git submodule update --init --recursive.'
}
& $localModUiBuildScript -GameRoot $GameRoot -BuildDirectory (Join-Path $localModUi '.tmp/build') -OutputDirectory (Join-Path $localModUi '.tmp/package') | Out-Null

if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') {
    throw 'RoomCalibration targets Beat Saber 1.45.2_28848'
}
$dotnet = 'C:/Program Files/dotnet/dotnet.exe'
$compiler = Get-ChildItem -LiteralPath 'C:/Program Files/dotnet/sdk' -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'Roslyn/bincore/csc.dll' }
if (!$compiler -or !(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'A local .NET SDK with the Roslyn compiler is required.' }
$source = Join-Path $PSScriptRoot 'room_calibration'
$managed = Join-Path $GameRoot 'Beat Saber_Data/Managed'
$references = @('mscorlib.dll','System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll','IPA.Loader.dll','Main.dll','DataModels.dll','HMLib.dll','BeatSaber.GameSettings.dll','BeatSaber.Settings.dll',
    'BGLib.AppFlow.dll','BGLib.FileStorage.dll','BGLib.UnityExtension.dll','BGLib.Polyglot.dll','Unity.Mathematics.dll',
    'UnityEngine.CoreModule.dll','UnityEngine.SubsystemsModule.dll','UnityEngine.XRModule.dll','UnityEngine.InputLegacyModule.dll',
    'UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','Unity.TextMeshPro.dll','HMUI.dll','BeatSaber.ViewSystem.dll',
    'Zenject.dll','Zenject-usage.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$references += Join-Path $localModUi '.tmp/local-mod-ui/build/LocalModUI.dll'
$references += @('Plugins/BSML.dll','Plugins/SiraUtil.dll','Libs/0Harmony.dll') | ForEach-Object { Join-Path $GameRoot $_ }
foreach ($reference in $references) { if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing reference: $reference" } }
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
$plugins = Join-Path $OutputDirectory 'Plugins'
[IO.Directory]::CreateDirectory($plugins) | Out-Null
$output = Join-Path $BuildDirectory 'RoomCalibration.dll'
$manifestPath = Join-Path $source 'RoomCalibration.manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$arguments = @('/nologo','/noconfig','/nostdlib+','/target:library','/optimize+','/warn:4','/langversion:latest','/codepage:65001',
    ('/out:'+$output),('/resource:'+$manifestPath+',RoomCalibration.manifest.json'),
    ('/resource:'+(Join-Path $source 'Settings.bsml')+',RoomCalibration.Settings.bsml'))
$arguments += $references | ForEach-Object { '/r:'+$_ }
$arguments += Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName }
& $dotnet $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'RoomCalibration compilation failed' }
$release = Join-Path $plugins 'RoomCalibration.dll'
Copy-Item -LiteralPath $output -Destination $release
$hash = (Get-FileHash -LiteralPath $release -Algorithm SHA256).Hash.ToLowerInvariant()
$provenance = [ordered]@{
    plugin='RoomCalibration'; version=$manifest.version; gameBuild='1.45.2_28848'; sha256=$hash
    references=@($references | ForEach-Object { [ordered]@{
        path=[IO.Path]::GetRelativePath($GameRoot,$_).Replace('\','/')
        sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    } })
}
$provenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json') -Encoding utf8NoBOM
$hash+'  Plugins/RoomCalibration.dll' | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Output $release

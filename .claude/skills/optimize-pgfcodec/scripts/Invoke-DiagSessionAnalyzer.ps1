[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $EtwPath,
    [Parameter(Mandatory)]
    [Alias('Pid')]
    [ValidateRange(1, 2147483647)]
    [int] $WorkerPid,
    [ValidateRange(1, 1000)]
    [int] $Top = 50,
    [ValidateRange(1, 3600)]
    [int] $TimeoutSeconds = 30,
    [ValidateRange(0, 10240)]
    [int] $SkipSizeMb = 50,
    [string] $AnalyzerRoot = (Join-Path $env:TEMP 'DiagSessionAnalyzer-7009ecb'),
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

$etl = (Resolve-Path -LiteralPath $EtwPath -ErrorAction Stop).Path
$project = Join-Path $AnalyzerRoot 'DiagSessionAnalyzer.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw "Analyzer project not found at '$project'. Run Install-DiagSessionAnalyzer.ps1 first."
}

$arguments = @(
    'run', '--project', $project, '--configuration', 'Release', '--no-restore', '--',
    $etl, '--pid', $WorkerPid, '--top', $Top, '--timeout', $TimeoutSeconds, '--skip-size', $SkipSizeMb
)

if ($OutputPath) {
    $outputDirectory = Split-Path -Parent $OutputPath
    if ($outputDirectory) { New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null }
    & dotnet @arguments 2>&1 | Tee-Object -FilePath $OutputPath
} else {
    & dotnet @arguments
}

exit $LASTEXITCODE

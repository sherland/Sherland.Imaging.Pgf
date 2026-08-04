<#!
.SYNOPSIS
Archives one PGF BenchmarkDotNet run as version-controlled benchmark evidence.

.DESCRIPTION
Copies the raw Markdown, CSV, and HTML BenchmarkDotNet reports from an explicit --artifacts
directory to docs/benchmarks/pgfcodec/<RunName>/ and writes a metadata.json manifest. The
destination must not exist: a benchmark run is immutable evidence, not a file to overwrite.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ArtifactDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}-[0-9a-f]{7,64}(?:-[a-z0-9]+)*$')]
    [string] $RunName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{7,64}$')]
    [string] $Revision,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Purpose,

    [string] $Command = 'dotnet run -c Release --project source/PictTag.PgfCodec.Benchmarks -- --job short --filter "*" --artifacts <directory>'
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$source = Join-Path (Resolve-Path -LiteralPath $ArtifactDirectory) 'results'
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "BenchmarkDotNet results directory was not found: $source"
}

$destination = Join-Path $repoRoot "docs/benchmarks/pgfcodec/$RunName"
if (Test-Path -LiteralPath $destination) {
    throw "Benchmark archive already exists and will not be overwritten: $destination"
}

$reports = Get-ChildItem -LiteralPath $source -File |
    Where-Object { $_.Name -match '^PictTag\.PgfCodec\.Benchmarks\..+-report(?:-github)?\.(csv|md|html)$' }
if ($reports.Count -ne 9) {
    throw "Expected 9 BenchmarkDotNet report files (three formats for each of three benchmark classes); found $($reports.Count)."
}

New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($report in $reports) {
    Copy-Item -LiteralPath $report.FullName -Destination $destination
}

$metadata = [ordered]@{
    runName = $RunName
    revision = $Revision
    purpose = $Purpose
    archivedAtUtc = [DateTime]::UtcNow.ToString('O')
    benchmarkCommand = $Command
    sourceArtifactDirectory = $ArtifactDirectory
    reports = @($reports.Name | Sort-Object)
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'metadata.json') -Encoding utf8

Write-Host "Archived $($reports.Count) reports to $destination"

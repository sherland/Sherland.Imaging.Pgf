[CmdletBinding()]
param(
    [string] $InstallPath = (Join-Path $env:TEMP 'DiagSessionAnalyzer-7009ecb'),
    [string] $Revision = '7009ecba36c2658994a95cf4b8ade38718968256'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'git is required to install DiagSessionAnalyzer.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet is required to build DiagSessionAnalyzer.'
}

$project = Join-Path $InstallPath 'DiagSessionAnalyzer.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    if (Test-Path -LiteralPath $InstallPath) {
        $existingEntries = @(Get-ChildItem -LiteralPath $InstallPath -Force)
        if ($existingEntries.Count -gt 0) {
            throw "Install path '$InstallPath' exists but is not a DiagSessionAnalyzer checkout. Choose an empty path or remove it manually."
        }
    }
    $parent = Split-Path -Parent $InstallPath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    git clone 'https://github.com/bivex/DiagSessionAnalyzer.git' $InstallPath
    if ($LASTEXITCODE -ne 0) { throw "git clone failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath (Join-Path $InstallPath '.git'))) {
    throw "Analyzer path '$InstallPath' is missing its .git directory. Run the setup script with a fresh checkout path."
}

git -C $InstallPath fetch --quiet origin $Revision
if ($LASTEXITCODE -ne 0) { throw "git fetch failed with exit code $LASTEXITCODE." }
git -C $InstallPath checkout --quiet --detach $Revision
if ($LASTEXITCODE -ne 0) { throw "git checkout failed with exit code $LASTEXITCODE." }

dotnet build $project --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

Write-Output (Resolve-Path -LiteralPath $project).Path

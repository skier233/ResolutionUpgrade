param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$DestinationRoot = (Join-Path $env:LOCALAPPDATA "cove_personal_live\extensions"),
    [switch]$SkipUi
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectDir = Join-Path $repoRoot "src\ResolutionUpgrade"

if (-not $SkipUi) {
    Write-Host "Building the UI bundle..."
    Push-Location $repoRoot
    try {
        npm run build:ui
        if ($LASTEXITCODE -ne 0) { throw "npm run build:ui failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
}

Write-Host "Building ResolutionUpgrade ($Configuration)..."
dotnet build (Join-Path $projectDir "ResolutionUpgrade.csproj") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$outputDir = Join-Path $projectDir "bin\$Configuration\net10.0"
$manifestPath = Join-Path $outputDir "extension.json"
if (-not (Test-Path $manifestPath)) { throw "Missing manifest at $manifestPath" }
if (-not (Test-Path (Join-Path $outputDir "dist\bundle.js"))) { throw "Missing UI bundle in $outputDir\dist; run without -SkipUi" }

$manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
$targetDir = Join-Path $DestinationRoot $manifest.id
if (Test-Path $targetDir) { Remove-Item -Path $targetDir -Recurse -Force }
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Copy-Item -Path (Join-Path $outputDir "*") -Destination $targetDir -Recurse -Force

Write-Host ("Staged {0} -> {1}" -f $manifest.id, $targetDir)
Write-Host "Restart Cove to load the extension if it is already running."

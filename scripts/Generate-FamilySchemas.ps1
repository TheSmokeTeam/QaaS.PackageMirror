param(
    [string]$MirrorRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = '',
    [string]$SnapshotId = '',
    [string]$TriggerRepository = '',
    [string]$TriggerTag = '',
    [string]$TriggerRunId = '',
    [string]$TriggerOrigin = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $MirrorRoot 'schemas'
}

if ([string]::IsNullOrWhiteSpace($SnapshotId)) {
    $SnapshotId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
}

$generatorProject = Join-Path $MirrorRoot 'QaaS.PackageMirror.FamilySchemas\QaaS.PackageMirror.FamilySchemas.csproj'
if (-not (Test-Path $generatorProject)) {
    throw "Schema generator project not found at $generatorProject"
}

function Get-LatestFamilyPackageVersion {
    param(
        [string]$PackagesRoot,
        [string]$PackageId
    )

    $packageDirectory = Join-Path $PackagesRoot ("packages\qaas\" + $PackageId.ToLowerInvariant())
    if (-not (Test-Path $packageDirectory)) {
        throw "Could not find mirrored package directory for $PackageId at $packageDirectory"
    }

    $versions = Get-ChildItem -Path $packageDirectory -Directory | Sort-Object Name -Descending
    if ($versions.Count -eq 0) {
        throw "No versions found for mirrored package $PackageId"
    }

    return $versions[0].Name
}

function Invoke-FamilySchemaGeneration {
    param(
        [string]$FamilyId,
        [hashtable]$PackageVersions,
        [string]$PackagesRoot,
        [string]$GeneratorProject,
        [string]$OutputRoot,
        [string]$SnapshotId,
        [string]$TriggerRepository,
        [string]$TriggerTag,
        [string]$TriggerRunId,
        [string]$TriggerOrigin
    )

    $arguments = @(
        'run',
        '--project', $GeneratorProject,
        '--configuration', 'Release',
        '--no-launch-profile',
        '--',
        '--family', $FamilyId,
        '--packages-root', $PackagesRoot,
        '--output-root', $OutputRoot,
        '--snapshot-id', $SnapshotId
    )

    if (-not [string]::IsNullOrWhiteSpace($TriggerRepository)) {
        $arguments += @('--trigger-repo', $TriggerRepository)
    }
    if (-not [string]::IsNullOrWhiteSpace($TriggerTag)) {
        $arguments += @('--trigger-tag', $TriggerTag)
    }
    if (-not [string]::IsNullOrWhiteSpace($TriggerRunId)) {
        $arguments += @('--trigger-run-id', $TriggerRunId)
    }
    if (-not [string]::IsNullOrWhiteSpace($TriggerOrigin)) {
        $arguments += @('--trigger-origin', $TriggerOrigin)
    }

    foreach ($package in $PackageVersions.GetEnumerator() | Sort-Object Name) {
        $arguments += @('--package', "$($package.Name)=$($package.Value)")
    }

    dotnet @arguments | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to generate schema for $FamilyId"
    }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$families = @(
    @{
        Id = 'runner-family'
        Packages = @(
            'QaaS.Runner',
            'QaaS.Common.Generators',
            'QaaS.Common.Assertions',
            'QaaS.Common.Probes'
        )
    },
    @{
        Id = 'mocker-family'
        Packages = @(
            'QaaS.Mocker',
            'QaaS.Common.Generators',
            'QaaS.Common.Processors'
        )
    }
)

foreach ($family in $families) {
    $familyOutputRoot = Join-Path $OutputRoot $family.Id
    if (Test-Path $familyOutputRoot) {
        Remove-Item $familyOutputRoot -Recurse -Force
    }
}

$indexPath = Join-Path $OutputRoot 'index.json'
if (Test-Path $indexPath) {
    Remove-Item $indexPath -Force
}

$packagesRoot = Join-Path $MirrorRoot 'packages'

foreach ($family in $families) {
    $packageVersions = @{}
    foreach ($packageId in $family.Packages) {
        $packageVersions[$packageId] = Get-LatestFamilyPackageVersion -PackagesRoot $MirrorRoot -PackageId $packageId
    }

    Invoke-FamilySchemaGeneration `
        -FamilyId $family.Id `
        -PackageVersions $packageVersions `
        -PackagesRoot $packagesRoot `
        -GeneratorProject $generatorProject `
        -OutputRoot $OutputRoot `
        -SnapshotId $SnapshotId `
        -TriggerRepository $TriggerRepository `
        -TriggerTag $TriggerTag `
        -TriggerRunId $TriggerRunId `
        -TriggerOrigin $TriggerOrigin
}

Write-Host "Generated family schemas into $OutputRoot"

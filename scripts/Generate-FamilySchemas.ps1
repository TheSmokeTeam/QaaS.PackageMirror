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

$temporaryRoot = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [System.IO.Path]::GetTempPath()
}
else {
    $env:RUNNER_TEMP
}

$workingRoot = Join-Path $temporaryRoot ("qaas-family-schemas-" + [Guid]::NewGuid().ToString('N'))
$feedRoot = Join-Path $workingRoot 'feed'
$loadersRoot = Join-Path $workingRoot 'loaders'

function Copy-MirroredPackagesToFeed {
    param(
        [string]$PackagesRoot,
        [string]$DestinationRoot
    )

    Get-ChildItem -Path $PackagesRoot -Filter *.nupkg -Recurse -File |
        Where-Object { $_.Extension -eq '.nupkg' } |
        ForEach-Object {
            Copy-Item $_.FullName (Join-Path $DestinationRoot $_.Name) -Force
        }
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

function New-LoaderProject {
    param(
        [string]$FamilyId,
        [hashtable]$PackageVersions,
        [string]$LoadersRoot,
        [string]$FeedRoot
    )

    $loaderDirectory = Join-Path $LoadersRoot $FamilyId
    New-Item -ItemType Directory -Path $loaderDirectory -Force | Out-Null

    $packageReferenceLines = $PackageVersions.GetEnumerator() |
        Sort-Object Name |
        ForEach-Object { "    <PackageReference Include=`"$($_.Name)`" Version=`"$($_.Value)`" />" }

    $projectPath = Join-Path $loaderDirectory "$FamilyId.Loader.csproj"
    $projectContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
$($packageReferenceLines -join [Environment]::NewLine)
  </ItemGroup>
</Project>
"@

    Set-Content -Path $projectPath -Value $projectContent
    Set-Content -Path (Join-Path $loaderDirectory 'Program.cs') -Value 'Console.WriteLine("family-schema-loader");'

    dotnet restore $projectPath `
        --source $FeedRoot `
        --nologo `
        --verbosity minimal | Write-Host

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to restore loader project for $FamilyId"
    }

    dotnet build $projectPath `
        --configuration Release `
        --no-restore `
        --nologo `
        --verbosity minimal | Write-Host

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build loader project for $FamilyId"
    }

    return Join-Path $loaderDirectory "bin\Release\net10.0\$FamilyId.Loader.dll"
}

function Invoke-FamilySchemaGeneration {
    param(
        [string]$FamilyId,
        [hashtable]$PackageVersions,
        [string]$ResolverAppPath,
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
        '--resolver-app', $ResolverAppPath,
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

try {
    New-Item -ItemType Directory -Path $feedRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $loadersRoot -Force | Out-Null
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

    Copy-MirroredPackagesToFeed -PackagesRoot (Join-Path $MirrorRoot 'packages') -DestinationRoot $feedRoot

    foreach ($family in $families) {
        $packageVersions = @{}
        foreach ($packageId in $family.Packages) {
            $packageVersions[$packageId] = Get-LatestFamilyPackageVersion -PackagesRoot $MirrorRoot -PackageId $packageId
        }

        $resolverAppPath = New-LoaderProject `
            -FamilyId $family.Id `
            -PackageVersions $packageVersions `
            -LoadersRoot $loadersRoot `
            -FeedRoot $feedRoot

        Invoke-FamilySchemaGeneration `
            -FamilyId $family.Id `
            -PackageVersions $packageVersions `
            -ResolverAppPath $resolverAppPath `
            -GeneratorProject $generatorProject `
            -OutputRoot $OutputRoot `
            -SnapshotId $SnapshotId `
            -TriggerRepository $TriggerRepository `
            -TriggerTag $TriggerTag `
            -TriggerRunId $TriggerRunId `
            -TriggerOrigin $TriggerOrigin
    }
    Write-Host "Generated family schemas into $OutputRoot"
}
finally {
    if (Test-Path $workingRoot) {
        Remove-Item $workingRoot -Recurse -Force
    }
}

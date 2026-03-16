param(
    [string]$WorkspaceRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)]
    [string]$GitHubRepository,
    [string]$BranchName = 'master',
    [string]$ReleaseTag = '',
    [string]$ReleaseTagPrefix = 'mirror',
    [string]$GitHubToken = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

if (-not $SkipPublish -and [string]::IsNullOrWhiteSpace($GitHubToken)) {
    throw 'GitHubToken is required unless -SkipPublish is used.'
}

$packagesRoot = Join-Path $WorkspaceRoot 'packages'
$qaasPackagesRoot = Join-Path $packagesRoot 'qaas'
$notQaasPackagesRoot = Join-Path $packagesRoot 'not-qaas'
$schemasRoot = Join-Path $WorkspaceRoot 'schemas'
$stateRoot = Join-Path $WorkspaceRoot 'state'

if (-not (Test-Path $qaasPackagesRoot)) {
    throw "Missing QaaS packages directory at $qaasPackagesRoot"
}

if (-not (Test-Path $notQaasPackagesRoot)) {
    throw "Missing non-QaaS packages directory at $notQaasPackagesRoot"
}

$runnerSchemaPath = Join-Path $schemasRoot 'runner-family/latest/schema.json'
$mockerSchemaPath = Join-Path $schemasRoot 'mocker-family/latest/schema.json'

if (-not (Test-Path $runnerSchemaPath)) {
    throw "Missing runner schema at $runnerSchemaPath"
}

if (-not (Test-Path $mockerSchemaPath)) {
    throw "Missing mocker schema at $mockerSchemaPath"
}

$israelTimeZone = [TimeZoneInfo]::FindSystemTimeZoneById('Israel Standard Time')
$releaseTime = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $israelTimeZone)
$releaseName = $releaseTime.ToString('yyyy-MM-dd HH:mm:ss')
$releaseTag = if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    "$ReleaseTagPrefix-$($releaseTime.ToString('yyyyMMdd-HHmmss'))"
}
else {
    $ReleaseTag
}

$assetRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("qaas-package-mirror-release-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null

function New-ZipArchive {
    param(
        [string]$ParentDirectory,
        [string]$ChildDirectoryName,
        [string]$DestinationPath,
        [int]$RetryCount = 5,
        [int]$RetryDelaySeconds = 15
    )

    if (Test-Path $DestinationPath) {
        Remove-Item $DestinationPath -Force
    }

    for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
        tar.exe -a -cf $DestinationPath -C $ParentDirectory $ChildDirectoryName
        if ($LASTEXITCODE -eq 0) {
            return
        }

        if ($attempt -lt $RetryCount) {
            Write-Warning "Archive creation failed for $DestinationPath on attempt $attempt/$RetryCount. Retrying in $RetryDelaySeconds seconds."
            if (Test-Path $DestinationPath) {
                Remove-Item $DestinationPath -Force
            }
            Start-Sleep -Seconds $RetryDelaySeconds
            continue
        }

        throw "Failed to create archive at $DestinationPath"
    }
}

try {
    $qaasZipPath = Join-Path $assetRoot 'qaas-packages.zip'
    $notQaasZipPath = Join-Path $assetRoot 'not-qaas-packages.zip'
    $runnerSchemaAssetPath = Join-Path $assetRoot 'runner-family-schema.json'
    $mockerSchemaAssetPath = Join-Path $assetRoot 'mocker-family-schema.json'
    $notesPath = Join-Path $assetRoot 'release-notes.md'

    New-ZipArchive -ParentDirectory $packagesRoot -ChildDirectoryName 'qaas' -DestinationPath $qaasZipPath
    New-ZipArchive -ParentDirectory $packagesRoot -ChildDirectoryName 'not-qaas' -DestinationPath $notQaasZipPath
    Copy-Item -Path $runnerSchemaPath -Destination $runnerSchemaAssetPath -Force
    Copy-Item -Path $mockerSchemaPath -Destination $mockerSchemaAssetPath -Force

    $qaasPackageMap = @{}
    foreach ($packageDirectory in Get-ChildItem -Path $qaasPackagesRoot -Directory) {
        $latestVersionDirectory = Get-ChildItem -Path $packageDirectory.FullName -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if ($null -eq $latestVersionDirectory) {
            continue
        }

        $qaasPackageMap[$packageDirectory.Name.ToLowerInvariant()] = [ordered]@{
            Name = $packageDirectory.Name
            Version = $latestVersionDirectory.Name
        }
    }

    $trackedRepositories = @(
        'TheSmokeTeam/QaaS.Common.Assertions',
        'TheSmokeTeam/QaaS.Common.Generators',
        'TheSmokeTeam/QaaS.Common.Probes',
        'TheSmokeTeam/QaaS.Common.Processors',
        'TheSmokeTeam/QaaS.Framework',
        'TheSmokeTeam/QaaS.Mocker',
        'TheSmokeTeam/Qaas.Mocker.CommunicationObjects',
        'TheSmokeTeam/QaaS.Runner'
    )

    function Test-IsQaasPackageName {
        param([string]$PackageName)

        return ($PackageName -split '[.-]') -contains 'qaas'
    }

    $releaseLines = New-Object System.Collections.Generic.List[string]
    $releaseLines.Add("# Included QaaS packages by solution")
    $releaseLines.Add("")

    foreach ($repository in $trackedRepositories) {
        $statePath = Join-Path $stateRoot ($repository.Replace('/', '_') + '.json')
        if (-not (Test-Path $statePath)) {
            continue
        }

        $state = Get-Content $statePath -Raw | ConvertFrom-Json
        $repositoryPackages = $state.packages |
            Where-Object { Test-IsQaasPackageName $_.name } |
            ForEach-Object { $_.name.ToLowerInvariant() } |
            Sort-Object -Unique

        if ($repositoryPackages.Count -eq 0) {
            continue
        }

        $releaseLines.Add("## $($repository.Split('/')[-1])")

        foreach ($packageName in $repositoryPackages) {
            if ($qaasPackageMap.ContainsKey($packageName)) {
                $package = $qaasPackageMap[$packageName]
                $releaseLines.Add("- $($package.Name) version $($package.Version)")
            }
        }

        $releaseLines.Add("")
    }

    Set-Content -Path $notesPath -Value ($releaseLines -join [Environment]::NewLine)

    if ($SkipPublish) {
        Write-Host "Release name: $releaseName"
        Write-Host "Release tag: $releaseTag"
        Write-Host "QaaS zip: $qaasZipPath"
        Write-Host "Not-QaaS zip: $notQaasZipPath"
        Write-Host "Runner schema asset: $runnerSchemaAssetPath"
        Write-Host "Mocker schema asset: $mockerSchemaAssetPath"
        Write-Host "Notes file: $notesPath"
        return
    }

    $env:GH_TOKEN = $GitHubToken
    gh release view $releaseTag --repo $GitHubRepository *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Release tag '$releaseTag' already exists."
    }

    gh release create $releaseTag $qaasZipPath $notQaasZipPath $runnerSchemaAssetPath $mockerSchemaAssetPath `
        --repo $GitHubRepository `
        --target $BranchName `
        --title $releaseName `
        --notes-file $notesPath `
        --latest

    Write-Host "Release URL: https://github.com/$GitHubRepository/releases/tag/$releaseTag"
}
finally {
    if (-not $SkipPublish -and (Test-Path $assetRoot)) {
        Remove-Item $assetRoot -Recurse -Force
    }
}

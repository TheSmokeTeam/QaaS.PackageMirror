param(
    [string]$WorkspaceRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory = $true)]
    [string]$GitHubRepository,
    [string]$BranchName = 'master',
    [string]$ReleaseTag = '',
    [string]$ReleaseTagPrefix = 'mirror',
    [string]$GitHubToken = '',
    [string]$PreviousPackagesRoot = '',
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

function New-CaseInsensitiveSet {
    return ,([System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase))
}

function Test-IsExcludedQaasBootstrapPackage {
    param(
        [string]$PackageName
    )

    return [string]::Equals($PackageName, 'qaas.elasticbootstrap', [System.StringComparison]::OrdinalIgnoreCase)
}

function New-PackageVersionKey {
    param(
        [string]$PackageName,
        [string]$Version
    )

    return "$PackageName/$Version"
}

function Get-PackageVersionSetFromDirectory {
    param(
        [string]$RootDirectory
    )

    $packageVersions = New-CaseInsensitiveSet
    if (-not (Test-Path $RootDirectory)) {
        return ,$packageVersions
    }

    foreach ($packageDirectory in Get-ChildItem -Path $RootDirectory -Directory) {
        foreach ($versionDirectory in Get-ChildItem -Path $packageDirectory.FullName -Directory) {
            [void]$packageVersions.Add((New-PackageVersionKey -PackageName $packageDirectory.Name -Version $versionDirectory.Name))
        }
    }

    return ,$packageVersions
}

function Get-GitRepositoryRoot {
    param(
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        return $null
    }

    $current = Get-Item -LiteralPath $Path
    if ($current -is [System.IO.FileInfo]) {
        $current = $current.Directory
    }

    while ($null -ne $current) {
        if (Test-Path (Join-Path $current.FullName '.git')) {
            return $current.FullName
        }

        $current = $current.Parent
    }

    return $null
}

function Test-GitRefExists {
    param(
        [string]$RepositoryRoot,
        [string]$GitRef
    )

    & git -C $RepositoryRoot rev-parse --verify --quiet $GitRef *> $null
    return $LASTEXITCODE -eq 0
}

function Resolve-PreviousPackagesGitRef {
    param(
        [string]$RepositoryRoot
    )

    $packagesStatus = & git -C $RepositoryRoot status --porcelain -- packages 2>$null
    if ($LASTEXITCODE -eq 0 -and $packagesStatus.Count -gt 0) {
        return 'HEAD'
    }

    if (Test-GitRefExists -RepositoryRoot $RepositoryRoot -GitRef 'HEAD^') {
        return 'HEAD^'
    }

    return $null
}

function Get-PackageVersionSetFromGitTree {
    param(
        [string]$RepositoryRoot,
        [string]$GitRef,
        [string]$Bucket
    )

    $packageVersions = New-CaseInsensitiveSet
    $treePaths = & git -C $RepositoryRoot ls-tree -r --name-only $GitRef -- "packages/$Bucket" 2>$null
    if ($LASTEXITCODE -ne 0) {
        return ,$packageVersions
    }

    foreach ($treePath in $treePaths) {
        if ([string]::IsNullOrWhiteSpace($treePath)) {
            continue
        }

        $segments = $treePath -split '[\\/]'
        if ($segments.Length -lt 4) {
            continue
        }

        if (-not [string]::Equals($segments[0], 'packages', [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($segments[1], $Bucket, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        [void]$packageVersions.Add((New-PackageVersionKey -PackageName $segments[2] -Version $segments[3]))
    }

    return ,$packageVersions
}

function Get-PreviousPackageVersionSet {
    param(
        [string]$Bucket
    )

    if (-not [string]::IsNullOrWhiteSpace($PreviousPackagesRoot)) {
        return ,(Get-PackageVersionSetFromDirectory -RootDirectory (Join-Path ([System.IO.Path]::GetFullPath($PreviousPackagesRoot)) $Bucket))
    }

    $repositoryRoot = Get-GitRepositoryRoot -Path $WorkspaceRoot
    if ([string]::IsNullOrWhiteSpace($repositoryRoot)) {
        return ,(New-CaseInsensitiveSet)
    }

    $gitRef = Resolve-PreviousPackagesGitRef -RepositoryRoot $repositoryRoot
    if ([string]::IsNullOrWhiteSpace($gitRef)) {
        return ,(New-CaseInsensitiveSet)
    }

    return ,(Get-PackageVersionSetFromGitTree -RepositoryRoot $repositoryRoot -GitRef $gitRef -Bucket $Bucket)
}

function Get-NewPackageVersionSet {
    param(
        [System.Collections.Generic.HashSet[string]]$CurrentPackages,
        [System.Collections.Generic.HashSet[string]]$PreviousPackages
    )

    $packageVersions = New-CaseInsensitiveSet
    foreach ($packageVersion in $CurrentPackages) {
        if ($PreviousPackages.Contains($packageVersion)) {
            continue
        }

        [void]$packageVersions.Add($packageVersion)
    }

    return ,$packageVersions
}

function Get-FilteredQaasBootstrapVersionSet {
    param(
        [System.Collections.Generic.HashSet[string]]$CurrentPackages
    )

    $packageVersions = New-CaseInsensitiveSet
    foreach ($packageVersion in $CurrentPackages) {
        $segments = $packageVersion -split '[\\/]'
        if ($segments.Length -lt 2) {
            continue
        }

        if (Test-IsExcludedQaasBootstrapPackage -PackageName $segments[0]) {
            continue
        }

        [void]$packageVersions.Add($packageVersion)
    }

    return ,$packageVersions
}

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

function Copy-ReleasePackageTree {
    param(
        [string]$SourceDirectory,
        [string]$DestinationDirectory,
        [System.Collections.Generic.HashSet[string]]$IncludedVersionKeys
    )

    $excludedDirectoryNames = @('src', 'source', 'sources', 'contentFiles')
    $excludedExtensions = @(
        '.cs', '.csx', '.csproj',
        '.fs', '.fsx', '.fsproj',
        '.vb', '.vbproj',
        '.c', '.cc', '.cpp', '.cxx', '.h', '.hpp',
        '.java', '.kt',
        '.js', '.jsx', '.ts', '.tsx',
        '.proto'
    )

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null

    foreach ($file in Get-ChildItem -Path $SourceDirectory -Recurse -File) {
        $sourceUri = New-Object System.Uri(($SourceDirectory.TrimEnd('\') + '\'))
        $fileUri = New-Object System.Uri($file.FullName)
        $relativePath = [System.Uri]::UnescapeDataString($sourceUri.MakeRelativeUri($fileUri).ToString()).Replace('/', '\')
        $relativeSegments = $relativePath -split '[\\/]'
        if ($relativeSegments.Length -lt 2) {
            continue
        }

        $packageVersionKey = New-PackageVersionKey -PackageName $relativeSegments[0] -Version $relativeSegments[1]
        if (-not $IncludedVersionKeys.Contains($packageVersionKey)) {
            continue
        }

        if ($relativeSegments | Where-Object { $excludedDirectoryNames -icontains $_ }) {
            continue
        }

        if ($excludedExtensions -icontains $file.Extension) {
            continue
        }

        $destinationPath = Join-Path $DestinationDirectory $relativePath
        $destinationParent = Split-Path -Parent $destinationPath
        if (-not (Test-Path $destinationParent)) {
            New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        }

        Copy-Item -Path $file.FullName -Destination $destinationPath -Force
    }
}

try {
    if (-not [string]::IsNullOrWhiteSpace($PreviousPackagesRoot) -and -not (Test-Path $PreviousPackagesRoot)) {
        throw "Previous packages root '$PreviousPackagesRoot' does not exist."
    }

    $currentQaasPackageVersions = Get-PackageVersionSetFromDirectory -RootDirectory $qaasPackagesRoot
    $currentNotQaasPackageVersions = Get-PackageVersionSetFromDirectory -RootDirectory $notQaasPackagesRoot
    $previousQaasPackageVersions = Get-PreviousPackageVersionSet -Bucket 'qaas'
    $previousNotQaasPackageVersions = Get-PreviousPackageVersionSet -Bucket 'not-qaas'
    $releaseQaasPackageVersions = Get-FilteredQaasBootstrapVersionSet -CurrentPackages $currentQaasPackageVersions
    $releaseNotQaasPackageVersions = Get-NewPackageVersionSet -CurrentPackages $currentNotQaasPackageVersions -PreviousPackages $previousNotQaasPackageVersions

    $qaasZipPath = Join-Path $assetRoot 'qaas-packages.zip'
    $notQaasZipPath = Join-Path $assetRoot 'not-qaas-packages.zip'
    $runnerSchemaAssetPath = Join-Path $assetRoot 'runner-family-schema.json'
    $mockerSchemaAssetPath = Join-Path $assetRoot 'mocker-family-schema.json'
    $notesPath = Join-Path $assetRoot 'release-notes.md'
    $releasePackagesRoot = Join-Path $assetRoot 'packages'
    $releaseQaasRoot = Join-Path $releasePackagesRoot 'qaas'
    $releaseNotQaasRoot = Join-Path $releasePackagesRoot 'not-qaas'

    Copy-ReleasePackageTree -SourceDirectory $qaasPackagesRoot -DestinationDirectory $releaseQaasRoot -IncludedVersionKeys $releaseQaasPackageVersions
    Copy-ReleasePackageTree -SourceDirectory $notQaasPackagesRoot -DestinationDirectory $releaseNotQaasRoot -IncludedVersionKeys $releaseNotQaasPackageVersions
    New-ZipArchive -ParentDirectory $releasePackagesRoot -ChildDirectoryName 'qaas' -DestinationPath $qaasZipPath
    New-ZipArchive -ParentDirectory $releasePackagesRoot -ChildDirectoryName 'not-qaas' -DestinationPath $notQaasZipPath
    Copy-Item -Path $runnerSchemaPath -Destination $runnerSchemaAssetPath -Force
    Copy-Item -Path $mockerSchemaPath -Destination $mockerSchemaAssetPath -Force

    $qaasPackageMap = @{}
    foreach ($packageDirectory in Get-ChildItem -Path $qaasPackagesRoot -Directory) {
        foreach ($versionDirectory in Get-ChildItem -Path $packageDirectory.FullName -Directory | Sort-Object Name -Descending) {
            $packageVersionKey = New-PackageVersionKey -PackageName $packageDirectory.Name -Version $versionDirectory.Name
            if (-not $releaseQaasPackageVersions.Contains($packageVersionKey)) {
                continue
            }

            $qaasPackageMap[$packageDirectory.Name.ToLowerInvariant()] = [ordered]@{
                Name = $packageDirectory.Name
                Version = $versionDirectory.Name
            }

            break
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
    $releaseLines.Add("# Included QaaS bootstrap packages by solution")
    $releaseLines.Add("")

    if ($qaasPackageMap.Count -eq 0) {
        $releaseLines.Add("No QaaS bootstrap packages were included in this release.")
        $releaseLines.Add("")
    }

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

        $includedRepositoryPackages = $repositoryPackages |
            Where-Object { $qaasPackageMap.ContainsKey($_) }

        if ($includedRepositoryPackages.Count -eq 0) {
            continue
        }

        $releaseLines.Add("## $($repository.Split('/')[-1])")

        foreach ($packageName in $includedRepositoryPackages) {
            $package = $qaasPackageMap[$packageName]
            $releaseLines.Add("- $($package.Name) version $($package.Version)")
        }

        $releaseLines.Add("")
    }

    Set-Content -Path $notesPath -Value ($releaseLines -join [Environment]::NewLine)

    if ($SkipPublish) {
        Write-Host "Release name: $releaseName"
        Write-Host "Release tag: $releaseTag"
        Write-Host "QaaS bootstrap package versions included: $($releaseQaasPackageVersions.Count)"
        Write-Host "Not-QaaS dependency package versions included: $($releaseNotQaasPackageVersions.Count)"
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

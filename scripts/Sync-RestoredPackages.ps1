param(
    [string]$SourceRepository,
    [Parameter(Mandatory = $true)]
    [string]$GitHubToken
)

$ErrorActionPreference = 'Stop'

$trackedRepositories = @(
    @{ SourceRepository = 'TheSmokeTeam/QaaS.Common.Assertions'; SourceWorkflowName = 'CI'; AllowPrerelease = $false }
    @{ SourceRepository = 'TheSmokeTeam/QaaS.Common.Generators'; SourceWorkflowName = 'CI'; AllowPrerelease = $false }
    @{ SourceRepository = 'TheSmokeTeam/QaaS.Common.Probes'; SourceWorkflowName = 'CI'; AllowPrerelease = $false }
    @{ SourceRepository = 'TheSmokeTeam/QaaS.Common.Processors'; SourceWorkflowName = 'CI'; AllowPrerelease = $false }
    @{ SourceRepository = 'TheSmokeTeam/QaaS.Framework'; SourceWorkflowName = 'CI'; AllowPrerelease = $false }
    @{ SourceRepository = 'TheSmokeTeam/QaaS.Mocker'; SourceWorkflowName = 'CI'; AllowPrerelease = $false }
    @{ SourceRepository = 'TheSmokeTeam/Qaas.Mocker.CommunicationObjects'; SourceWorkflowName = 'CI'; AllowPrerelease = $false }
    @{ SourceRepository = 'TheSmokeTeam/QaaS.Runner'; SourceWorkflowName = 'CI'; AllowPrerelease = $true }
)

if (-not [string]::IsNullOrWhiteSpace($SourceRepository)) {
    throw 'Targeted sync is not supported. The mirror is rebuilt from the full tracked repository set on every run.'
}

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$incomingRoot = Join-Path $workspaceRoot 'incoming'
$combinedRoot = Join-Path $incomingRoot 'combined'
$stateRoot = Join-Path $workspaceRoot 'state'

if (Test-Path $incomingRoot) {
    Remove-Item $incomingRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $incomingRoot | Out-Null
New-Item -ItemType Directory -Path $combinedRoot | Out-Null
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
Get-ChildItem -Path $stateRoot -Filter *.json -File -ErrorAction SilentlyContinue | Remove-Item -Force

$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $GitHubToken"
    'X-GitHub-Api-Version' = '2022-11-28'
}

function Get-LatestArtifactContext {
    param(
        [string]$Repository,
        [string]$WorkflowName,
        [bool]$AllowPrerelease
    )

    $runsUrl = "https://api.github.com/repos/$Repository/actions/runs?per_page=30"
    $runsResponse = Invoke-RestMethod -Method Get -Headers $headers -Uri $runsUrl

    foreach ($run in $runsResponse.workflow_runs) {
        if ($run.name -ne $WorkflowName) { continue }
        if ($run.conclusion -ne 'success') { continue }

        $isStableTag = $run.head_branch -match '^[0-9]+\.[0-9]+\.[0-9]+$'
        $isPrereleaseTag = $run.head_branch -match '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$'
        if (-not $isStableTag -and (-not $AllowPrerelease -or -not $isPrereleaseTag)) { continue }

        $artifactsUrl = "https://api.github.com/repos/$Repository/actions/runs/$($run.id)/artifacts"
        $artifactsResponse = Invoke-RestMethod -Method Get -Headers $headers -Uri $artifactsUrl
        $artifact = $artifactsResponse.artifacts | Where-Object { $_.name -eq 'restored-packages' -and -not $_.expired } | Select-Object -First 1
        if ($null -ne $artifact) {
            return @{
                Run = $run
                Artifact = $artifact
            }
        }
    }

    return $null
}

function Copy-PackageTree {
    param(
        [string]$SourceRoot,
        [string]$DestinationRoot
    )

    foreach ($packageDirectory in Get-ChildItem -Path $SourceRoot -Directory) {
        $targetPackageDirectory = Join-Path $DestinationRoot $packageDirectory.Name
        New-Item -ItemType Directory -Path $targetPackageDirectory -Force | Out-Null

        foreach ($versionDirectory in Get-ChildItem -Path $packageDirectory.FullName -Directory) {
            $targetVersionDirectory = Join-Path $targetPackageDirectory $versionDirectory.Name
            New-Item -ItemType Directory -Path $targetVersionDirectory -Force | Out-Null
            Copy-Item (Join-Path $versionDirectory.FullName '*') $targetVersionDirectory -Recurse -Force
        }
    }
}

function Get-PackageVersions {
    param(
        [string]$ArtifactRoot
    )

    $packageVersions = New-Object System.Collections.Generic.List[object]

    foreach ($packageDirectory in Get-ChildItem -Path $ArtifactRoot -Directory) {
        foreach ($versionDirectory in Get-ChildItem -Path $packageDirectory.FullName -Directory) {
            $packageVersions.Add([ordered]@{
                name = $packageDirectory.Name
                version = $versionDirectory.Name
            })
        }
    }

    return $packageVersions | Sort-Object name, version
}

function Write-StateFile {
    param(
        [string]$Repository,
        [string]$Tag,
        [string]$Origin,
        [string]$RunId,
        [object[]]$Packages
    )

    $stateKey = $Repository.Replace('/', '_')
    $statePath = Join-Path $stateRoot "$stateKey.json"
    $state = [ordered]@{
        repository = $Repository
        tag = $Tag
        origin = $Origin
        runId = $RunId
        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        packages = $Packages
    }

    $state | ConvertTo-Json -Depth 6 | Set-Content -Path $statePath
}

$processedRepositories = New-Object System.Collections.Generic.List[string]

foreach ($trackedRepository in $trackedRepositories) {
    $repository = $trackedRepository.SourceRepository
    $workflowName = $trackedRepository.SourceWorkflowName
    $allowPrerelease = [bool]$trackedRepository.AllowPrerelease
    Write-Host "Resolving latest artifact for $repository"

    $context = Get-LatestArtifactContext -Repository $repository -WorkflowName $workflowName -AllowPrerelease $allowPrerelease
    if ($null -eq $context) {
        Write-Warning "Skipping $repository because no successful restored-packages artifact is currently available."
        continue
    }

    $run = $context.Run
    $artifact = $context.Artifact

    $repositoryKey = $repository.Replace('/', '_')
    $artifactZipPath = Join-Path $incomingRoot "$repositoryKey.zip"
    $artifactExtractRoot = Join-Path $incomingRoot $repositoryKey

    Invoke-WebRequest -Method Get -Headers $headers -Uri $artifact.archive_download_url -OutFile $artifactZipPath
    Expand-Archive -Path $artifactZipPath -DestinationPath $artifactExtractRoot -Force

    $metadataPath = Join-Path $artifactExtractRoot 'restore-artifact-metadata.json'
    if (-not (Test-Path $metadataPath)) {
        throw "Missing restore artifact metadata file: $metadataPath"
    }

    $metadata = Get-Content $metadataPath | ConvertFrom-Json
    Copy-PackageTree -SourceRoot $artifactExtractRoot -DestinationRoot $combinedRoot

    $packageVersions = Get-PackageVersions -ArtifactRoot $artifactExtractRoot
    Write-StateFile -Repository $metadata.repository -Tag $metadata.tag -Origin $run.html_url -RunId $run.id -Packages $packageVersions

    $processedRepositories.Add($repository) | Out-Null
}

if ($processedRepositories.Count -eq 0) {
    throw 'No repositories were processed.'
}

$fullSyncTag = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$fullSyncOrigin = if ($env:GITHUB_REPOSITORY -and $env:GITHUB_RUN_ID) {
    "https://github.com/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)"
}
else {
    'manual-local-sync'
}

dotnet run --project (Join-Path $workspaceRoot 'QaaS.PackageMirror\QaaS.PackageMirror.csproj') -- `
    --artifact-root $combinedRoot `
    --mirror-root $workspaceRoot `
    --source-repo 'TheSmokeTeam/QaaS.PackageMirror.FullSync' `
    --source-tag $fullSyncTag `
    --origin $fullSyncOrigin `
    --source-run-id $fullSyncTag `
    --reset-packages `
    --skip-duplicate-check `
    --skip-state-write

if ($LASTEXITCODE -ne 0) {
    throw 'Full mirror rebuild failed.'
}

& (Join-Path $workspaceRoot 'scripts\Generate-FamilySchemas.ps1') `
    -MirrorRoot $workspaceRoot `
    -SnapshotId $fullSyncTag `
    -TriggerRepository 'TheSmokeTeam/QaaS.PackageMirror.FullSync' `
    -TriggerTag $fullSyncTag `
    -TriggerRunId $fullSyncTag `
    -TriggerOrigin $fullSyncOrigin

if ($LASTEXITCODE -ne 0) {
    throw 'Family schema generation failed.'
}

if (Test-Path $incomingRoot) {
    Remove-Item $incomingRoot -Recurse -Force
}

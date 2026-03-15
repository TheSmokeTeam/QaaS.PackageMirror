param(
    [string]$PackagesRoot = (Join-Path $PSScriptRoot '..\packages'),
    [ValidateSet('all', 'qaas', 'not-qaas')]
    [string]$Bucket = 'all',
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [Parameter(Mandatory = $true)]
    [string]$ApiKey,
    [switch]$PushSymbols,
    [string]$SymbolSource,
    [string]$SymbolApiKey
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The dotnet CLI is required to push mirrored packages.'
}

if ($PushSymbols) {
    if ([string]::IsNullOrWhiteSpace($SymbolSource)) {
        throw 'Specify -SymbolSource when -PushSymbols is used.'
    }

    if ([string]::IsNullOrWhiteSpace($SymbolApiKey)) {
        $SymbolApiKey = $ApiKey
    }
}

$resolvedPackagesRoot = (Resolve-Path $PackagesRoot).Path
$bucketNames = if ($Bucket -eq 'all') { @('qaas', 'not-qaas') } else { @($Bucket) }

$packageFiles = foreach ($bucketName in $bucketNames) {
    $bucketRoot = Join-Path $resolvedPackagesRoot $bucketName
    if (-not (Test-Path $bucketRoot)) {
        continue
    }

    Get-ChildItem -Path $bucketRoot -Recurse -File -Filter *.nupkg |
        Where-Object { $_.Name -notlike '*.snupkg' }
}

$packageFiles = $packageFiles | Sort-Object FullName -Unique

if (-not $packageFiles) {
    throw "No .nupkg files were found under '$resolvedPackagesRoot'."
}

foreach ($packageFile in $packageFiles) {
    $arguments = @(
        'nuget',
        'push',
        $packageFile.FullName,
        '--source',
        $Source,
        '--api-key',
        $ApiKey,
        '--skip-duplicate'
    )

    if ($PushSymbols) {
        $symbolPackage = [System.IO.Path]::ChangeExtension($packageFile.FullName, '.snupkg')
        if (Test-Path $symbolPackage) {
            $arguments += @(
                '--symbol-source',
                $SymbolSource,
                '--symbol-api-key',
                $SymbolApiKey
            )
        }
    }

    Write-Host "Pushing $($packageFile.FullName)"
    & dotnet @arguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet nuget push failed for '$($packageFile.FullName)'."
    }
}

param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$FeedUrl,

    [Parameter(Mandatory = $true)]
    [string]$ApiKey
)

Write-Host "This repository currently stores extracted restored packages under packages/<id>/<version>."
Write-Host "If you later want direct dotnet nuget push support, extend the source artifact to include .nupkg files."

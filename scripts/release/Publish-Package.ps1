param(
    [Parameter(Mandatory)]
    [ValidateSet('nuget', 'npm')]
    [string]$Kind,
    [Parameter(Mandatory)]
    [string]$Path,
    [Parameter(Mandatory)]
    [string]$PackageId,
    [Parameter(Mandatory)]
    [string]$Version,
    [string]$NuGetApiKey,
    [int]$TimeoutMinutes = 20
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$fullPath = Join-Path $root $Path

function Test-PackageAvailable {
    try {
        if ($Kind -eq 'nuget') {
            $normalizedId = $PackageId.ToLowerInvariant()
            $normalizedVersion = $Version.ToLowerInvariant()
            $url = "https://api.nuget.org/v3-registration5-gz-semver2/$normalizedId/$normalizedVersion.json"
        }
        else {
            $escapedId = [Uri]::EscapeDataString($PackageId)
            $url = "https://registry.npmjs.org/$escapedId/$Version"
        }
        $response = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Wait-PackageAvailable {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
    do {
        if (Test-PackageAvailable) {
            Write-Host "$PackageId $Version is available on $Kind."
            return
        }
        Write-Host "Waiting for $PackageId $Version to become available on $Kind..."
        Start-Sleep -Seconds 5
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "$PackageId $Version was not available on $Kind within $TimeoutMinutes minutes."
}

if (Test-PackageAvailable) {
    Write-Host "$PackageId $Version is already available; skipping publish."
    exit 0
}

if ($Kind -eq 'nuget') {
    if (-not $NuGetApiKey) {
        throw 'NuGetApiKey is required for NuGet publishing.'
    }
    $output = Join-Path $env:RUNNER_TEMP "packages/$PackageId"
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $buildSucceeded = $false
    $buildDeadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
    do {
        & dotnet build $fullPath --configuration Release --force -p:Version=$Version -p:RestoreNoCache=true
        if ($LASTEXITCODE -eq 0) {
            $buildSucceeded = $true
            break
        }
        Write-Host "Build or restore failed for $PackageId; retrying while NuGet propagates..."
        Start-Sleep -Seconds 10
    } while ([DateTimeOffset]::UtcNow -lt $buildDeadline)
    if (-not $buildSucceeded) { throw "dotnet build failed for $PackageId." }
    & dotnet pack $fullPath --configuration Release --no-build --output $output -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $PackageId." }

    $package = Get-ChildItem $output -Filter '*.nupkg' |
        Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
        Select-Object -First 1
    if ($null -eq $package) { throw "No nupkg was produced for $PackageId." }

    & dotnet nuget push $package.FullName --api-key $NuGetApiKey --source https://api.nuget.org/v3/index.json --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "NuGet push failed for $PackageId." }
}
else {
    $packageDirectory = Split-Path $fullPath
    Push-Location $packageDirectory
    try {
        if (Test-Path 'package-lock.json') { & npm ci }
        else { & npm install }
        if ($LASTEXITCODE -ne 0) { throw "npm install failed for $PackageId." }
        $publishArguments = @('--access', 'public')
        if ($Version -match '-') {
            $tag = [regex]::Match($Version, '-(?<tag>[0-9A-Za-z-]+)\.').Groups['tag'].Value
            if (-not $tag) { throw "Cannot determine npm dist-tag from version $Version." }
            $publishArguments += @('--tag', $tag)
        }
        & npm publish @publishArguments
        if ($LASTEXITCODE -ne 0) { throw "npm publish failed for $PackageId." }
    }
    finally {
        Pop-Location
    }
}

Wait-PackageAvailable

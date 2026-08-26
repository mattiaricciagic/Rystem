param(
    [ValidateSet('prerelease', 'patch', 'minor', 'major')]
    [string]$Increment = 'prerelease',
    [string]$SpecificVersion,
    [string]$Profile = 'all',
    [string]$ManifestPath = '.github/release-packages.json',
    [string]$PrereleaseLabel = 'beta',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$manifestFile = Join-Path $root $ManifestPath
$manifest = Get-Content $manifestFile -Raw | ConvertFrom-Json

function Get-NextVersion([string]$currentVersion) {
    if ($currentVersion -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<label>[0-9A-Za-z-]+)\.(?<number>\d+))?$') {
        throw "Unsupported release version '$currentVersion'."
    }

    $major = [int]$Matches.major
    $minor = [int]$Matches.minor
    $patch = [int]$Matches.patch
    $label = $Matches.label
    $number = if ($Matches.number) { [int]$Matches.number } else { 0 }

    $nextVersion = switch ($Increment) {
        'prerelease' {
            if ($label -eq $PrereleaseLabel) { "$major.$minor.$patch-$PrereleaseLabel.$($number + 1)" }
            else { "$major.$minor.$($patch + 1)-$PrereleaseLabel.1" }
        }
        'patch' {
            if ($label) { "$major.$minor.$patch" }
            else { "$major.$minor.$($patch + 1)" }
        }
        'minor' { "$major.$($minor + 1).0" }
        'major' { "$($major + 1).0.0" }
    }
    return $nextVersion
}

if ($SpecificVersion) {
    if ($SpecificVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+\.\d+)?$') {
        throw "Unsupported release version '$SpecificVersion'."
    }
    $newVersion = $SpecificVersion
}
else {
    $newVersion = Get-NextVersion $manifest.version
}
$nugetPaths = @($manifest.nuget)
$npmPaths = @($manifest.npm)
if ($Profile -ne 'all') {
    $profileNode = $manifest.profiles.PSObject.Properties[$Profile]
    if ($null -eq $profileNode) {
        throw "Release profile '$Profile' was not found in $ManifestPath."
    }
    $nugetPaths = @($profileNode.Value.nuget)
    $npmPaths = @($profileNode.Value.npm)
}
$packages = [System.Collections.Generic.List[object]]::new()
$packageById = @{}

foreach ($relativePath in $nugetPaths) {
    $fullPath = Join-Path $root $relativePath
    [xml]$project = Get-Content $fullPath -Raw
    $packageIdNode = $project.SelectSingleNode('/Project/PropertyGroup/PackageId')
    if ($null -eq $packageIdNode) {
        throw "PackageId is missing in $relativePath."
    }

    $descriptor = [pscustomobject]@{
        kind = 'nuget'
        path = $relativePath.Replace('\', '/')
        id = $packageIdNode.InnerText
        dependencies = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    }
    $packages.Add($descriptor)
    if ($packageById.ContainsKey($descriptor.id)) {
        throw "Duplicate package id '$($descriptor.id)'."
    }
    $packageById[$descriptor.id] = $descriptor
}

foreach ($relativePath in $npmPaths) {
    $fullPath = Join-Path $root $relativePath
    $packageJson = Get-Content $fullPath -Raw | ConvertFrom-Json
    $descriptor = [pscustomobject]@{
        kind = 'npm'
        path = $relativePath.Replace('\', '/')
        id = $packageJson.name
        dependencies = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    }
    $packages.Add($descriptor)
    if ($packageById.ContainsKey($descriptor.id)) {
        throw "Duplicate package id '$($descriptor.id)'."
    }
    $packageById[$descriptor.id] = $descriptor
}

foreach ($package in $packages) {
    $fullPath = Join-Path $root $package.path
    if ($package.kind -eq 'nuget') {
        $content = Get-Content $fullPath -Raw
        [xml]$project = $content
        foreach ($reference in $project.SelectNodes('//PackageReference[@Include]')) {
            $dependencyId = $reference.GetAttribute('Include')
            if ($packageById.ContainsKey($dependencyId)) {
                [void]$package.dependencies.Add($dependencyId)
                $escapedId = [regex]::Escape($dependencyId)
                $content = [regex]::Replace(
                    $content,
                    "(<PackageReference\s+Include=[`"']$escapedId[`"'][^>]*?\sVersion=[`"'])[^`"']+([`"'])",
                    "`${1}$newVersion`${2}")
            }
        }
        $content = [regex]::Replace($content, '<Version>[^<]+</Version>', "<Version>$newVersion</Version>", 1)
        if (-not $WhatIf) {
            [IO.File]::WriteAllText($fullPath, $content)
        }
    }
    else {
        $packageJson = Get-Content $fullPath -Raw | ConvertFrom-Json
        $packageJson.version = $newVersion
        foreach ($section in @('dependencies', 'devDependencies', 'peerDependencies', 'optionalDependencies')) {
            $dependencies = $packageJson.$section
            if ($null -eq $dependencies) { continue }
            foreach ($dependency in $dependencies.PSObject.Properties) {
                if ($packageById.ContainsKey($dependency.Name)) {
                    [void]$package.dependencies.Add($dependency.Name)
                    $dependency.Value = $newVersion
                }
            }
        }
        if (-not $WhatIf) {
            $json = $packageJson | ConvertTo-Json -Depth 100
            [IO.File]::WriteAllText($fullPath, "$json`n")
        }
    }
}

$remaining = [System.Collections.Generic.List[object]]::new()
$remaining.AddRange($packages)
$published = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$levels = [System.Collections.Generic.List[object]]::new()

while ($remaining.Count -gt 0) {
    $level = @($remaining | Where-Object {
        $unresolved = @($_.dependencies | Where-Object { -not $published.Contains($_) })
        $unresolved.Count -eq 0
    })
    if ($level.Count -eq 0) {
        $blocked = $remaining | ForEach-Object { "$($_.id) -> $([string]::Join(', ', $_.dependencies))" }
        throw "Circular package dependency detected: $([string]::Join('; ', $blocked))"
    }
    $levels.Add($level)
    foreach ($package in $level) {
        [void]$published.Add($package.id)
        [void]$remaining.Remove($package)
    }
}

if ($levels.Count -gt 6) {
    throw "The package graph has $($levels.Count) levels, but the workflow supports at most 6."
}

if (-not $WhatIf -and $Profile -eq 'all') {
    $manifest.version = $newVersion
    [IO.File]::WriteAllText($manifestFile, "$(($manifest | ConvertTo-Json -Depth 10))`n")
}

Write-Host "Release version: $newVersion"
for ($index = 0; $index -lt $levels.Count; $index++) {
    Write-Host "Level $index`: $([string]::Join(', ', @($levels[$index].id)))"
}

if ($env:GITHUB_OUTPUT) {
    "version=$newVersion" >> $env:GITHUB_OUTPUT
    for ($index = 0; $index -lt 6; $index++) {
        $matrix = if ($index -lt $levels.Count) {
            @($levels[$index] | Select-Object kind, path, id) | ConvertTo-Json -Compress -AsArray
        }
        else { '[]' }
        "level_$index=$matrix" >> $env:GITHUB_OUTPUT
    }
}

param(
    [string] $SourcePath = (Join-Path $PSScriptRoot '..\austins-trade-copier.cs'),
    [string] $AddOnDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NinjaTrader 8\bin\Custom\AddOns'),
    [string] $Version = ''
)

$ErrorActionPreference = 'Stop'

function Resolve-RequiredPath {
    param(
        [string] $Path,
        [string] $Description
    )

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $resolved) {
        throw "$Description not found: $Path"
    }

    return $resolved.ProviderPath
}

function Get-GitShortHash {
    param(
        [string] $RepositoryRoot,
        [string] $InstalledSourcePath
    )

    try {
        $hash = (& git -C $RepositoryRoot rev-parse --short HEAD 2>$null)
        if (-not [string]::IsNullOrWhiteSpace($hash)) {
            $status = (& git -C $RepositoryRoot status --short -- $InstalledSourcePath 2>$null)
            $suffix = if ([string]::IsNullOrWhiteSpace($status)) { '' } else { '-dirty' }
            return ($hash.Trim() + $suffix)
        }
    }
    catch {
    }

    return 'nogit'
}

function Set-ConstantValue {
    param(
        [string] $Content,
        [string] $Name,
        [string] $Value
    )

    $escapedValue = $Value.Replace('\', '\\').Replace('"', '\"')
    $pattern = 'private const string ' + [Regex]::Escape($Name) + ' = "[^"]*";'
    $replacement = 'private const string ' + $Name + ' = "' + $escapedValue + '";'
    return [Regex]::Replace($Content, $pattern, $replacement, 1)
}

$source = Resolve-RequiredPath $SourcePath 'NinjaScript source'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$commit = Get-GitShortHash $repositoryRoot $source
$versionToWrite = if ([string]::IsNullOrWhiteSpace($Version)) { $null } else { $Version.Trim() }

if (-not (Test-Path -LiteralPath $AddOnDirectory)) {
    New-Item -ItemType Directory -Path $AddOnDirectory | Out-Null
}

$destination = Join-Path $AddOnDirectory (Split-Path -Leaf $source)
$content = Get-Content -LiteralPath $source -Raw
if ($null -ne $versionToWrite) {
    $content = Set-ConstantValue $content 'BuildVersion' $versionToWrite
}

$content = Set-ConstantValue $content 'BuildCommit' $commit
Set-Content -LiteralPath $destination -Value $content -Encoding UTF8

$installedVersion = if ($null -ne $versionToWrite) { $versionToWrite } else { '0.1.0-dev' }
Write-Host "Installed Austin's Trade Copier to $destination"
Write-Host "Build tag: v$installedVersion+$commit"

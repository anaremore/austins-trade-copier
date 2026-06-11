param(
    [string] $SourcePath = (Join-Path $PSScriptRoot '..\austins-trade-copier.cs'),
    [string] $AddOnDirectory = '',
    [string] $NinjaTraderUserDirectory = '',
    [string] $Version = '',
    [string] $NinjaTraderBin = '',
    [switch] $Verify
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

function Resolve-NinjaTraderUserDirectory {
    param([string] $Path)

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }

    if (-not [string]::IsNullOrWhiteSpace($env:NINJATRADER_USER_DIR)) {
        return $env:NINJATRADER_USER_DIR
    }

    $documents = [Environment]::GetFolderPath('MyDocuments')
    if ([string]::IsNullOrWhiteSpace($documents)) {
        throw 'Unable to resolve the current user Documents folder. Pass -NinjaTraderUserDirectory or -AddOnDirectory.'
    }

    return Join-Path $documents 'NinjaTrader 8'
}

function Resolve-AddOnDirectory {
    param(
        [string] $Path,
        [string] $UserDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }

    $resolvedUserDirectory = Resolve-NinjaTraderUserDirectory $UserDirectory
    return Join-Path $resolvedUserDirectory 'bin\Custom\AddOns'
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
    if (-not [Regex]::IsMatch($Content, $pattern)) {
        throw "Build stamp constant not found: $Name"
    }

    $replacement = 'private const string ' + $Name + ' = "' + $escapedValue + '";'
    return [Regex]::Replace($Content, $pattern, $replacement, 1)
}

function Get-ConstantValue {
    param(
        [string] $Content,
        [string] $Name
    )

    $pattern = 'private const string ' + [Regex]::Escape($Name) + ' = "([^"]*)";'
    $match = [Regex]::Match($Content, $pattern)
    if (-not $match.Success) {
        throw "Build stamp constant not found: $Name"
    }

    return $match.Groups[1].Value
}

$source = Resolve-RequiredPath $SourcePath 'NinjaScript source'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$commit = Get-GitShortHash $repositoryRoot $source
$versionToWrite = if ([string]::IsNullOrWhiteSpace($Version)) { $null } else { $Version.Trim() }
$resolvedAddOnDirectory = Resolve-AddOnDirectory $AddOnDirectory $NinjaTraderUserDirectory

if (-not (Test-Path -LiteralPath $resolvedAddOnDirectory)) {
    New-Item -ItemType Directory -Path $resolvedAddOnDirectory | Out-Null
}

$destination = Join-Path $resolvedAddOnDirectory (Split-Path -Leaf $source)
$content = Get-Content -LiteralPath $source -Raw
if ($null -ne $versionToWrite) {
    $content = Set-ConstantValue $content 'BuildVersion' $versionToWrite
}

$content = Set-ConstantValue $content 'BuildCommit' $commit
Set-Content -LiteralPath $destination -Value $content -Encoding UTF8

$installedVersion = Get-ConstantValue $content 'BuildVersion'
Write-Host "Installed Austin's Trade Copier to $destination"
Write-Host "Build tag: v$installedVersion+$commit"

if ($Verify) {
    $verifyScript = Resolve-RequiredPath (Join-Path $PSScriptRoot 'verify-ninjascript.ps1') 'NinjaScript verifier'
    Write-Host "Verifying installed NinjaScript..."
    & $verifyScript -SourcePath $destination -NinjaTraderBin $NinjaTraderBin
}

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

function Resolve-FullPath {
    param(
        [string] $Path,
        [string] $Description
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Description path is empty."
    }

    try {
        return [System.IO.Path]::GetFullPath($Path)
    }
    catch {
        throw "$Description path is invalid: $Path"
    }
}

function Resolve-RequiredDirectory {
    param(
        [string] $Path,
        [string] $Description
    )

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $resolved -or -not (Test-Path -LiteralPath $resolved.ProviderPath -PathType Container)) {
        throw "$Description not found: $Path"
    }

    return $resolved.ProviderPath
}

function Resolve-NinjaTraderUserDirectory {
    param([string] $Path)

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return Resolve-RequiredDirectory $Path 'NinjaTrader user directory'
    }

    if (-not [string]::IsNullOrWhiteSpace($env:NINJATRADER_USER_DIR)) {
        return Resolve-RequiredDirectory $env:NINJATRADER_USER_DIR 'NINJATRADER_USER_DIR'
    }

    $documents = [Environment]::GetFolderPath('MyDocuments')
    if ([string]::IsNullOrWhiteSpace($documents)) {
        throw 'Unable to resolve the current user Documents folder. Pass -NinjaTraderUserDirectory or -AddOnDirectory.'
    }

    $defaultUserDirectory = Join-Path $documents 'NinjaTrader 8'
    return Resolve-RequiredDirectory $defaultUserDirectory 'NinjaTrader user directory'
}

function Resolve-AddOnDirectory {
    param(
        [string] $Path,
        [string] $UserDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return Resolve-FullPath $Path 'AddOn directory'
    }

    if (-not [string]::IsNullOrWhiteSpace($env:NINJATRADER_ADDON_DIR)) {
        return Resolve-FullPath $env:NINJATRADER_ADDON_DIR 'NINJATRADER_ADDON_DIR'
    }

    $resolvedUserDirectory = Resolve-NinjaTraderUserDirectory $UserDirectory
    $customDirectory = Resolve-RequiredDirectory (Join-Path $resolvedUserDirectory 'bin\Custom') 'NinjaTrader custom source directory'
    return Join-Path $customDirectory 'AddOns'
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
    [System.IO.Directory]::CreateDirectory($resolvedAddOnDirectory) | Out-Null
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

param(
    [string] $SourcePath = (Join-Path $PSScriptRoot '..\austins-trade-copier.cs'),
    [string] $NinjaTraderBin = '',
    [string] $OutputPath = (Join-Path ([System.IO.Path]::GetTempPath()) 'AustinTradeCopier.verify.dll')
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

function Test-NinjaTraderBin {
    param([string] $Path)

    return -not [string]::IsNullOrWhiteSpace($Path) `
        -and (Test-Path -LiteralPath (Join-Path $Path 'NinjaTrader.Core.dll')) `
        -and (Test-Path -LiteralPath (Join-Path $Path 'NinjaTrader.Gui.dll'))
}

function Get-CandidateNinjaTraderBins {
    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($env:NINJATRADER_BIN)) {
        $candidates.Add($env:NINJATRADER_BIN)
    }

    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not [string]::IsNullOrWhiteSpace($root)) {
            $candidates.Add((Join-Path $root 'NinjaTrader 8\bin'))
        }
    }

    return $candidates | Select-Object -Unique
}

function Resolve-NinjaTraderBin {
    param([string] $Path)

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        $resolved = Resolve-RequiredPath $Path 'NinjaTrader bin folder'
        if (Test-NinjaTraderBin $resolved) {
            return $resolved
        }

        throw "NinjaTrader bin folder does not contain required assemblies: $resolved"
    }

    foreach ($candidate in Get-CandidateNinjaTraderBins) {
        $resolved = Resolve-Path -LiteralPath $candidate -ErrorAction SilentlyContinue
        if ($null -ne $resolved -and (Test-NinjaTraderBin $resolved.ProviderPath)) {
            return $resolved.ProviderPath
        }
    }

    throw 'NinjaTrader bin folder was not found. Set $env:NINJATRADER_BIN or pass -NinjaTraderBin with the folder containing NinjaTrader.Core.dll and NinjaTrader.Gui.dll.'
}

function Resolve-FrameworkReferenceRoot {
    foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        $frameworkRoot = Join-Path $root 'Reference Assemblies\Microsoft\Framework\.NETFramework'
        foreach ($version in @('v4.8', 'v4.7.2', 'v4.0')) {
            $candidate = Join-Path $frameworkRoot $version
            if (Test-Path -LiteralPath $candidate) {
                return (Resolve-Path -LiteralPath $candidate).ProviderPath
            }
        }
    }

    throw 'No supported .NET Framework reference assemblies were found. Install .NET Framework developer/reference assemblies and retry.'
}

$source = Resolve-RequiredPath $SourcePath 'NinjaScript source'
$ntBin = Resolve-NinjaTraderBin $NinjaTraderBin
$frameworkRoot = Resolve-FrameworkReferenceRoot

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($csc)) {
    throw 'C# compiler not found in the .NET Framework v4.0.30319 folders.'
}

$references = @(
    (Join-Path $ntBin 'NinjaTrader.Core.dll'),
    (Join-Path $ntBin 'NinjaTrader.Gui.dll'),
    (Join-Path $frameworkRoot 'mscorlib.dll'),
    (Join-Path $frameworkRoot 'System.dll'),
    (Join-Path $frameworkRoot 'System.Core.dll'),
    (Join-Path $frameworkRoot 'System.Xml.dll'),
    (Join-Path $frameworkRoot 'WindowsBase.dll'),
    (Join-Path $frameworkRoot 'PresentationCore.dll'),
    (Join-Path $frameworkRoot 'PresentationFramework.dll'),
    (Join-Path $frameworkRoot 'System.Xaml.dll')
)

foreach ($reference in $references) {
    Resolve-RequiredPath $reference 'Compile reference' | Out-Null
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$compilerArgs = @(
    '/nologo',
    '/noconfig',
    '/nostdlib',
    '/target:library',
    "/out:$OutputPath"
)

$compilerArgs += $references | ForEach-Object { "/reference:$_" }
$compilerArgs += $source

& $csc @compilerArgs
if ($LASTEXITCODE -ne 0) {
    throw "NinjaScript compile check failed with exit code $LASTEXITCODE."
}

Write-Host "NinjaScript compile check passed: $OutputPath"

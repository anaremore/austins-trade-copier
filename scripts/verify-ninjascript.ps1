param(
    [string] $SourcePath = (Join-Path $PSScriptRoot '..\austins-trade-copier.cs'),
    [string] $NinjaTraderBin = 'C:\Program Files\NinjaTrader 8\bin',
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

function Resolve-FrameworkReferenceRoot {
    $root = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework'
    foreach ($version in @('v4.8', 'v4.7.2', 'v4.0')) {
        $candidate = Join-Path $root $version
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).ProviderPath
        }
    }

    throw "No supported .NET Framework reference assemblies found under $root"
}

$source = Resolve-RequiredPath $SourcePath 'NinjaScript source'
$ntBin = Resolve-RequiredPath $NinjaTraderBin 'NinjaTrader bin folder'
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

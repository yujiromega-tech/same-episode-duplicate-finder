param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sources = @(
    (Join-Path $root "src\Models.cs"),
    (Join-Path $root "src\RecommendationScorer.cs"),
    (Join-Path $root "src\SameEpisodeDuplicateFinder.cs")
)
$assemblyInfo = Join-Path $root "Properties\AssemblyInfo.cs"
$dist = Join-Path $root "dist"
$output = Join-Path $dist "SameEpisodeDuplicateFinder.exe"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (!(Test-Path -LiteralPath $csc)) {
    throw "The .NET Framework compiler was not found at $csc"
}

foreach ($source in $sources) {
    if (!(Test-Path -LiteralPath $source)) {
        throw "Source file not found: $source"
    }
}

if (!(Test-Path -LiteralPath $assemblyInfo)) {
    throw "Assembly info file not found: $assemblyInfo"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

& $csc `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /out:$output `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Data.dll `
    /reference:System.Drawing.dll `
    /reference:System.Security.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Xml.dll `
    /reference:Microsoft.VisualBasic.dll `
    $assemblyInfo `
    $sources

Write-Host "Built $output"

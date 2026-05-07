param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sources = @(
    (Join-Path $root "src\EpisodeParser.cs"),
    (Join-Path $root "src\Models.cs"),
    (Join-Path $root "src\RecommendationScorer.cs"),
    (Join-Path $root "src\SameEpisodeDuplicateFinder.cs")
)
$assemblyInfo = Join-Path $root "Properties\AssemblyInfo.cs"
$tests = Join-Path $root "tests\UnitTests.cs"
$dist = Join-Path $root "dist"
$output = Join-Path $dist "SameEpisodeDuplicateFinder.Tests.exe"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

foreach ($path in @($csc, $assemblyInfo, $tests) + $sources) {
    if (!(Test-Path -LiteralPath $path)) {
        throw "Required file not found: $path"
    }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

& $csc `
    /nologo `
    /target:exe `
    /platform:anycpu `
    /optimize+ `
    /main:SameEpisodeDuplicateFinder.Tests.UnitTestRunner `
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
    $sources `
    $tests

if ($LASTEXITCODE -ne 0) {
    throw "C# compiler failed with exit code $LASTEXITCODE"
}

& $output
if ($LASTEXITCODE -ne 0) {
    throw "Unit tests failed with exit code $LASTEXITCODE"
}

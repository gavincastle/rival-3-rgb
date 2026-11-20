$ErrorActionPreference = 'Stop'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET'
$compiler = @(
    (Join-Path $frameworkRoot 'Framework64\v4.0.30319\csc.exe')
    (Join-Path $frameworkRoot 'Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1

if (-not $compiler) {
    throw 'The Windows .NET Framework C# compiler could not be found.'
}

$outputDir = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$executable = Join-Path $outputDir 'Rival 3 RGB.exe'
$sources = @('LightingWindow.cs', 'PalettePreferences.cs', 'RivalMouse.cs') |
    ForEach-Object { Join-Path $PSScriptRoot $_ }

& $compiler /nologo /target:winexe /optimize+ /warnaserror "/out:$executable" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @sources

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Get-Item -LiteralPath $executable

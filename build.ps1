# SPDX-FileCopyrightText: 2026 Hemingchu
# SPDX-License-Identifier: MIT
param([switch]$RunTests)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$outputDirectory = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$outputFile = Join-Path $outputDirectory 'RecentWidget.exe'
$compileArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$outputFile", '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', (Join-Path $PSScriptRoot 'RecentWidget.cs'))
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output 'Build succeeded: build\RecentWidget.exe'
if ($RunTests) { & (Join-Path $PSScriptRoot 'tools\Test.ps1') }

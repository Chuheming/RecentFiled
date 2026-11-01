# SPDX-FileCopyrightText: 2026 Hemingchu
# SPDX-License-Identifier: MIT
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$testOutput = Join-Path $projectRoot 'build\tests'
New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
$testNames = @('AnimationRenderTest', 'OpenTargetTest', 'ParticleLayoutTest', 'RefreshIntervalTest', 'ResizeTest')
Push-Location $testOutput
try {
    foreach ($testName in $testNames) {
        $executable = Join-Path $testOutput ($testName + '.exe')
        $compileArgs = @('/nologo', "/main:$testName", "/out:$executable", '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', (Join-Path $projectRoot 'RecentWidget.cs'), (Join-Path $projectRoot ('tests\' + $testName + '.cs')))
        & $compiler @compileArgs
        if ($LASTEXITCODE -ne 0) { throw "Compile failed: $testName" }
        & $executable
        if ($LASTEXITCODE -ne 0) { throw "Test failed: $testName" }
    }
} finally { Pop-Location }

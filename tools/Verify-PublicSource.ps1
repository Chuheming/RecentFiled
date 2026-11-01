# SPDX-FileCopyrightText: 2026 Hemingchu
# SPDX-License-Identifier: MIT
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$allowedFiles = @(
    '.gitignore', 'LICENSE', 'README.md', 'PRIVACY.md', 'THIRD_PARTY.md', 'CONTRIBUTING.md',
    'build.ps1', 'RecentWidget.cs', 'docs/preview.png', 'docs/LICENSE-NOTES.zh-CN.md',
    'tools/Test.ps1', 'tools/Verify-PublicSource.ps1',
    'tests/AnimationRenderTest.cs', 'tests/OpenTargetTest.cs', 'tests/ParticleLayoutTest.cs',
    'tests/RefreshIntervalTest.cs', 'tests/ResizeTest.cs'
)
$privatePatterns = @('(?i)(?<![a-z0-9])[a-z]:[\\/]', '(?i)wxid_[a-z0-9_]+', '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----', '(?i)github_pat_[a-z0-9_]+', '(?i)gh[pousr]_[a-z0-9]{20,}')
$localName = [Environment]::UserName
if ($localName -and $localName -ne 'Hemingchu') { $privatePatterns += '(?i)(?<![a-z0-9])' + [regex]::Escape($localName) + '(?![a-z0-9])' }
foreach ($relative in $allowedFiles) {
    $file = Join-Path $projectRoot $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing public file: $relative" }
    if ([IO.Path]::GetExtension($file) -eq '.png') { continue }
    $content = [IO.File]::ReadAllText($file)
    foreach ($pattern in $privatePatterns) {
        if ($content -match $pattern) { throw "Potential private data in: $relative (matched content is not printed)" }
    }
}
$allFiles = Get-ChildItem -LiteralPath $projectRoot -Recurse -Force -File
foreach ($file in $allFiles) {
    $relative = $file.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
    if ($relative.StartsWith('build/') -or $relative.StartsWith('.git/')) { continue }
    if ($relative -notin $allowedFiles) { throw "Unexpected public file: $relative" }
}
Write-Output ('PASS: ' + $allowedFiles.Count + ' approved public files; no selected private-data patterns found.')
Write-Output 'The preview must also be visually checked. This scan does not inspect Git history or account settings.'

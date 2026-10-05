$ErrorActionPreference = 'Stop'
$output = 'QobuzDownloaderX/bin/Release'
foreach ($required in @('QobuzDownloaderX.exe', 'QobuzDownloaderX.exe.config', 'taglib-sharp.dll', 'Qo(penAPI).dll', 'themes.json', 'languages/zh-cn.json')) {
    if (!(Test-Path (Join-Path $output $required))) { throw "Missing application file: $required" }
}
# Package the exact decoder used in the checks. Tests start it directly; normal
# application launches find it beside the executable through Windows search.
Copy-Item $env:QBDLX_TEST_FLAC (Join-Path $output 'flac.exe')
Copy-Item '.ci-tools/flac-source/COPYING*' $output
$sourceArchive = Join-Path $PWD (Join-Path $output 'flac-1.5.0-source.zip')
git -C .ci-tools/flac-source archive --format=zip --output=$sourceArchive 1507800de4b70e21be71f38caa0d9079d0bc6e45
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item 'docs/FLAC-build.txt' $output
Copy-Item 'docs/下载修复说明.md' (Join-Path $output '修复说明.md')
Copy-Item 'docs/GitHub自动测试说明.md' (Join-Path $output '自动测试说明.md')
Compress-Archive -Path "$output/*" -DestinationPath 'artifacts/QobuzDownloaderX-Windows.zip' -Force
Get-FileHash 'artifacts/QobuzDownloaderX-Windows.zip' -Algorithm SHA256 | Format-List | Out-File 'artifacts/Windows-package-SHA256.txt'

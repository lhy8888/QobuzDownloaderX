$ErrorActionPreference = 'Stop'
$source = Join-Path $PWD '.ci-tools/flac-source'
$build = Join-Path $PWD '.ci-tools/flac-build'
cmake -S $source -B $build -DBUILD_CXXLIBS=OFF -DBUILD_EXAMPLES=OFF -DBUILD_DOCS=OFF -DBUILD_TESTING=OFF -DWITH_OGG=OFF -DBUILD_SHARED_LIBS=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cmake --build $build --config Release --target flacapp --parallel 2
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$executables = @(Get-ChildItem $build -Recurse -File | Where-Object { $_.Name -eq 'flac' -or $_.Name -eq 'flac.exe' })
if ($executables.Count -ne 1) { throw 'Expected exactly one compiled official FLAC executable.' }
$decoder = $executables[0].FullName
& $decoder --version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
"QBDLX_TEST_FLAC=$decoder" | Out-File $env:GITHUB_ENV -Encoding utf8 -Append
$executables[0].DirectoryName | Out-File $env:GITHUB_PATH -Encoding utf8 -Append

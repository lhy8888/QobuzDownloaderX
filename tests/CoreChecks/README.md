# Core download regression checks

The console runner links the production HTTP, metadata, isolation, identity/quality, file receipt, credential protection, logging and FLAC validation code. UI adapters provide only the unavailable desktop controls. HTTP requests use simulated responses; these are not live Qobuz tests. The audio fixtures are generated one-second tones, with no account data or copyrighted music.

With .NET SDK 8 installed, from the repository root (the test project restores its exact-version dependencies automatically):

```sh
dotnet run --project tests/CoreChecks/CoreChecks.csproj
```

On Linux this executes 80 checks. Three process-exit/cancellation checks use small fake executables. To add real complete-file FLAC checks, point to the official FLAC executable:

```sh
QBDLX_TEST_FLAC=/absolute/path/to/flac dotnet run --project tests/CoreChecks/CoreChecks.csproj
```

That executes 83 checks on Linux, including corruption detection, preserving valid compressed bytes, repairing only an unset MD5, and a 200-item simulated batch with distinct tags and injected failures. On Windows the three Linux fake-executable checks are omitted, yielding 80 checks with the real decoder. An unset variable means the three real decoder checks did not run. CI sets `QBDLX_REQUIRE_FLAC=1` so a missing real decoder fails. The runner exits nonzero for a failing check and reports the executed count. Set `QBDLX_TEST_REPORT` to an XML path to save per-check results; Actions also displays them in its job summary.

The fixtures were generated using:

```sh
ffmpeg -f lavfi -i sine=frequency=1000:sample_rate=44100:duration=1 -ac 2 -c:a flac -sample_fmt s16 Fixtures/tone.flac
ffmpeg -f lavfi -i sine=frequency=1000:sample_rate=44100:duration=1 -ac 2 -c:a libmp3lame -b:a 320k Fixtures/tone.mp3
```

The test project uses the .NET 6 build of the same pinned Newtonsoft.Json package for SDK compatibility; the desktop application continues using its existing .NET Framework build. The application additionally pins NLayer 3.0.0 for strict per-frame MP3 decoding; its MIT license is packaged.

`tests/WindowsChecks` additionally tests the compiled .NET Framework application on Windows: window construction/resources, native file moves, image/tag writing, 100 sequential tags and real FLAC validation, credential protection, missing optional metadata, old credential migration, long Chinese paths, simultaneous window/log construction, stopping between batch items, the real main window opening on the UI thread, and a 200-track actual download queue using simulated HTTP responses, with skip/stop, cover reuse, metadata errors, save rollback, MP3 corruption, settings and responsive thumbnail loading. See `docs/GitHub自动测试说明.md` for the workflow and remaining live validation: full WinForms lifetime during authenticated downloads, network shares, disk exhaustion, multiple instances writing the same target, and authenticated long-duration Qobuz downloads. MP3 checks parse every frame and use the decoder directly; the permissive resynchronizing playback reader is not used.

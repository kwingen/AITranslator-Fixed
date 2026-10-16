# Verification — AITranslator-Fixed

## Verified locally

- Windows .NET SDK 8.0.426; target remains `net8.0-windows`.
- Locked dependency restore succeeded for the WPF application, LLamaSelect and regression project.
- Full solution Release rebuild succeeded (0 errors). Existing compiler warnings remain; this is not a warning-free modernization.
- Final integrated Release regression run: **301 executed, 301 passed, 0 failed, 0 skipped**, after review corrections. The complete locked restore / non-incremental solution build / tests / publish pipeline was executed via `scripts/verify-windows.ps1`.
- Actual native current-user DPAPI exercised, not simulated encryption. All credential fixtures use isolated storage with synthetic values.
- Framework-dependent publish succeeded. A copied published apphost was launched under a distinct executable name and UDP port to avoid the user's running installation; main window `AI Translator` was present and responsive. This is a startup smoke test, not a full interactive UI test.
- No real/paid API calls, real GPU model load, or production translation data were used.

## Coverage

Regression tests execute the real WPF project code: read-only imports, atomic checkpoint rotation/failures, primary/backup precedence, backup-only resumption, cue IDs/timestamps/coverage, retry result selection, stable TPP keys, TXT export, output path collisions/traversal, failed-checkpoint clearing, config recovery, response parsing, retry classification/timing/cancellation, parameter construction, adaptive history/batch retries, and DPAPI credential migration/destination binding.

## Boundaries

- Windows CI is provided as `ci/windows.yml`, **not active on GitHub** because the current OAuth token lacks `workflow` permission. Local locked restore/build/test/publish commands were actually executed; they are not a substitute for a claimed remote CI pass.
- The shipped build is for an external OpenAI-compatible server. It does not bundle an updated native llama backend or a model.
- Exact token budgeting, RPM/TPM scheduling, safe long-single-item splitting, isolated concurrency, .NET 10 migration, script sandbox/approval enforcement, and GPU compatibility remain future work. Existing `.csx` files are executable, trusted code only.
- Tests cannot establish crash durability under every filesystem, storage firmware, disk-full or sudden-power-loss condition; checkpoint tests cover deterministic I/O failures, not destructive physical power-cut experiments.

## Reproduction

```powershell
dotnet restore AITranslator.sln --locked-mode
dotnet build AITranslator.sln -c Release --no-restore --no-incremental
dotnet test AITranslator.Tests/AITranslator.Tests.csproj -c Release --no-build --no-restore --logger trx --results-directory TestResults
dotnet publish AITranslator/AITranslator.csproj -c Release --no-restore --self-contained false -o dist/AITranslator-Fixed
```

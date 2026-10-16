# Persistence safety contract

## Read APIs and trust boundary

- `JsonPersister.Load<T>`, `TxtPersister.Load`, `SrtPersister.Load`, and `CsvPersister.Load` read only the exact requested file. They never probe, move, delete, or overwrite an adjacent backup. Folder CSV imports likewise use the read-only API.
- App-owned checkpoint/config callers explicitly opt in with `JsonPersister.LoadInternal<T>(path)`, `TxtPersister.LoadInternal(path)`, or `SrtPersister.LoadInternal(path)`. Check existence with `CheckpointFile.Exists(path)`, **not** `File.Exists(path)`, before initializing an empty state or a default configuration.
- The internal reader tries the primary, `<path>.bak`, then the legacy `<stem>_bak<extension>`. A valid primary always wins, even if a backup is newer. This deliberately preserves manually edited successful/failed translations. Recovery returns data without rewriting any candidate.
- JSON `null`/empty content is not a valid internal checkpoint. Empty collections remain valid, including intentionally cleared failed records. TXT and SRT empty collections remain valid; without a schema/hash, an intentionally empty file cannot be distinguished from some kinds of truncation.
- `LoadInternal` is a caller trust boundary, not a path sandbox. Use it only for paths already known to be app-managed. No automatic recovery is applied to selected external input/template files. Legacy unmarked backups are accepted only by this explicit API for compatibility.

## Save protocol

1. Serialize completely before touching the filesystem.
2. Create a GUID-named `.tmp` file in the destination directory using `CreateNew`, `FileShare.None`, and `WriteThrough`.
3. Flush the text writer and call `FileStream.Flush(flushToDisk: true)` before commit.
4. For an existing primary, use `File.Replace`; retain the previous parseable primary as `<path>.bak`. Do not rotate a known corrupt/null primary over an existing recovery file. For a missing primary, use a non-overwriting same-directory `File.Move`, leaving backup-only recovery files intact.
5. Clean up the staging file on ordinary exceptions. Never delete the destination as part of saving. Do not fall back to a delete/copy sequence if the filesystem does not support replacement.

The existing `hide` option is preserved. Windows replacement retains destination attributes, so metadata changes are made before commit rather than risking an attribute error after data has committed.

## Compatibility decisions

- Existing file schemas and state filenames are unchanged; no migration rewrites are required.
- New backup names append `.bak`, rather than using `_bak` before the extension. This prevents generated CSV backups from becoming additional `*.csv` input files. Legacy `_bak` checkpoint/config files remain readable and are not deleted automatically.
- No hash sidecar is enforced on editable primary files. This preserves manual correction workflows. Recovery validates parsing/nullness, not semantic completeness or source identity. Legacy backups have no transaction marker; they are trusted only at explicit app-owned callsites.
- Power-loss guarantees remain subject to Windows/filesystem/storage behavior. The protocol removes the intentional missing-primary interval and requests durable file flushing; actual power cuts, physical disk exhaustion, and directory-metadata persistence were not hardware-tested.

## Regression verification

Run against the actual WPF assembly:

```sh
dotnet test AITranslator.Tests/AITranslator.Tests.csproj -c Release --filter FullyQualifiedName~PersistenceTests
```

Test-first cycles observed actual behavioral failures before their fixes: external imports returned stale backup text (4 cases); backup-only TranslateData startup initialized no cleaned/progress state (4 cases); saves discarded their previous version (4 cases); null/empty JSON bypassed recovery (2 cases); incomplete SRT threw `IndexOutOfRangeException` (1 case); corrupt primaries replaced good recovery files (3 cases); corrupt new backups blocked valid legacy recovery (2 cases); Windows replacement lost the requested Hidden flag (1 case).

The final owned suite passes **46 tests**. Additional real-filesystem checks cover primary/backup sharing violations, backup-only commit-path conflicts, serialization errors, staging cleanup, editable-primary precedence, current and legacy backup-only startup for all four formats, and CSV-folder backup exclusion. Existing project nullable/unused-variable warnings remain outside this change's acceptance criteria.

内部配置集成：`TranslationTask.ReadConfig` 和 `ViewModelManager.LoadBaseConfig` 已使用 `JsonPersister.LoadInternal<T>`；基础配置的默认初始化判断使用 `CheckpointFile.Exists`。外部模板/导入仍保持只读，不自动探测邻接备份。

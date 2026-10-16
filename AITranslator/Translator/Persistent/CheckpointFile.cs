using System.IO;
using System.Text;
using AITranslator.Exceptions;

namespace AITranslator.Translator.Persistent
{
    /// <summary>
    /// Explicit opt-in for app-owned state only. External imports must call Load instead.
    /// Legacy _bak files are accepted here for interrupted saves from earlier releases.
    /// A readable primary always wins, including manually edited failed translations.
    /// Recovery never moves, deletes or overwrites either candidate.
    /// </summary>
    public static class CheckpointFile
    {
        // A trailing extension keeps generated backups out of *.csv source imports.
        public static string BackupPath(string filePath) => Path.GetFullPath(filePath) + ".bak";

        private static string LegacyBackupPath(string filePath)
        {
            string path = Path.GetFullPath(filePath);
            return Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_bak" + Path.GetExtension(path));
        }

        public static bool Exists(string filePath) => File.Exists(filePath) || File.Exists(BackupPath(filePath)) || File.Exists(LegacyBackupPath(filePath));

        internal static void WriteAllText(string filePath, string contents, bool hide, Func<string, bool> isValid)
        {
            string path = Path.GetFullPath(filePath);
            string temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Same-volume staging plus a durable flush before the atomic commit.
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
                    {
                        writer.Write(contents);
                        writer.Flush();
                    }
                    stream.Flush(flushToDisk: true);
                }
                if (hide)
                    File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.Hidden);
                if (File.Exists(path))
                {
                    bool validPrimary;
                    try { validPrimary = isValid(path); }
                    catch (KnownException) { validPrimary = false; }
                    // ReplaceFile preserves destination attributes, not staging attributes.
                    // Apply requested metadata before commit so an attribute failure cannot follow a committed save.
                    if (hide)
                        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
                    // Never rotate a known corrupt/null primary over the last good recovery file.
                    File.Replace(temporary, path, validPrimary ? BackupPath(path) : null);
                }
                else
                    File.Move(temporary, path); // Never overwrite a concurrently created primary.
            }
            finally
            {
                // A failed staging/commit must not modify the primary or its recovery file.
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static T Load<T>(string filePath, Func<string, T> read)
        {
            KnownException? failure = null;
            foreach (string candidate in new[] { filePath, BackupPath(filePath), LegacyBackupPath(filePath) })
            {
                if (!File.Exists(candidate))
                    continue;
                try
                {
                    return read(candidate);
                }
                catch (KnownException error)
                {
                    failure ??= error;
                }
            }
            if (failure is not null)
                throw failure;
            return read(filePath); // Preserve the persister's normal missing-file error.
        }
    }
}

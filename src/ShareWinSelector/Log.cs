using System.IO;

namespace ShareWinSelector;

/// <summary>%LOCALAPPDATA%\ShareWinSelector\app.log への簡易ログ（スレッドセーフ、1MB 超でローテート）。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1024 * 1024;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShareWinSelector", "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ログ失敗でアプリを落とさない
        }
    }
}

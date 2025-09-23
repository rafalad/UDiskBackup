using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace UDiskBackup;

public record SourceStatus(string Path, bool Exists, bool Readable, long? UsedBytes);

public class SourceService
{
    public async Task<SourceStatus> GetStatusAsync(string path = "/mnt/shared")
    {
        bool exists = Directory.Exists(path);
        bool readable = false;
        long? usedBytes = null;

        if (exists)
        {
            // szybki test czytelności: spróbuj enumeracji
            try { _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault(); readable = true; }
            catch { readable = false; }

            // spróbuj pobrać rozmiar zajętości przez 'du -sb PATH'
            try
            {
                var (exit, stdout, _) = await Run("du", $"-sb \"{path}\"");
                if (exit == 0)
                {
                    var first = stdout.Trim().Split('\t', 2);
                    if (first.Length > 0 && long.TryParse(first[0], out var b)) usedBytes = b;
                }
            }
            catch { /* du nieobecne albo brak uprawnień – trudno */ }
        }

        return new SourceStatus(path, exists, readable, usedBytes);
    }

    private static async Task<(int exit, string stdout, string stderr)> Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        var so = await p.StandardOutput.ReadToEndAsync();
        var se = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, so, se);
    }
}

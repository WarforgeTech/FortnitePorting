using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using FortnitePorting.Mcp.Config;
using Serilog;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// Headless port of the GUI's DependencyService. binkadec/radadec ship as embedded
/// resources instead of avares assets; vgmstream is downloaded lazily on first use
/// so a plain load never touches the network for it.
/// </summary>
public class DependencyManager(McpConfig config)
{
    private const string VgmStreamUrl = "https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win.zip";

    private static readonly Assembly Assembly = typeof(DependencyManager).Assembly;

    private readonly Lock _gate = new();
    private bool _ensuredEmbedded;
    private bool _ensuredVgmStream;

    public FileInfo BinkaDecoderFile => new(Path.Combine(config.DependencyFolder.FullName, "binka", "binkadec.exe"));
    public FileInfo RadaDecoderFile => new(Path.Combine(config.DependencyFolder.FullName, "rada", "radadec.exe"));
    public FileInfo VgmStreamFile => new(Path.Combine(VgmStreamFolder.FullName, "vgmstream-cli.exe"));
    public DirectoryInfo VgmStreamFolder => new(Path.Combine(config.DependencyFolder.FullName, "vgmstream"));

    /// <summary>Extracts the embedded audio decoders. Cheap and idempotent.</summary>
    public void EnsureEmbedded()
    {
        lock (_gate)
        {
            if (_ensuredEmbedded) return;

            EnsureResource("binkadec.exe", BinkaDecoderFile);
            EnsureResource("radadec.exe", RadaDecoderFile);
            _ensuredEmbedded = true;
        }
    }

    /// <summary>Downloads and unpacks vgmstream. Only called when audio conversion actually needs it.</summary>
    public void EnsureVgmStream()
    {
        lock (_gate)
        {
            if (_ensuredVgmStream) return;
            if (VgmStreamFile is { Exists: true, Length: > 0 })
            {
                _ensuredVgmStream = true;
                return;
            }

            try
            {
                VgmStreamFolder.Create();

                var zipPath = Path.Combine(VgmStreamFolder.FullName, "vgmstream-win.zip");
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                using (var stream = client.GetStreamAsync(VgmStreamUrl).GetAwaiter().GetResult())
                using (var fileStream = File.Create(zipPath))
                {
                    stream.CopyTo(fileStream);
                }

                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var targetPath = Path.Combine(VgmStreamFolder.FullName, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    entry.ExtractToFile(targetPath, overwrite: true);
                }

                _ensuredVgmStream = true;
            }
            catch (Exception e)
            {
                Log.Warning("Failed to provision vgmstream: {Message}", e.Message);
            }
        }
    }

    private static void EnsureResource(string fileName, FileInfo targetFile)
    {
        var resourceName = ResolveResourceName(fileName);
        if (resourceName is null)
        {
            Log.Warning("Embedded dependency {File} was not found in the assembly manifest", fileName);
            return;
        }

        using var resourceStream = Assembly.GetManifestResourceStream(resourceName);
        if (resourceStream is null)
        {
            Log.Warning("Failed to open embedded dependency {Resource}", resourceName);
            return;
        }

        var resourceBytes = new byte[resourceStream.Length];
        resourceStream.ReadExactly(resourceBytes);

        if (targetFile is { Exists: true, Length: > 0 }
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(targetFile.FullName))) == Convert.ToHexString(SHA256.HashData(resourceBytes)))
            return;

        targetFile.Directory?.Create();
        if (targetFile.Exists) targetFile.Delete();
        File.WriteAllBytes(targetFile.FullName, resourceBytes);
    }

    private static string? ResolveResourceName(string fileName)
    {
        return Assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
    }
}

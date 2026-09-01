using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Math;
using FortnitePorting.Exporting.Providers;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// Headless implementation of IExportAssetProvider, wiring FortnitePorting.Exporting
/// up to the MCP loader and dependency manager instead of the GUI services.
/// </summary>
public class HeadlessExportAssetProvider(HeadlessLoader loader, DependencyManager dependencies) : IExportAssetProvider
{
    public AbstractVfsFileProvider Provider => loader.Provider;

    public List<UAnimMontage> MaleLobbyMontages => loader.MaleLobbyMontages;
    public List<UAnimMontage> FemaleLobbyMontages => loader.FemaleLobbyMontages;

    public Dictionary<int, FColor> BeanstalkColors => loader.BeanstalkColors;
    public Dictionary<int, FLinearColor> BeanstalkMaterialProps => loader.BeanstalkMaterialProps;
    public Dictionary<int, FVector> BeanstalkAtlasTextureUVs => loader.BeanstalkAtlasTextureUVs;

    public FileInfo BinkaDecoderFile
    {
        get
        {
            dependencies.EnsureEmbedded();
            return dependencies.BinkaDecoderFile;
        }
    }

    public FileInfo RadaDecoderFile
    {
        get
        {
            dependencies.EnsureEmbedded();
            return dependencies.RadaDecoderFile;
        }
    }

    public FileInfo VgmStreamFile
    {
        get
        {
            dependencies.EnsureVgmStream();
            return dependencies.VgmStreamFile;
        }
    }
}

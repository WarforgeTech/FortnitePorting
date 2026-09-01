using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using FortnitePorting.CUE4Parse.Extensions;
using FortnitePorting.Exporting.Styles;
using Serilog;

namespace FortnitePorting.Mcp.Core;

/// <summary>One selectable option of a style channel, paired with the exporter object it maps to.</summary>
public sealed record StyleOption(string Name, ExportStyleBase Style);

/// <summary>One <c>ItemVariants</c> entry: a named channel with its options.</summary>
public sealed record StyleChannel(string Channel, string RawChannel, string VariantType, List<StyleOption> Options);

/// <summary>What the caller asked for: nothing (base look), everything, or one option per channel.</summary>
public sealed record StyleSelection
{
    public static readonly StyleSelection Everything = new() { All = true };

    public bool All { get; init; }

    /// <summary>Channel name -> option name. Both sides are matched loosely (case- and punctuation-insensitive).</summary>
    public Dictionary<string, string> ByChannel { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => !All && ByChannel.Count == 0;
}

/// <summary>
/// Headless port of the GUI's style pipeline
/// (<c>AssetInfo</c> ctor + <c>AssetStyleInfo</c> + <c>ExportService.ConvertStyles</c>).
///
/// <para>
/// The GUI builds an <c>AssetStyleInfo</c> per <c>ItemVariants</c> entry, each holding
/// <c>BaseStyleData</c> options, then converts the chosen ones into <see cref="ExportStyleBase"/>
/// for <c>ExportSession.CreateExport</c>. This class does exactly that minus the bitmaps: the
/// option objects handed to <c>MeshExport.ExportStyles</c> are the same <c>FStructFallback</c>s the
/// GUI would hand over, so a selected style produces byte-identical output to the GUI.
/// </para>
///
/// <para>
/// Selecting every option of every channel mirrors <c>AssetInfo.GetAllStyles()</c>, which is what a
/// GUI folder export does. Prefab "Individual Props" object styles are deliberately NOT part of this
/// resolver - <c>export_gallery</c> owns that path.
/// </para>
/// </summary>
public static class StyleResolver
{
    /// <summary>ItemVariants export type -> the property holding its options. Verbatim from AssetInfo.cs.</summary>
    public static string? OptionsPropertyFor(string variantExportType) => variantExportType switch
    {
        "FortCosmeticCharacterPartVariant" => "PartOptions",
        "FortCosmeticMaterialVariant" => "MaterialOptions",
        "FortCosmeticParticleVariant" => "ParticleOptions",
        "FortCosmeticMeshVariant" => "MeshOptions",
        "FortCosmeticGameplayTagVariant" => "GenericTagOptions",
        "FortCosmeticRichColorVariant" => "InlineVariant",
        "FortCosmeticMaterialParameterSetVariant" => "MaterialParameterSetChoices",
        "FortCosmeticMorphTargetVariant" => "MorphTargetOptions",
        "FortCosmeticLoadoutTagDrivenVariant" => "Variants",
        _ => null
    };

    /// <summary>Every style channel the asset exposes, in ItemVariants order.</summary>
    public static List<StyleChannel> ReadChannels(UObject asset)
    {
        var channels = new List<StyleChannel>();

        UObject[] variants;
        try { variants = asset.GetOrDefault("ItemVariants", Array.Empty<UObject>()); }
        catch { return channels; }

        foreach (var variant in variants)
        {
            if (variant is null) continue;

            string rawChannel;
            try { rawChannel = variant.GetOrDefault("VariantChannelName", new FText("Style")).Text; }
            catch { rawChannel = "Style"; }

            var optionsName = OptionsPropertyFor(variant.ExportType);
            if (optionsName is null) continue;

            var options = new List<StyleOption>();
            try
            {
                if (variant.ExportType is "FortCosmeticRichColorVariant")
                    options.AddRange(ReadColorSwatchOptions(variant));
                else if (variant.ExportType is "FortCosmeticMaterialParameterSetVariant")
                    options.AddRange(ReadParameterSetOptions(variant));
                else
                    options.AddRange(ReadStructOptions(variant, optionsName));
            }
            catch (Exception e)
            {
                Log.Debug("Style channel {Channel} of {Asset} failed to read: {Message}", rawChannel, asset.Name, e.Message);
            }

            if (options.Count == 0) continue;

            channels.Add(new StyleChannel(TitleCase(rawChannel), rawChannel, variant.ExportType, options));
        }

        return channels;
    }

    /// <summary>
    /// Turns a selection into the exact <see cref="ExportStyleBase"/> array
    /// <c>ExportSession.CreateExport</c> expects. Returns false with a caller-facing error when a
    /// channel or an option name does not exist.
    /// </summary>
    public static bool TryResolve(
        UObject asset, StyleSelection selection,
        out ExportStyleBase[] styles, out List<string> applied, out string? error)
    {
        styles = [];
        applied = [];
        error = null;

        if (selection.IsEmpty) return true;

        var channels = ReadChannels(asset);
        if (channels.Count == 0)
        {
            error = $"'{asset.Name}' exposes no style channels (no usable ItemVariants), so `styles` cannot be applied. "
                    + "Omit `styles` to export the base look, or call list_asset_styles first.";
            return false;
        }

        var chosen = new List<ExportStyleBase>();

        if (selection.All)
        {
            foreach (var channel in channels)
            foreach (var option in channel.Options)
            {
                chosen.Add(option.Style);
                applied.Add($"{channel.Channel}: {option.Name}");
            }

            styles = chosen.ToArray();
            return true;
        }

        foreach (var (requestedChannel, requestedOption) in selection.ByChannel)
        {
            var channel = channels.FirstOrDefault(c => Matches(c.Channel, requestedChannel) || Matches(c.RawChannel, requestedChannel));
            if (channel is null)
            {
                error = $"'{asset.Name}' has no style channel named \"{requestedChannel}\". "
                        + $"Available channels: {string.Join(", ", channels.Select(c => $"\"{c.Channel}\""))}. "
                        + "Call list_asset_styles for the options of each.";
                return false;
            }

            var option = channel.Options.FirstOrDefault(o => Matches(o.Name, requestedOption));
            if (option is null)
            {
                error = $"Channel \"{channel.Channel}\" of '{asset.Name}' has no option named \"{requestedOption}\". "
                        + $"Available options: {string.Join(", ", channel.Options.Select(o => $"\"{o.Name}\""))}.";
                return false;
            }

            chosen.Add(option.Style);
            applied.Add($"{channel.Channel}: {option.Name}");
        }

        styles = chosen.ToArray();
        return true;
    }

    // ---------------------------------------------------------------- option readers

    /// <summary>Port of AssetStyleInfo(channelName, FStructFallback[], ..., addDefault).</summary>
    private static IEnumerable<StyleOption> ReadStructOptions(UObject variant, string optionsName)
    {
        // Tag-driven variants get the GUI's synthetic "Universal" entry (an empty style struct) so
        // MeshExport.ExportStyles still evaluates the meta-tag queries with nothing applied.
        if (variant.ExportType.Equals("FortCosmeticLoadoutTagDrivenVariant", StringComparison.Ordinal))
            yield return new StyleOption("Universal", new ExportStructStyle { StyleData = new FStructFallback() });

        var options = variant.GetOrDefault<FStructFallback[]>(optionsName, []);
        foreach (var option in options)
        {
            if (option.GetOrDefault<FText?>("VariantName") is not { } variantName
                || variantName.Text.Equals("Empty", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = TitleCase(variantName.Text);
            if (string.IsNullOrWhiteSpace(name)) name = "Unnamed";

            yield return new StyleOption(name, new ExportStructStyle { StyleData = option });
        }
    }

    /// <summary>Port of AssetStyleInfo.ParseColorSwatchStyles.</summary>
    private static IEnumerable<StyleOption> ReadColorSwatchOptions(UObject variant)
    {
        var options = new List<StyleOption>();

        if (!variant.TryGetValue(out FStructFallback inlineVariant, "InlineVariant")
            || !inlineVariant.TryGetValue(out FStructFallback richColorVariant, "RichColorVar")
            || !richColorVariant.TryGetValue(out FSoftObjectPath swatchPath, "ColorSwatchForChoices")
            || !swatchPath.TryLoad(out var swatch)
            || swatch is null
            || !swatch.TryGetValue(out FStructFallback[] colorPairs, "ColorPairs"))
            return options;

        foreach (var pair in colorPairs)
        {
            var colorValue = pair.GetOrDefault<FLinearColor>("ColorValue");
            var colorName = pair.GetOrDefault("ColorName", new FName(colorValue.Hex));

            options.Add(new StyleOption(colorName.PlainText, new ExportColorStyle
            {
                StyleData = richColorVariant,
                ColorData = pair,
                IsParamSet = false
            }));
        }

        return options;
    }

    /// <summary>Port of AssetStyleInfo.ParseParamSetStyles.</summary>
    private static IEnumerable<StyleOption> ReadParameterSetOptions(UObject variant)
    {
        var options = new List<StyleOption>();

        if (!variant.TryGetValue(out FStructFallback inlineVariant, "InlineVariant")
            || !inlineVariant.TryGetValue(out UObject parameterSet, "MaterialParameterSetChoices")
            || !parameterSet.TryGetValue(out FStructFallback[] choices, "Choices"))
            return options;

        foreach (var choice in choices)
        {
            // The GUI skips choices with no UI tile colour, so the option lists stay in lockstep.
            if (!choice.TryGetValue(out FInstancedStruct tile, "UITileDisplayData")
                || tile.NonConstStruct is not { } tileStruct
                || !tileStruct.TryGetValue(out FLinearColor colorValue, "Color"))
                continue;

            var name = choice.GetOrDefault("DisplayName", new FText(colorValue.Hex)).Text;

            options.Add(new StyleOption(string.IsNullOrWhiteSpace(name) ? colorValue.Hex : name, new ExportColorStyle
            {
                StyleData = inlineVariant,
                ColorData = choice,
                IsParamSet = true
            }));
        }

        return options;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Loose name matching: exact (ignoring case) first, then ignoring every non-alphanumeric
    /// character so "Black &amp; Gold", "black and gold" and "BlackGold" all land on the same option.
    /// </summary>
    private static bool Matches(string candidate, string requested)
    {
        if (candidate.Equals(requested, StringComparison.OrdinalIgnoreCase)) return true;
        return Normalize(candidate).Equals(Normalize(requested), StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized.Replace("and", string.Empty);
    }

    public static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var parts = value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}

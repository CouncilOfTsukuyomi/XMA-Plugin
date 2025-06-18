using PluginManager.Core.Models;

namespace PluginManager.Plugins.XMA.Models;

public class XmaMods
{
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string ModUrl { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public XmaGender Gender { get; set; } = XmaGender.Unisex;

    /// <summary>
    /// Convert to PluginMod for the plugin manager
    /// </summary>
    public PluginMod ToPluginMod(string pluginSource)
    {
        return new PluginMod
        {
            Name = Name,
            ModUrl = ModUrl,
            DownloadUrl = DownloadUrl,
            ImageUrl = ImageUrl,
            Publisher = Publisher,
            Type = Type,
            PluginSource = pluginSource,
            UploadDate = DateTime.UtcNow, // TODO:
            Version = "1.0" // TODO:
        };
    }

}
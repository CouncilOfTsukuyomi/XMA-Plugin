using MessagePack;
using PluginManager.Core.Models;

namespace PluginManager.Plugins.XMA.Models;

[MessagePackObject]
public class XmaMods
{
    [Key(0)]
    public string Name { get; set; } = string.Empty;
    
    [Key(1)]
    public string Publisher { get; set; } = string.Empty;
    
    [Key(2)]
    public string Type { get; set; } = string.Empty;
    
    [Key(3)]
    public string ImageUrl { get; set; } = string.Empty;
    
    [Key(4)]
    public string ModUrl { get; set; } = string.Empty;
    
    [Key(5)]
    public string DownloadUrl { get; set; } = string.Empty;
    
    [Key(6)]
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
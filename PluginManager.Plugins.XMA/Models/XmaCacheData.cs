using MessagePack;

namespace PluginManager.Plugins.XMA.Models;

[MessagePackObject]
public class XmaCacheData
{
    [Key(0)]
    public List<XmaMods> Mods { get; set; } = new();

    [Key(1)]
    public DateTimeOffset ExpirationTime { get; set; }
}
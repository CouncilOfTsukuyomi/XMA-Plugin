namespace PluginManager.Plugins.XMA.Models;

public class XmaSearchParameters
{
    public List<int> ModTypes { get; set; } = new();
    public string SortBy { get; set; } = "rank";
    public string SortOrder { get; set; } = "desc";
    public int DtCompatibility { get; set; } = 1;
    public int Page { get; set; } = 1;
}

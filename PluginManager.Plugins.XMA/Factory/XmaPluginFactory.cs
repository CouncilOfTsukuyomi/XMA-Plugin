using Microsoft.Extensions.Logging;
using PluginManager.Core.Interfaces;

namespace PluginManager.Plugins.XMA.Factory;

public class XmaPluginFactory
{
    private readonly ILogger<XmaPlugin> _logger;
    private readonly HttpClient _httpClient;

    public XmaPluginFactory(ILogger<XmaPlugin> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public IModPlugin CreatePlugin()
    {
        return new XmaPlugin(_logger, _httpClient);
    }

    public bool CanCreatePlugin(string pluginId)
    {
        return pluginId == "xmamod-plugin";
    }

}
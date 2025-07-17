using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PluginManager.Core.Interfaces;
using PluginManager.Core.Models;
using PluginManager.Core.Plugins;
using PluginManager.Plugins.XMA.Factory;
using PluginManager.Plugins.XMA.Services;

namespace PluginManager.Plugins.XMA;

public class XmaPlugin : BaseModPlugin, IModPlugin
{
    private HttpClient? _httpClient;
    private XmaConfigurationService? _configService;
    private XmaCacheService? _cacheService;
    private XmaWebScrapingService? _webScrapingService;
    private XmaModEnrichmentService? _enrichmentService;
    private XmaCacheInvalidationService? _invalidationService;
    private string _xmaCacheFilePath = string.Empty;

    public override string PluginId => "xmamod-plugin";
    public override string DisplayName => "XIV Mod Archive";
    public override string Description => "XIV Mod Archive integration - browse and download FFXIV mods";
    public override string Version => "1.0.7";
    public override string Author => "Council of Tsukuyomi";

    public XmaPlugin() : base(NullLogger.Instance)
    {
    }

    public XmaPlugin(ILogger logger) : base(logger, TimeSpan.FromMinutes(30))
    {
    }

    public XmaPlugin(ILogger logger, HttpClient httpClient) : base(logger, TimeSpan.FromMinutes(30))
    {
        _httpClient = httpClient;
    }

    public override async Task InitializeAsync(Dictionary<string, object> configuration)
    {
        ThrowIfCancellationRequested();

        Logger.LogInformation("Initializing XIV Mod Archive plugin");
        Logger.LogInformation($"Configuration received: {string.Join(", ", configuration.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");

        _configService = new XmaConfigurationService(Logger);
        _configService.LoadConfiguration(configuration);

        _xmaCacheFilePath = Path.Combine(PluginDirectory, "xma_mods.cache");
        var cacheDuration = TimeSpan.FromMinutes(_configService.CacheDuration);

        var directory = Path.GetDirectoryName(_xmaCacheFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _cacheService = new XmaCacheService(Logger, _xmaCacheFilePath, cacheDuration, CancellationToken);
        _invalidationService = new XmaCacheInvalidationService(Logger, _cacheService);

        _invalidationService.InvalidateOnFilterSortChange(_configService.SearchParameters);
        _invalidationService.InvalidateOnCookieChange(_configService.CookieValue);

        var configHash = _configService.GetConfigurationHash(configuration);
        _invalidationService.InvalidateOnConfigChange(configHash);

        CreateHttpClient();
        CreateServices();

        Logger.LogInformation($"Performance settings - Concurrent requests: {_configService.ConcurrentDownloadRequests}, Parallel pages: {_configService.ParallelPageFetching}, Reduced delay: {_configService.ReducedDelayForParallel}");
        Logger.LogInformation($"Filter/Sort settings - Types: [{string.Join(", ", _configService.SearchParameters.ModTypes)}], Sort: {_configService.SearchParameters.SortBy} {_configService.SearchParameters.SortOrder}, DT Compat: {_configService.SearchParameters.DtCompatibility}");
        Logger.LogInformation("XIV Mod Archive plugin initialized successfully");
    }

    private void CreateHttpClient()
    {
        _httpClient?.Dispose();

        var factory = new XmaHttpClientFactory(_configService!.UserAgent, _configService.CookieValue, TimeSpan.FromSeconds(30));
        _httpClient = factory.CreateClient();
    }

    private void CreateServices()
    {
        _webScrapingService = new XmaWebScrapingService(Logger, _httpClient!, _configService!, CancellationToken);
        _enrichmentService = new XmaModEnrichmentService(Logger, _webScrapingService, _configService!, CancellationToken);
    }

    public override async Task<List<PluginMod>> GetRecentModsAsync()
    {
        try
        {
            ThrowIfCancellationRequested();

            Logger.LogDebug("Getting recent mods from XIV Mod Archive");

            _invalidationService!.InvalidateOnCookieChange(_configService!.CookieValue);

            var cachedData = _cacheService!.LoadFromFile();
            if (cachedData != null && cachedData.ExpirationTime > DateTimeOffset.Now)
            {
                Logger.LogDebug($"Returning {cachedData.Mods.Count} mods from XMA cache");
                return cachedData.Mods.Select(m => m.ToPluginMod(PluginId)).ToList();
            }

            Logger.LogDebug("XMA cache is empty or expired. Fetching new data...");

            var xmaMods = await _webScrapingService!.FetchModsAsync();

            ThrowIfCancellationRequested();

            Logger.LogDebug($"Fetched {xmaMods.Count} raw XMA mods before enrichment");

            Logger.LogDebug("Enriching mods with download links...");
            xmaMods = await _enrichmentService!.EnrichWithDownloadLinksAsync(xmaMods);

            ThrowIfCancellationRequested();

            Logger.LogDebug($"After enrichment: {xmaMods.Count} mods");

            var newCache = _cacheService.CreateCacheData(xmaMods);
            _cacheService.SaveToFile(newCache);

            var pluginMods = xmaMods.Select(m => m.ToPluginMod(PluginId)).ToList();

            Logger.LogInformation($"Retrieved {pluginMods.Count} recent mods from XIV Mod Archive");
            return pluginMods;
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("XMA plugin operation was cancelled");
            return new List<PluginMod>();
        }
    }

    protected override async Task<string?> GetModDownloadLinkAsync(string modUrl)
    {
        return _webScrapingService?.GetModDownloadLinkAsync(modUrl) != null 
            ? await _webScrapingService.GetModDownloadLinkAsync(modUrl) 
            : null;
    }

    protected override async Task OnDisposingAsync()
    {
        Logger.LogInformation("Cleaning up XMA plugin resources...");

        try
        {
            _httpClient?.Dispose();
            _httpClient = null;

            Logger.LogDebug("HttpClient disposed successfully");
            Logger.LogInformation("XMA plugin resources cleaned up successfully");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during XMA plugin cleanup");
            throw;
        }
    }
}
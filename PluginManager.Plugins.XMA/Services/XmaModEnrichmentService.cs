using Microsoft.Extensions.Logging;
using PluginManager.Plugins.XMA.Models;

namespace PluginManager.Plugins.XMA.Services;

public class XmaModEnrichmentService
{
    private readonly ILogger _logger;
    private readonly XmaWebScrapingService _webScrapingService;
    private readonly XmaConfigurationService _configService;
    private readonly CancellationToken _cancellationToken;

    public XmaModEnrichmentService(ILogger logger, XmaWebScrapingService webScrapingService, XmaConfigurationService configService, CancellationToken cancellationToken)
    {
        _logger = logger;
        _webScrapingService = webScrapingService;
        _configService = configService;
        _cancellationToken = cancellationToken;
    }

    public async Task<List<XmaMods>> EnrichWithDownloadLinksAsync(List<XmaMods> mods)
    {
        _logger.LogDebug($"Enriching {mods.Count} mods with download links, tags, versions, and update dates using {_configService.ConcurrentDownloadRequests} concurrent requests");

        var semaphore = new SemaphoreSlim(_configService.ConcurrentDownloadRequests, _configService.ConcurrentDownloadRequests);

        var tasks = mods.Select(async mod =>
        {
            await semaphore.WaitAsync(_cancellationToken);
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var (downloadLink, tags, lastVersionUpdate, version) = await _webScrapingService.GetModDetailsAsync(mod.ModUrl);
                
                return new XmaMods
                {
                    Name = mod.Name,
                    Publisher = mod.Publisher,
                    Type = mod.Type,
                    ImageUrl = mod.ImageUrl,
                    ModUrl = mod.ModUrl,
                    DownloadUrl = downloadLink ?? "",
                    Gender = mod.Gender,
                    Tags = tags,
                    LastVersionUpdate = lastVersionUpdate,
                    Version = version
                };
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }
}
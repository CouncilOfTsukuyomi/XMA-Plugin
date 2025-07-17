using Microsoft.Extensions.Logging;
using PluginManager.Plugins.XMA.Models;

namespace PluginManager.Plugins.XMA.Services;

public class XmaCacheInvalidationService
{
    private readonly ILogger _logger;
    private readonly XmaCacheService _cacheService;
    
    private List<int> _lastModTypes = new();
    private string _lastSortBy = "time_published";
    private string _lastSortOrder = "desc";
    private int _lastDtCompatibility = 1;
    private string? _lastCookieValue;
    private string? _lastConfigHash;

    public XmaCacheInvalidationService(ILogger logger, XmaCacheService cacheService)
    {
        _logger = logger;
        _cacheService = cacheService;
    }

    public void InvalidateOnCookieChange(string? currentCookie)
    {
        if (currentCookie != _lastCookieValue)
        {
            _logger.LogDebug("Cookie changed. Invalidating cached data.");
            _cacheService.InvalidateCache();
            _lastCookieValue = currentCookie;
        }
    }

    public void InvalidateOnFilterSortChange(XmaSearchParameters searchParams)
    {
        var modTypesChanged = !searchParams.ModTypes.SequenceEqual(_lastModTypes);
        var sortByChanged = searchParams.SortBy != _lastSortBy;
        var sortOrderChanged = searchParams.SortOrder != _lastSortOrder;
        var dtCompatChanged = searchParams.DtCompatibility != _lastDtCompatibility;

        if (modTypesChanged || sortByChanged || sortOrderChanged || dtCompatChanged)
        {
            _logger.LogDebug("Filter/sort configuration changed. Invalidating cached data.");

            if (modTypesChanged)
                _logger.LogDebug($"ModTypes changed from [{string.Join(", ", _lastModTypes)}] to [{string.Join(", ", searchParams.ModTypes)}]");
            if (sortByChanged)
                _logger.LogDebug($"SortBy changed from '{_lastSortBy}' to '{searchParams.SortBy}'");
            if (sortOrderChanged)
                _logger.LogDebug($"SortOrder changed from '{_lastSortOrder}' to '{searchParams.SortOrder}'");
            if (dtCompatChanged)
                _logger.LogDebug($"DtCompatibility changed from {_lastDtCompatibility} to {searchParams.DtCompatibility}");

            _cacheService.InvalidateCache();

            _lastModTypes = new List<int>(searchParams.ModTypes);
            _lastSortBy = searchParams.SortBy;
            _lastSortOrder = searchParams.SortOrder;
            _lastDtCompatibility = searchParams.DtCompatibility;
        }
    }

    public void InvalidateOnConfigChange(string configHash)
    {
        if (_lastConfigHash != null && _lastConfigHash != configHash)
        {
            _logger.LogDebug("Configuration changed. Invalidating cached data.");
            _cacheService.InvalidateCache();
        }

        _lastConfigHash = configHash;
    }
}
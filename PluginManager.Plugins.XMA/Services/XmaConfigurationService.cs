using Microsoft.Extensions.Logging;
using PluginManager.Plugins.XMA.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PluginManager.Plugins.XMA.Services;

public class XmaConfigurationService
{
    private readonly ILogger _logger;
    
    public string BaseUrl { get; private set; } = "https://www.xivmodarchive.com";
    public string? CookieValue { get; private set; }
    public TimeSpan RequestDelay { get; private set; } = TimeSpan.FromMilliseconds(1000);
    public int MaxRetries { get; private set; } = 3;
    public int MaxPages { get; private set; } = 2;
    public string UserAgent { get; private set; } = "XmaModPlugin/1.0.7";
    public int CacheDuration { get; private set; } = 10;
    public int ConcurrentDownloadRequests { get; private set; } = 8;
    public bool ParallelPageFetching { get; private set; } = true;
    public bool ReducedDelayForParallel { get; private set; } = true;
    public XmaSearchParameters SearchParameters { get; private set; } = new();

    public XmaConfigurationService(ILogger logger)
    {
        _logger = logger;
    }

    public void LoadConfiguration(Dictionary<string, object> configuration)
    {
        if (configuration.TryGetValue("BaseUrl", out var baseUrl))
            BaseUrl = baseUrl.ToString() ?? BaseUrl;

        if (configuration.TryGetValue("CookieValue", out var cookie))
            CookieValue = cookie.ToString();

        if (configuration.TryGetValue("RequestDelay", out var delay) && 
            int.TryParse(delay.ToString(), out var delayMs))
            RequestDelay = TimeSpan.FromMilliseconds(delayMs);

        if (configuration.TryGetValue("MaxRetries", out var retries) && 
            int.TryParse(retries.ToString(), out var maxRetries))
            MaxRetries = maxRetries;

        if (configuration.TryGetValue("UserAgent", out var userAgent))
            UserAgent = userAgent.ToString() ?? UserAgent;

        LoadMaxPages(configuration);
        LoadCacheDuration(configuration);
        LoadConcurrentRequests(configuration);
        LoadParallelSettings(configuration);
        LoadSearchParameters(configuration);
    }

    private void LoadMaxPages(Dictionary<string, object> configuration)
    {
        if (configuration.TryGetValue("MaxPages", out var maxPages))
        {
            if (int.TryParse(maxPages.ToString(), out var pageCount))
            {
                MaxPages = pageCount;
            }
            else
            {
                _logger.LogInformation($"MaxPages setting failed to parse: '{maxPages}' - using default: {MaxPages}");
            }
        }
    }

    private void LoadCacheDuration(Dictionary<string, object> configuration)
    {
        if (configuration.TryGetValue("CacheDuration", out var cacheDuration))
        {
            if (int.TryParse(cacheDuration.ToString(), out var cacheDurationMinutes))
            {
                CacheDuration = cacheDurationMinutes;
                _logger.LogInformation($"Cache duration set to {CacheDuration} minutes");
            }
            else
            {
                _logger.LogWarning($"CacheDuration setting failed to parse: '{cacheDuration}' - using default: {CacheDuration} minutes");
            }
        }
    }

    private void LoadConcurrentRequests(Dictionary<string, object> configuration)
    {
        if (configuration.TryGetValue("ConcurrentDownloadRequests", out var concurrentRequests))
        {
            if (int.TryParse(concurrentRequests.ToString(), out var concurrent))
            {
                ConcurrentDownloadRequests = concurrent;
                _logger.LogInformation($"Concurrent download requests set to {ConcurrentDownloadRequests}");
            }
            else
            {
                _logger.LogWarning($"ConcurrentDownloadRequests setting failed to parse: '{concurrentRequests}' - using default: {ConcurrentDownloadRequests}");
            }
        }
    }

    private void LoadParallelSettings(Dictionary<string, object> configuration)
    {
        if (configuration.TryGetValue("ParallelPageFetching", out var parallelPages))
        {
            if (bool.TryParse(parallelPages.ToString(), out var parallel))
            {
                ParallelPageFetching = parallel;
                _logger.LogInformation($"Parallel page fetching set to {ParallelPageFetching}");
            }
            else
            {
                _logger.LogWarning($"ParallelPageFetching setting failed to parse: '{parallelPages}' - using default: {ParallelPageFetching}");
            }
        }

        if (configuration.TryGetValue("ReducedDelayForParallel", out var reducedDelay))
        {
            if (bool.TryParse(reducedDelay.ToString(), out var reduced))
            {
                ReducedDelayForParallel = reduced;
                _logger.LogInformation($"Reduced delay for parallel requests set to {ReducedDelayForParallel}");
            }
            else
            {
                _logger.LogWarning($"ReducedDelayForParallel setting failed to parse: '{reducedDelay}' - using default: {ReducedDelayForParallel}");
            }
        }
    }

    private void LoadSearchParameters(Dictionary<string, object> configuration)
    {
        SearchParameters.ModTypes = ParseModTypes(configuration);
        ParseSortBy(configuration);
        ParseSortOrder(configuration);
        ParseDtCompatibility(configuration);
    }

    private List<int> ParseModTypes(Dictionary<string, object> configuration)
    {
        if (!configuration.TryGetValue("ModTypes", out var modTypes))
            return new List<int>();

        _logger.LogDebug($"ModTypes raw value: {modTypes} (Type: {modTypes?.GetType().Name})");

        var parsedTypes = new List<int>();

        if (modTypes is JsonElement jsonElement)
        {
            try
            {
                parsedTypes = ParseJsonElement(jsonElement);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to parse JsonElement ModTypes: {ex.Message}");
            }
        }
        else if (modTypes is IEnumerable<object> typesArray)
        {
            parsedTypes = typesArray
                .Select(t => t?.ToString())
                .Where(t => !string.IsNullOrEmpty(t) && int.TryParse(t, out _))
                .Select(int.Parse)
                .ToList();
        }
        else if (modTypes is string modTypesString)
        {
            parsedTypes = ParseModTypesString(modTypesString);
        }
        else if (modTypes != null)
        {
            parsedTypes = ParseModTypesFromObject(modTypes);
        }

        if (parsedTypes.Any())
        {
            _logger.LogInformation($"Mod types filter set to: {string.Join(", ", parsedTypes)}");
        }
        else
        {
            _logger.LogInformation("ModTypes could not be parsed or is empty - will fetch all mod types");
            _logger.LogDebug($"ModTypes parsing failed. Original value: '{modTypes}', Type: {modTypes?.GetType().Name}");
        }

        return parsedTypes;
    }

    private List<int> ParseJsonElement(JsonElement jsonElement)
    {
        var parsedTypes = new List<int>();

        if (jsonElement.ValueKind == JsonValueKind.Array)
        {
            parsedTypes = jsonElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Number)
                .Select(element => element.GetInt32())
                .ToList();
        }
        else if (jsonElement.ValueKind == JsonValueKind.Number)
        {
            parsedTypes.Add(jsonElement.GetInt32());
        }
        else if (jsonElement.ValueKind == JsonValueKind.String)
        {
            var stringValue = jsonElement.GetString();
            if (!string.IsNullOrEmpty(stringValue))
            {
                parsedTypes = stringValue.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => int.TryParse(s, out _))
                    .Select(int.Parse)
                    .ToList();
            }
        }

        return parsedTypes;
    }

    private List<int> ParseModTypesString(string modTypesString)
    {
        var parsedTypes = new List<int>();

        if (modTypesString.StartsWith("[") && modTypesString.EndsWith("]"))
        {
            try
            {
                var jsonContent = modTypesString.Trim('[', ']');
                var stringTypes = jsonContent.Split(',')
                    .Select(s => s.Trim().Trim('"'))
                    .Where(s => !string.IsNullOrEmpty(s));

                parsedTypes = stringTypes
                    .Where(s => int.TryParse(s, out _))
                    .Select(int.Parse)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to parse ModTypes JSON array: {ex.Message}");
            }
        }
        else
        {
            parsedTypes = modTypesString.Split(',')
                .Select(s => s.Trim())
                .Where(s => int.TryParse(s, out _))
                .Select(int.Parse)
                .ToList();
        }

        return parsedTypes;
    }

    private List<int> ParseModTypesFromObject(object modTypes)
    {
        var stringValue = modTypes.ToString();
        if (string.IsNullOrEmpty(stringValue))
            return new List<int>();

        if (int.TryParse(stringValue, out var singleType))
        {
            return new List<int> { singleType };
        }

        return stringValue.Split(',')
            .Select(s => s.Trim())
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .ToList();
    }

    private void ParseSortBy(Dictionary<string, object> configuration)
    {
        if (configuration.TryGetValue("SortBy", out var sortBy))
        {
            var sortByString = sortBy.ToString();
            if (!string.IsNullOrEmpty(sortByString))
            {
                var validSortOptions = new[] { "rank", "time_edited", "time_published", "name_slug", "views", "views_today", "downloads", "followers" };
                if (validSortOptions.Contains(sortByString))
                {
                    SearchParameters.SortBy = sortByString;
                    _logger.LogInformation($"Sort by set to: {SearchParameters.SortBy}");
                }
                else
                {
                    _logger.LogWarning($"Invalid SortBy value: '{sortByString}' - using default: {SearchParameters.SortBy}");
                }
            }
        }
    }

    private void ParseSortOrder(Dictionary<string, object> configuration)
    {
        if (configuration.TryGetValue("SortOrder", out var sortOrder))
        {
            var sortOrderString = sortOrder.ToString();
            if (sortOrderString == "asc" || sortOrderString == "desc")
            {
                SearchParameters.SortOrder = sortOrderString;
                _logger.LogInformation($"Sort order set to: {SearchParameters.SortOrder}");
            }
            else
            {
                _logger.LogWarning($"Invalid SortOrder value: '{sortOrderString}' - using default: {SearchParameters.SortOrder}");
            }
        }
    }

    private void ParseDtCompatibility(Dictionary<string, object> configuration)
    {
        if (configuration.TryGetValue("DtCompatibility", out var dtCompat))
        {
            if (int.TryParse(dtCompat.ToString(), out var dtCompatValue) && dtCompatValue >= 0 && dtCompatValue <= 3)
            {
                SearchParameters.DtCompatibility = dtCompatValue;
                _logger.LogInformation($"DT compatibility set to: {SearchParameters.DtCompatibility}");
            }
            else
            {
                _logger.LogWarning($"Invalid DtCompatibility value: '{dtCompat}' - using default: {SearchParameters.DtCompatibility}");
            }
        }
    }

    public string GetConfigurationHash(Dictionary<string, object> configuration)
    {
        var configString = string.Join("|", configuration.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(configString));
        return Convert.ToBase64String(hash);
    }
}
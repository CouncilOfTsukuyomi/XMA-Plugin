using System.Net;
using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PluginManager.Core.Interfaces;
using PluginManager.Core.Models;
using PluginManager.Core.Plugins;
using PluginManager.Plugins.XMA.Factory;
using PluginManager.Plugins.XMA.Models;

namespace PluginManager.Plugins.XMA;

public class XmaPlugin : BaseModPlugin, IModPlugin
{
    private HttpClient? _httpClient;
    private string _baseUrl = "https://www.xivmodarchive.com";
    private string? _cookieValue;
    private TimeSpan _requestDelay = TimeSpan.FromMilliseconds(1000);
    private int _maxRetries = 3;
    private int _maxPages = 2;
    private string _userAgent = "XmaModPlugin/1.0.7";
    private int _cacheDuration = 10;
    private int _concurrentDownloadRequests = 8;
    private bool _parallelPageFetching = true;
    private bool _reducedDelayForParallel = true;

    private List<int> _modTypes = new();
    private string _sortBy = "time_published";
    private string _sortOrder = "desc";
    private int _dtCompatibility = 1;

    private List<int> _lastModTypes = new();
    private string _lastSortBy = "time_published";
    private string _lastSortOrder = "desc";
    private int _lastDtCompatibility = 1;

    private string? _lastCookieValue;
    private string? _lastConfigHash;

    private string _xmaCacheFilePath = string.Empty;
    private static TimeSpan _xmaCacheDuration;

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
        
        LogInfo("Initializing XIV Mod Archive plugin");
        LogInfo($"Configuration received: {string.Join(", ", configuration.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");

        if (configuration.TryGetValue("BaseUrl", out var baseUrl))
            _baseUrl = baseUrl.ToString() ?? _baseUrl;

        if (configuration.TryGetValue("CookieValue", out var cookie))
            _cookieValue = cookie.ToString();

        if (configuration.TryGetValue("RequestDelay", out var delay) && 
            int.TryParse(delay.ToString(), out var delayMs))
            _requestDelay = TimeSpan.FromMilliseconds(delayMs);

        if (configuration.TryGetValue("MaxRetries", out var retries) && 
            int.TryParse(retries.ToString(), out var maxRetries))
            _maxRetries = maxRetries;

        if (configuration.TryGetValue("UserAgent", out var userAgent))
            _userAgent = userAgent.ToString() ?? _userAgent;
        
        if (configuration.TryGetValue("MaxPages", out var maxPages))
        {
            if (int.TryParse(maxPages.ToString(), out var pageCount))
            {
                _maxPages = pageCount;
            }
            else
            {
                LogInfo($"MaxPages setting failed to parse: '{maxPages}' - using default: {_maxPages}");
            }
        }

        if (configuration.TryGetValue("CacheDuration", out var cacheDuration))
        {
            if (int.TryParse(cacheDuration.ToString(), out var cacheDurationMinutes))
            {
                _cacheDuration = cacheDurationMinutes;
                LogInfo($"Cache duration set to {_cacheDuration} minutes");
            }
            else
            {
                LogWarning($"CacheDuration setting failed to parse: '{cacheDuration}' - using default: {_cacheDuration} minutes");
            }
        }
        
        if (configuration.TryGetValue("ConcurrentDownloadRequests", out var concurrentRequests))
        {
            if (int.TryParse(concurrentRequests.ToString(), out var concurrent))
            {
                _concurrentDownloadRequests = concurrent;
                LogInfo($"Concurrent download requests set to {_concurrentDownloadRequests}");
            }
            else
            {
                LogWarning($"ConcurrentDownloadRequests setting failed to parse: '{concurrentRequests}' - using default: {_concurrentDownloadRequests}");
            }
        }

        if (configuration.TryGetValue("ParallelPageFetching", out var parallelPages))
        {
            if (bool.TryParse(parallelPages.ToString(), out var parallel))
            {
                _parallelPageFetching = parallel;
                LogInfo($"Parallel page fetching set to {_parallelPageFetching}");
            }
            else
            {
                LogWarning($"ParallelPageFetching setting failed to parse: '{parallelPages}' - using default: {_parallelPageFetching}");
            }
        }

        if (configuration.TryGetValue("ReducedDelayForParallel", out var reducedDelay))
        {
            if (bool.TryParse(reducedDelay.ToString(), out var reduced))
            {
                _reducedDelayForParallel = reduced;
                LogInfo($"Reduced delay for parallel requests set to {_reducedDelayForParallel}");
            }
            else
            {
                LogWarning($"ReducedDelayForParallel setting failed to parse: '{reducedDelay}' - using default: {_reducedDelayForParallel}");
            }
        }

        if (configuration.TryGetValue("ModTypes", out var modTypes))
        {
            LogDebug($"ModTypes raw value: {modTypes} (Type: {modTypes?.GetType().Name})");
            
            var parsedTypes = new List<int>();
            
            if (modTypes is System.Text.Json.JsonElement jsonElement)
            {
                try
                {
                    if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        parsedTypes = jsonElement.EnumerateArray()
                            .Where(element => element.ValueKind == System.Text.Json.JsonValueKind.Number)
                            .Select(element => element.GetInt32())
                            .ToList();
                    }
                    else if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        parsedTypes.Add(jsonElement.GetInt32());
                    }
                    else if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
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
                }
                catch (Exception ex)
                {
                    LogWarning($"Failed to parse JsonElement ModTypes: {ex.Message}");
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
                        LogWarning($"Failed to parse ModTypes JSON array: {ex.Message}");
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
            }
            else if (modTypes != null)
            {
                var stringValue = modTypes.ToString();
                if (!string.IsNullOrEmpty(stringValue))
                {
                    if (int.TryParse(stringValue, out var singleType))
                    {
                        parsedTypes.Add(singleType);
                    }
                    else
                    {
                        parsedTypes = stringValue.Split(',')
                            .Select(s => s.Trim())
                            .Where(s => int.TryParse(s, out _))
                            .Select(int.Parse)
                            .ToList();
                    }
                }
            }
            
            if (parsedTypes.Any())
            {
                _modTypes = parsedTypes;
                LogInfo($"Mod types filter set to: {string.Join(", ", _modTypes)}");
            }
            else
            {
                LogInfo("ModTypes could not be parsed or is empty - will fetch all mod types");
                LogDebug($"ModTypes parsing failed. Original value: '{modTypes}', Type: {modTypes?.GetType().Name}");
            }
        }

        if (configuration.TryGetValue("SortBy", out var sortBy))
        {
            var sortByString = sortBy.ToString();
            if (!string.IsNullOrEmpty(sortByString))
            {
                var validSortOptions = new[] { "rank", "time_edited", "time_published", "name_slug", "views", "views_today", "downloads", "followers" };
                if (validSortOptions.Contains(sortByString))
                {
                    _sortBy = sortByString;
                    LogInfo($"Sort by set to: {_sortBy}");
                }
                else
                {
                    LogWarning($"Invalid SortBy value: '{sortByString}' - using default: {_sortBy}");
                }
            }
        }

        if (configuration.TryGetValue("SortOrder", out var sortOrder))
        {
            var sortOrderString = sortOrder.ToString();
            if (sortOrderString == "asc" || sortOrderString == "desc")
            {
                _sortOrder = sortOrderString;
                LogInfo($"Sort order set to: {_sortOrder}");
            }
            else
            {
                LogWarning($"Invalid SortOrder value: '{sortOrderString}' - using default: {_sortOrder}");
            }
        }

        if (configuration.TryGetValue("DtCompatibility", out var dtCompat))
        {
            if (int.TryParse(dtCompat.ToString(), out var dtCompatValue) && dtCompatValue >= 0 && dtCompatValue <= 3)
            {
                _dtCompatibility = dtCompatValue;
                LogInfo($"DT compatibility set to: {_dtCompatibility}");
            }
            else
            {
                LogWarning($"Invalid DtCompatibility value: '{dtCompat}' - using default: {_dtCompatibility}");
            }
        }
        
        _xmaCacheFilePath = Path.Combine(PluginDirectory, "xma_mods.cache");
        _xmaCacheDuration = TimeSpan.FromMinutes(_cacheDuration);
        
        LogInfo($"XMA cache duration set to {_xmaCacheDuration.TotalMinutes} minutes");
        LogInfo($"Performance settings - Concurrent requests: {_concurrentDownloadRequests}, Parallel pages: {_parallelPageFetching}, Reduced delay: {_reducedDelayForParallel}");
        LogInfo($"Filter/Sort settings - Types: [{string.Join(", ", _modTypes)}], Sort: {_sortBy} {_sortOrder}, DT Compat: {_dtCompatibility}");
        
        var directory = Path.GetDirectoryName(_xmaCacheFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        InvalidateCacheOnFilterSortChange();

        _lastCookieValue = _cookieValue;

        var configHash = GetConfigurationHash(configuration);
        InvalidateCacheOnConfigChange(configHash);
        
        CreateHttpClient();
        
        LogInfo("XIV Mod Archive plugin initialized successfully");
    }

    private void CreateHttpClient()
    {
        _httpClient?.Dispose();
        
        var factory = new XmaHttpClientFactory(_userAgent, _cookieValue, TimeSpan.FromSeconds(30));
        _httpClient = factory.CreateClient();
    }

    public override async Task<List<PluginMod>> GetRecentModsAsync()
    {
        try
        {
            ThrowIfCancellationRequested();
            
            LogDebug("Getting recent mods from XIV Mod Archive");

            InvalidateCacheOnCookieChange();

            var cachedData = LoadXmaCacheFromFile();
            if (cachedData != null && cachedData.ExpirationTime > DateTimeOffset.Now)
            {
                LogDebug($"Returning {cachedData.Mods.Count} mods from XMA cache");
                
                foreach (var cachedMod in cachedData.Mods)
                {
                    LogDebug($"Cached Mod: Name='{cachedMod.Name}', Publisher='{cachedMod.Publisher}', ImageUrl='{cachedMod.ImageUrl}', ModUrl='{cachedMod.ModUrl}', DownloadUrl='{cachedMod.DownloadUrl}'");
                }
                
                var cachedPluginMods = cachedData.Mods.Select(m => m.ToPluginMod(PluginId)).ToList();
                
                foreach (var pluginMod in cachedPluginMods)
                {
                    LogDebug($"PluginMod from cache: ModName='{pluginMod.Name}', Author='{pluginMod.Publisher}', ImageUrl='{pluginMod.ImageUrl}', ModUrl='{pluginMod.ModUrl}', DownloadUrl='{pluginMod.DownloadUrl}', PluginSource='{pluginMod.PluginSource}'");
                }
                
                return cachedPluginMods;
            }

            LogDebug("XMA cache is empty or expired. Fetching new data...");

            var xmaMods = await FetchRecentXmaModsAsync();
            
            ThrowIfCancellationRequested();
            
            LogDebug($"Fetched {xmaMods.Count} raw XMA mods before enrichment");
            foreach (var xmaMod in xmaMods.Take(3))
            {
                LogDebug($"Raw XMA Mod: Name='{xmaMod.Name}', Publisher='{xmaMod.Publisher}', ImageUrl='{xmaMod.ImageUrl}', ModUrl='{xmaMod.ModUrl}', DownloadUrl='{xmaMod.DownloadUrl}'");
            }
            
            LogDebug("Enriching mods with download links...");
            xmaMods = await EnrichWithDownloadLinksAsync(xmaMods);

            ThrowIfCancellationRequested();

            LogDebug($"After enrichment: {xmaMods.Count} mods");
            foreach (var enrichedMod in xmaMods.Take(3))
            {
                LogDebug($"Enriched XMA Mod: Name='{enrichedMod.Name}', Publisher='{enrichedMod.Publisher}', ImageUrl='{enrichedMod.ImageUrl}', ModUrl='{enrichedMod.ModUrl}', DownloadUrl='{enrichedMod.DownloadUrl}'");
            }
            
            var newCache = new XmaCacheData
            {
                Mods = xmaMods,
                ExpirationTime = DateTimeOffset.Now.Add(_xmaCacheDuration)
            };
            SaveXmaCacheToFile(newCache);

            var pluginMods = xmaMods.Select(m => m.ToPluginMod(PluginId)).ToList();

            LogInfo($"Final conversion: {pluginMods.Count} PluginMods ready to return");
            foreach (var pluginMod in pluginMods)
            {
                LogInfo($"FINAL PluginMod: ModName='{pluginMod.Name}', Author='{pluginMod.Publisher}', ImageUrl='{pluginMod.ImageUrl}', ModUrl='{pluginMod.ModUrl}', DownloadUrl='{pluginMod.DownloadUrl}', PluginSource='{pluginMod.PluginSource}', PublishedDate='{pluginMod.UploadDate}'");
            }

            LogInfo($"Retrieved {pluginMods.Count} recent mods from XIV Mod Archive");
            return pluginMods;
        }
        catch (OperationCanceledException)
        {
            LogInfo("XMA plugin operation was cancelled");
            return new List<PluginMod>();
        }
    }

    private async Task<List<XmaMods>> FetchRecentXmaModsAsync()
    {
        var retryCount = 0;
    
        while (retryCount <= _maxRetries)
        {
            try
            {
                ThrowIfCancellationRequested();
            
                if (retryCount > 0)
                    await Task.Delay(_requestDelay * retryCount, CancellationToken);

                List<XmaMods> allResults;

                if (_parallelPageFetching && _maxPages > 1)
                {
                    LogInfo($"Fetching mods from {_maxPages} pages in parallel");

                    var pageTasks = Enumerable.Range(1, _maxPages)
                        .Select(async page =>
                        {
                            ThrowIfCancellationRequested();
                            LogDebug($"Fetching page {page} of {_maxPages}");
                            var pageResults = await ParsePageAsync(page);
                            LogDebug($"Page {page} returned {pageResults.Count} mods");
                            return pageResults;
                        });

                    var pageResultsArray = await Task.WhenAll(pageTasks);
                    allResults = pageResultsArray.SelectMany(results => results).ToList();
                }
                else
                {
                    LogInfo($"Fetching mods from {_maxPages} pages sequentially");
                    allResults = new List<XmaMods>();

                    for (int page = 1; page <= _maxPages; page++)
                    {
                        ThrowIfCancellationRequested();
                        
                        LogDebug($"Fetching page {page} of {_maxPages}");
                        var pageResults = await ParsePageAsync(page);
                        LogDebug($"Page {page} returned {pageResults.Count} mods");
                        allResults.AddRange(pageResults);
                    }
                }

                LogInfo($"Total mods fetched before deduplication: {allResults.Count}");

                var distinctMods = allResults
                    .GroupBy(m => m.ImageUrl)
                    .Select(g => g.First())
                    .ToList();

                return distinctMods;
            }
            catch (OperationCanceledException)
            {
                LogInfo("Fetch operation was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                retryCount++;
                LogWarning(ex, $"Attempt {retryCount}/{_maxRetries} failed to fetch mods from XIV Mod Archive");

                if (retryCount > _maxRetries)
                {
                    LogError(ex, $"Failed to fetch mods after {_maxRetries} retries");
                    throw;
                }
            }
        }
        return new List<XmaMods>();
    }

    private async Task<List<XmaMods>> EnrichWithDownloadLinksAsync(List<XmaMods> mods)
    {
        LogDebug($"Enriching {mods.Count} mods with download links using {_concurrentDownloadRequests} concurrent requests");
        
        var semaphore = new SemaphoreSlim(_concurrentDownloadRequests, _concurrentDownloadRequests);

        var tasks = mods.Select(async mod =>
        {
            await semaphore.WaitAsync(CancellationToken);
            try
            {
                ThrowIfCancellationRequested();
                
                var downloadUrl = await GetModDownloadLinkAsync(mod.ModUrl);
                var enrichedMod = new XmaMods
                {
                    Name = mod.Name,
                    Publisher = mod.Publisher,
                    Type = mod.Type,
                    ImageUrl = mod.ImageUrl,
                    ModUrl = mod.ModUrl,
                    DownloadUrl = downloadUrl ?? "",
                    Gender = mod.Gender
                };
                return enrichedMod;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    protected override async Task<string?> GetModDownloadLinkAsync(string modUrl)
    {
        try
        {
            ThrowIfCancellationRequested();
            
            if (!modUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                modUrl = _baseUrl + modUrl;
            }
            
            if (_reducedDelayForParallel && _concurrentDownloadRequests > 1)
            {
                await Task.Delay(_requestDelay / 2, CancellationToken);
            }
            else
            {
                await Task.Delay(_requestDelay, CancellationToken);
            }
            
            var html = await _httpClient!.GetStringAsync(modUrl, CancellationToken);

            ThrowIfCancellationRequested();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var downloadNode = doc.DocumentNode.SelectSingleNode("//a[@id='mod-download-link']");
            if (downloadNode == null)
            {
                LogWarning($"No download anchor found on: {modUrl}");
                return null;
            }

            var hrefValue = downloadNode.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(hrefValue))
            {
                LogWarning($"Download link was empty or missing on: {modUrl}");
                return null;
            }

            hrefValue = WebUtility.HtmlDecode(hrefValue);

            if (hrefValue.StartsWith("/"))
            {
                hrefValue = _baseUrl + hrefValue;
            }

            hrefValue = Uri.UnescapeDataString(hrefValue);

            hrefValue = hrefValue
                .Replace(" ", "%20")
                .Replace("'", "%27");

            return hrefValue;
        }
        catch (OperationCanceledException)
        {
            LogDebug($"Download link parsing was cancelled for: {modUrl}");
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex, $"Failed to parse mod download link from: {modUrl}");
            return null;
        }
    }

    private async Task<List<XmaMods>> ParsePageAsync(int pageNumber)
    {
        var url = BuildSearchUrl(pageNumber);
        
        if (_reducedDelayForParallel && _parallelPageFetching && _maxPages > 1)
        {
            await Task.Delay(_requestDelay / 2, CancellationToken);
        }
        else
        {
            await Task.Delay(_requestDelay, CancellationToken);
        }
        
        var html = await _httpClient!.GetStringAsync(url, CancellationToken);

        ThrowIfCancellationRequested();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<XmaMods>();
        var modCards = doc.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card')]");

        if (modCards == null)
        {
            LogDebug($"No mod-card blocks found for page {pageNumber}.");
            return results;
        }

        foreach (var modCard in modCards)
        {
            try
            {
                ThrowIfCancellationRequested();
                
                var mod = ParseModFromCard(modCard);
                if (mod != null)
                {
                    results.Add(mod);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogWarning(ex, $"Failed to parse mod card on page {pageNumber}");
            }
        }

        return results;
    }

    private string BuildSearchUrl(int pageNumber)
    {
        var url = $"{_baseUrl}/search?sortby={_sortBy}&sortorder={_sortOrder}&dt_compat={_dtCompatibility}&page={pageNumber}";
    
        if (_modTypes.Count > 0)
        {
            var typeParams = string.Join(",", _modTypes);
            var encodedTypeParams = Uri.EscapeDataString(typeParams);
            url += $"&types={encodedTypeParams}";
        }
    
        LogDebug($"Built search URL for page {pageNumber}: {url}");
        return url;
    }

    private XmaMods? ParseModFromCard(HtmlNode modCard)
    {
        var linkNode = modCard.SelectSingleNode(".//a[@href]");
        var linkAttr = linkNode?.GetAttributeValue("href", "") ?? "";
        var fullLink = string.IsNullOrWhiteSpace(linkAttr) ? "" : _baseUrl + linkAttr;

        var nameNode = modCard.SelectSingleNode(".//h5[contains(@class,'card-title')]");
        var rawName = nameNode?.InnerText?.Trim() ?? "";
        var normalizedName = NormalizeModName(rawName);

        if (string.IsNullOrEmpty(normalizedName))
            return null;

        var publisherNode = modCard.SelectSingleNode(".//p[contains(@class,'card-text')]/a[@href]");
        var publisherText = publisherNode?.InnerText?.Trim() ?? "";

        var infoNodes = modCard.SelectNodes(".//code[contains(@class, 'text-light')]");
        var typeText = "";
        var genderText = "";

        if (infoNodes != null)
        {
            foreach (var node in infoNodes)
            {
                var text = node.InnerText.Trim();
                if (text.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
                {
                    typeText = text.Replace("Type:", "").Trim();
                }
                else if (text.StartsWith("Genders:", StringComparison.OrdinalIgnoreCase))
                {
                    genderText = text.Replace("Genders:", "").Trim().ToLowerInvariant();
                }
            }
        }

        var genderVal = XmaGender.Unisex;
        if (string.Equals(genderText, "male", StringComparison.OrdinalIgnoreCase))
        {
            genderVal = XmaGender.Male;
        }
        else if (string.Equals(genderText, "female", StringComparison.OrdinalIgnoreCase))
        {
            genderVal = XmaGender.Female;
        }

        var imgNode = modCard.SelectSingleNode(".//img[contains(@class, 'card-img-top')]");
        var imgUrl = imgNode?.GetAttributeValue("src", "") ?? "";

        if (string.IsNullOrWhiteSpace(imgUrl))
        {
            LogWarning($"Skipping mod due to missing image URL, Name={normalizedName}");
            return null;
        }

        return new XmaMods
        {
            Name = normalizedName,
            Publisher = publisherText,
            Type = typeText,
            ImageUrl = imgUrl,
            ModUrl = fullLink,
            DownloadUrl = "",
            Gender = genderVal
        };
    }

    private void InvalidateCacheOnCookieChange()
    {
        if (_cookieValue != _lastCookieValue)
        {
            LogDebug("Cookie changed. Invalidating cached data.");

            if (File.Exists(_xmaCacheFilePath))
            {
                try
                {
                    ThrowIfCancellationRequested();
                    File.Delete(_xmaCacheFilePath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogError(ex, "Failed to delete old XMA cache file while invalidating cache.");
                }
            }

            _lastCookieValue = _cookieValue;
            // Recreate HttpClient with new cookie
            CreateHttpClient();
        }
    }

    private void InvalidateCacheOnFilterSortChange()
    {
        var modTypesChanged = !_modTypes.SequenceEqual(_lastModTypes);
        var sortByChanged = _sortBy != _lastSortBy;
        var sortOrderChanged = _sortOrder != _lastSortOrder;
        var dtCompatChanged = _dtCompatibility != _lastDtCompatibility;

        if (modTypesChanged || sortByChanged || sortOrderChanged || dtCompatChanged)
        {
            LogDebug("Filter/sort configuration changed. Invalidating cached data.");
            
            if (modTypesChanged)
                LogDebug($"ModTypes changed from [{string.Join(", ", _lastModTypes)}] to [{string.Join(", ", _modTypes)}]");
            if (sortByChanged)
                LogDebug($"SortBy changed from '{_lastSortBy}' to '{_sortBy}'");
            if (sortOrderChanged)
                LogDebug($"SortOrder changed from '{_lastSortOrder}' to '{_sortOrder}'");
            if (dtCompatChanged)
                LogDebug($"DtCompatibility changed from {_lastDtCompatibility} to {_dtCompatibility}");

            if (File.Exists(_xmaCacheFilePath))
            {
                try
                {
                    ThrowIfCancellationRequested();
                    File.Delete(_xmaCacheFilePath);
                    LogDebug("XMA cache file deleted due to filter/sort changes");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogError(ex, "Failed to delete old XMA cache file while invalidating due to filter/sort changes.");
                }
            }

            _lastModTypes = new List<int>(_modTypes);
            _lastSortBy = _sortBy;
            _lastSortOrder = _sortOrder;
            _lastDtCompatibility = _dtCompatibility;
        }
    }

    protected override string GetConfigurationHash(Dictionary<string, object> configuration)
    {
        var configString = string.Join("|", configuration.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(configString));
        return Convert.ToBase64String(hash);
    }

    protected override void InvalidateCacheOnConfigChange(string configHash)
    {
        if (_lastConfigHash != null && _lastConfigHash != configHash)
        {
            LogDebug("Configuration changed. Invalidating cached data.");

            if (File.Exists(_xmaCacheFilePath))
            {
                try
                {
                    ThrowIfCancellationRequested();
                    File.Delete(_xmaCacheFilePath);
                    LogDebug("XMA cache file deleted due to configuration changes");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogError(ex, "Failed to delete old XMA cache file while invalidating due to configuration changes.");
                }
            }
        }

        _lastConfigHash = configHash;
    }

    protected override string NormalizeModName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return string.Empty;

        return WebUtility.HtmlDecode(rawName.Trim());
    }

    private XmaCacheData? LoadXmaCacheFromFile()
    {
        try
        {
            ThrowIfCancellationRequested();
            
            if (!File.Exists(_xmaCacheFilePath))
                return null;

            var bytes = File.ReadAllBytes(_xmaCacheFilePath);
            return MessagePackSerializer.Deserialize<XmaCacheData>(bytes);
        }
        catch (OperationCanceledException)
        {
            LogDebug("Cache loading was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex, "Failed to load XMA cache from file.");
            return null;
        }
    }

    private void SaveXmaCacheToFile(XmaCacheData data)
    {
        try
        {
            ThrowIfCancellationRequested();
            
            var bytes = MessagePackSerializer.Serialize(data);
            File.WriteAllBytes(_xmaCacheFilePath, bytes);

            LogDebug($"XMA cache saved to {_xmaCacheFilePath}, valid until {data.ExpirationTime:u}.");
        }
        catch (OperationCanceledException)
        {
            LogDebug("Cache saving was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex, "Failed to save XMA cache to file.");
        }
    }

    protected override async Task OnDisposingAsync()
    {
        LogInfo("Cleaning up XMA plugin resources...");
    
        try
        {
            _httpClient?.Dispose();
            _httpClient = null;
        
            LogDebug("HttpClient disposed successfully");
        
            LogInfo("XMA plugin resources cleaned up successfully");
        }
        catch (Exception ex)
        {
            LogError(ex, "Error during XMA plugin cleanup");
            throw; 
        }
    }
    
    private void LogInfo(string message)
    {
        if (Logger != NullLogger.Instance)
        {
            Logger.LogInformation(message);
        }
        else
        {
            Console.WriteLine($"[XMA Plugin] {message}");
        }
    }

    private void LogDebug(string message)
    {
        if (Logger != NullLogger.Instance)
        {
            Logger.LogDebug(message);
        }
        else
        {
            #if DEBUG
            Console.WriteLine($"[XMA Plugin DEBUG] {message}");
            #endif
        }
    }

    private void LogWarning(string message)
    {
        if (Logger != NullLogger.Instance)
        {
            Logger.LogWarning(message);
        }
        else
        {
            Console.WriteLine($"[XMA Plugin WARN] {message}");
        }
    }

    private void LogWarning(Exception ex, string message)
    {
        if (Logger != NullLogger.Instance)
        {
            Logger.LogWarning(ex, message);
        }
        else
        {
            Console.WriteLine($"[XMA Plugin WARN] {message}: {ex.Message}");
        }
    }

    private void LogError(Exception ex, string message)
    {
        if (Logger != NullLogger.Instance)
        {
            Logger.LogError(ex, message);
        }
        else
        {
            Console.WriteLine($"[XMA Plugin ERROR] {message}: {ex.Message}");
        }
    }
}
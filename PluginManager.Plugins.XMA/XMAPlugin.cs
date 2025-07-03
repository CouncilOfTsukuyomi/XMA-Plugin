
using System.Net;
using HtmlAgilityPack;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PluginManager.Core.Interfaces;
using PluginManager.Core.Models;
using PluginManager.Core.Plugins;
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
    private string _userAgent = "XmaModPlugin/1.0.6";

    // We store the last known cookie to detect changes between calls.
    private string? _lastCookieValue;

    // Cache file path specifically for XMA data
    private string _xmaCacheFilePath = string.Empty;
    private static readonly TimeSpan _xmaCacheDuration = TimeSpan.FromMinutes(30);

    public override string PluginId => "xmamod-plugin";
    public override string DisplayName => "XIV Mod Archive";
    public override string Description => "XIV Mod Archive integration - browse and download FFXIV mods";
    public override string Version => "1.0.6";
    public override string Author => "Council of Tsukuyomi";

    // Simple parameterless constructor for isolated loader
    public XmaPlugin() : base(NullLogger.Instance)
    {
        InitializeHttpClient();
    }

    // Simple constructor with non-generic logger for isolated loader compatibility  
    public XmaPlugin(ILogger logger) : base(logger, TimeSpan.FromMinutes(30))
    {
        InitializeHttpClient();
    }

    // Constructor for dependency injection (testing/manual use)
    public XmaPlugin(ILogger logger, HttpClient httpClient) : base(logger, TimeSpan.FromMinutes(30))
    {
        _httpClient = httpClient;
        ConfigureHttpClient();
    }

    private void InitializeHttpClient()
    {
        _httpClient = new HttpClient();
        ConfigureHttpClient();
    }

    public override async Task InitializeAsync(Dictionary<string, object> configuration)
    {
        ThrowIfCancellationRequested();
        
        LogInfo("Initializing XIV Mod Archive plugin");
        LogInfo($"Current _maxPages before configuration: {_maxPages}");
        LogInfo($"Configuration received: {string.Join(", ", configuration.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");

        // Load configuration
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
            LogInfo($"MaxPages found in configuration: value='{maxPages}', type={maxPages?.GetType().Name}");
            
            if (int.TryParse(maxPages.ToString(), out var pageCount))
            {
                _maxPages = pageCount;
                LogInfo($"MaxPages setting loaded successfully: {_maxPages}");
            }
            else
            {
                LogInfo($"MaxPages setting failed to parse: '{maxPages}' - using default: {_maxPages}");
            }
        }
        else
        {
            LogInfo($"MaxPages setting not found in configuration, using default: {_maxPages}");
            LogInfo($"Available configuration keys: {string.Join(", ", configuration.Keys)}");
        }

        LogInfo($"Final _maxPages after configuration: {_maxPages}");

        // Set up cache paths
        _xmaCacheFilePath = Path.Combine(PluginDirectory, "xma_mods.cache");
        
        // Ensure cache directory exists
        var directory = Path.GetDirectoryName(_xmaCacheFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Initialize cookie tracking
        _lastCookieValue = _cookieValue;

        // Update cache key if configuration changed
        var configHash = GetConfigurationHash(configuration);
        InvalidateCacheOnConfigChange(configHash);

        ConfigureHttpClient();
        
        LogInfo("XIV Mod Archive plugin initialized successfully");
    }

    public override async Task<List<PluginMod>> GetRecentModsAsync()
    {
        try
        {
            ThrowIfCancellationRequested();
            
            LogDebug("Getting recent mods from XIV Mod Archive");

            // Check for cookie changes and invalidate cache if needed
            InvalidateCacheOnCookieChange();

            // Check XMA-specific cache first
            var cachedData = LoadXmaCacheFromFile();
            if (cachedData != null && cachedData.ExpirationTime > DateTimeOffset.Now)
            {
                LogDebug($"Returning {cachedData.Mods.Count} mods from XMA cache");
                
                // Log each cached mod before converting
                foreach (var cachedMod in cachedData.Mods)
                {
                    LogDebug($"Cached Mod: Name='{cachedMod.Name}', Publisher='{cachedMod.Publisher}', ImageUrl='{cachedMod.ImageUrl}', ModUrl='{cachedMod.ModUrl}', DownloadUrl='{cachedMod.DownloadUrl}'");
                }
                
                var cachedPluginMods = cachedData.Mods.Select(m => m.ToPluginMod(PluginId)).ToList();
                
                // Log each converted PluginMod
                foreach (var pluginMod in cachedPluginMods)
                {
                    LogDebug($"PluginMod from cache: ModName='{pluginMod.Name}', Author='{pluginMod.Publisher}', ImageUrl='{pluginMod.ImageUrl}', ModUrl='{pluginMod.ModUrl}', DownloadUrl='{pluginMod.DownloadUrl}', PluginSource='{pluginMod.PluginSource}'");
                }
                
                return cachedPluginMods;
            }

            LogDebug("XMA cache is empty or expired. Fetching new data...");

            // Fetch fresh data
            var xmaMods = await FetchRecentXmaModsAsync();
            
            ThrowIfCancellationRequested();
            
            // Log raw XMA mods before enrichment
            LogDebug($"Fetched {xmaMods.Count} raw XMA mods before enrichment");
            foreach (var xmaMod in xmaMods.Take(3)) // Log first 3 to avoid spam
            {
                LogDebug($"Raw XMA Mod: Name='{xmaMod.Name}', Publisher='{xmaMod.Publisher}', ImageUrl='{xmaMod.ImageUrl}', ModUrl='{xmaMod.ModUrl}', DownloadUrl='{xmaMod.DownloadUrl}'");
            }
            
            // Always fetch download links for each mod
            LogDebug("Enriching mods with download links...");
            xmaMods = await EnrichWithDownloadLinksAsync(xmaMods);

            ThrowIfCancellationRequested();

            // Log enriched mods
            LogDebug($"After enrichment: {xmaMods.Count} mods");
            foreach (var enrichedMod in xmaMods.Take(3)) // Log first 3 to avoid spam
            {
                LogDebug($"Enriched XMA Mod: Name='{enrichedMod.Name}', Publisher='{enrichedMod.Publisher}', ImageUrl='{enrichedMod.ImageUrl}', ModUrl='{enrichedMod.ModUrl}', DownloadUrl='{enrichedMod.DownloadUrl}'");
            }
            
            // Cache the XMA-specific results
            var newCache = new XmaCacheData
            {
                Mods = xmaMods,
                ExpirationTime = DateTimeOffset.Now.Add(_xmaCacheDuration)
            };
            SaveXmaCacheToFile(newCache);

            // Convert to PluginMod format
            var pluginMods = xmaMods.Select(m => m.ToPluginMod(PluginId)).ToList();

            // Log each final PluginMod that will be returned
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

    private void ConfigureHttpClient()
    {
        if (_httpClient == null) return;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", _userAgent);
        
        if (!string.IsNullOrEmpty(_cookieValue))
        {
            _httpClient.DefaultRequestHeaders.Add("Cookie", $"connect.sid={_cookieValue}");
        }

        _httpClient.Timeout = TimeSpan.FromSeconds(30);
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

                LogInfo($"About to fetch mods - Current _maxPages value: {_maxPages}");
                LogInfo($"Fetching mods from {_maxPages} pages");

                var allResults = new List<XmaMods>();

                // Fetch from all configured pages
                for (int page = 1; page <= _maxPages; page++)
                {
                    ThrowIfCancellationRequested();
                    
                    LogDebug($"Fetching page {page} of {_maxPages}");
                    var pageResults = await ParsePageAsync(page);
                    LogDebug($"Page {page} returned {pageResults.Count} mods");
                    allResults.AddRange(pageResults);
                }

                LogInfo($"Total mods fetched before deduplication: {allResults.Count}");

                // Combine and deduplicate mods by ImageUrl
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

    /// <summary>
    /// Enriches mod list with download links by fetching each mod's detail page
    /// </summary>
    private async Task<List<XmaMods>> EnrichWithDownloadLinksAsync(List<XmaMods> mods)
    {
        LogDebug($"Enriching {mods.Count} mods with download links");
        
        var semaphore = new SemaphoreSlim(3, 3); // Limit concurrent requests

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
                    DownloadUrl = downloadUrl ?? "", // Set download URL
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

    /// <summary>
    /// Gets the download link for a specific mod by parsing its detail page
    /// </summary>
    protected override async Task<string?> GetModDownloadLinkAsync(string modUrl)
    {
        try
        {
            ThrowIfCancellationRequested();
            
            if (!modUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                modUrl = _baseUrl + modUrl;
            }

            await Task.Delay(_requestDelay, CancellationToken);
            
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

            // Decode HTML entities
            hrefValue = WebUtility.HtmlDecode(hrefValue);

            // If the link is relative, prepend domain
            if (hrefValue.StartsWith("/"))
            {
                hrefValue = _baseUrl + hrefValue;
            }

            // Unescape percent-encoded sequences
            hrefValue = Uri.UnescapeDataString(hrefValue);

            // Manually encode spaces/apostrophes
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

    /// <summary>
    /// Parses a single search-results page from XIV Mod Archive.
    /// Extracts mod name, publisher, type, image URL, direct link, and gender info.
    /// </summary>
    private async Task<List<XmaMods>> ParsePageAsync(int pageNumber)
    {
        var url = $"{_baseUrl}/search?sortby=time_published&sortorder=desc&dt_compat=1&page={pageNumber}";

        await Task.Delay(_requestDelay, CancellationToken);
        
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

    private XmaMods? ParseModFromCard(HtmlNode modCard)
    {
        // The detail link is often in <a href="...">
        var linkNode = modCard.SelectSingleNode(".//a[@href]");
        var linkAttr = linkNode?.GetAttributeValue("href", "") ?? "";
        var fullLink = string.IsNullOrWhiteSpace(linkAttr) ? "" : _baseUrl + linkAttr;

        // Name in <h5 class="card-title">
        var nameNode = modCard.SelectSingleNode(".//h5[contains(@class,'card-title')]");
        var rawName = nameNode?.InnerText?.Trim() ?? "";
        var normalizedName = NormalizeModName(rawName);

        if (string.IsNullOrEmpty(normalizedName))
            return null;

        // Publisher text in <p class="card-text mx-2"> or similar
        var publisherNode = modCard.SelectSingleNode(".//p[contains(@class,'card-text')]/a[@href]");
        var publisherText = publisherNode?.InnerText?.Trim() ?? "";

        // Type/genders in <code class="text-light">
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

        // Convert to enum
        var genderVal = XmaGender.Unisex;
        if (string.Equals(genderText, "male", StringComparison.OrdinalIgnoreCase))
        {
            genderVal = XmaGender.Male;
        }
        else if (string.Equals(genderText, "female", StringComparison.OrdinalIgnoreCase))
        {
            genderVal = XmaGender.Female;
        }

        // The image is in 'card-img-top' <img>
        var imgNode = modCard.SelectSingleNode(".//img[contains(@class, 'card-img-top')]");
        var imgUrl = imgNode?.GetAttributeValue("src", "") ?? "";

        // Skip mods without an image
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
            DownloadUrl = "", // Will be populated later if FetchDownloadLinks is enabled
            Gender = genderVal
        };
    }

    /// <summary>
    /// Deletes the existing mod cache file if the connect.sid cookie has changed.
    /// Then updates _lastCookieValue to the current cookie.
    /// </summary>
    private void InvalidateCacheOnCookieChange()
    {
        if (_cookieValue != _lastCookieValue)
        {
            LogDebug("Cookie changed. Invalidating cached data.");

            // Remove XMA-specific cache file
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
            ConfigureHttpClient(); // Update HttpClient with new cookie
        }
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

    /// <summary>
    /// Custom cleanup logic for XMA plugin
    /// </summary>
    protected override async Task OnDisposingAsync()
    {
        LogInfo("Cleaning up XMA plugin resources...");
    
        try
        {
            // Dispose HttpClient
            _httpClient?.Dispose();
            _httpClient = null;
        
            // Clean up any pending requests or other resources
            LogDebug("HttpClient disposed successfully");
        
            // You could also clean up cache files if needed
            // DeleteCacheFiles();
        
            LogInfo("XMA plugin resources cleaned up successfully");
        }
        catch (Exception ex)
        {
            LogError(ex, "Error during XMA plugin cleanup");
            throw; // Re-throw to let base class handle it
        }
    }

    // Logging helper methods with fallback
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
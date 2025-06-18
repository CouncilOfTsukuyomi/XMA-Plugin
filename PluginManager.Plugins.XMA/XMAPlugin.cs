using System.Net;
using HtmlAgilityPack;
using MessagePack;
using Microsoft.Extensions.Logging;
using PluginManager.Core.Models;
using PluginManager.Core.Plugins;
using PluginManager.Plugins.XMA.Models;

namespace PluginManager.Plugins.XMA;

public class XmaPlugin : BaseModPlugin
{
    private HttpClient? _httpClient;
    private string _baseUrl = "https://www.xivmodarchive.com";
    private string? _cookieValue;
    private TimeSpan _requestDelay = TimeSpan.FromMilliseconds(1000);
    private int _maxRetries = 3;
    private string _userAgent = "XmaModPlugin/1.0.0";
    private bool _fetchDownloadLinks = true; // Option to fetch download links immediately

    // We store the last known cookie to detect changes between calls.
    private string? _lastCookieValue;

    // Cache file path specifically for XMA data
    private string _xmaCacheFilePath = string.Empty;
    private static readonly TimeSpan _xmaCacheDuration = TimeSpan.FromMinutes(30);

    public override string PluginId => "xmamod-plugin";
    public override string DisplayName => "XIV Mod Archive";
    public override string Description => "Official XIV Mod Archive integration - browse and download FFXIV mods";
    public override string Version => "1.0.0";
    public override string Author => "Council of Tsukuyomi";

    public XmaPlugin(ILogger<XmaPlugin> logger, HttpClient? httpClient = null) 
        : base(logger, TimeSpan.FromMinutes(30))
    {
        _httpClient = httpClient ?? new HttpClient();
        ConfigureHttpClient();
    }

    public override async Task InitializeAsync(Dictionary<string, object> configuration)
    {
        Logger.LogInformation("Initializing XIV Mod Archive plugin");

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

        if (configuration.TryGetValue("FetchDownloadLinks", out var fetchLinks) && 
            bool.TryParse(fetchLinks.ToString(), out var shouldFetch))
            _fetchDownloadLinks = shouldFetch;

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
        
        Logger.LogInformation("XIV Mod Archive plugin initialized successfully");
    }

    public override async Task<List<PluginMod>> GetRecentModsAsync()
    {
        Logger.LogDebug("Getting recent mods from XIV Mod Archive");

        // Check for cookie changes and invalidate cache if needed
        InvalidateCacheOnCookieChange();

        // Check XMA-specific cache first
        var cachedData = LoadXmaCacheFromFile();
        if (cachedData != null && cachedData.ExpirationTime > DateTimeOffset.Now)
        {
            Logger.LogDebug("Returning {Count} mods from XMA cache", cachedData.Mods.Count);
            return cachedData.Mods.Select(m => m.ToPluginMod(PluginId)).ToList();
        }

        Logger.LogDebug("XMA cache is empty or expired. Fetching new data...");

        // Fetch fresh data
        var xmaMods = await FetchRecentXmaModsAsync();
        
        // Optionally fetch download links for each mod
        if (_fetchDownloadLinks)
        {
            xmaMods = await EnrichWithDownloadLinksAsync(xmaMods);
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

        Logger.LogInformation("Retrieved {Count} recent mods from XIV Mod Archive", pluginMods.Count);
        return pluginMods;
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
                if (retryCount > 0)
                    await Task.Delay(_requestDelay * retryCount);

                var page1Results = await ParsePageAsync(1);
                var page2Results = await ParsePageAsync(2);

                // Combine and deduplicate mods by ImageUrl
                var distinctMods = page1Results
                    .Concat(page2Results)
                    .GroupBy(m => m.ImageUrl)
                    .Select(g => g.First())
                    .ToList();

                return distinctMods;
            }
            catch (Exception ex)
            {
                retryCount++;
                Logger.LogWarning(ex, "Attempt {Retry}/{MaxRetries} failed to fetch mods from XIV Mod Archive", 
                    retryCount, _maxRetries);

                if (retryCount > _maxRetries)
                {
                    Logger.LogError(ex, "Failed to fetch mods after {MaxRetries} retries", _maxRetries);
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
        Logger.LogDebug("Enriching {Count} mods with download links", mods.Count);
        
        var enrichedMods = new List<XmaMods>();
        var semaphore = new SemaphoreSlim(3, 3); // Limit concurrent requests

        var tasks = mods.Select(async mod =>
        {
            await semaphore.WaitAsync();
            try
            {
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
            if (!modUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                modUrl = _baseUrl + modUrl;
            }

            await Task.Delay(_requestDelay);
            var html = await _httpClient!.GetStringAsync(modUrl);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var downloadNode = doc.DocumentNode.SelectSingleNode("//a[@id='mod-download-link']");
            if (downloadNode == null)
            {
                Logger.LogWarning("No download anchor found on: {ModUrl}", modUrl);
                return null;
            }

            var hrefValue = downloadNode.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(hrefValue))
            {
                Logger.LogWarning("Download link was empty or missing on: {ModUrl}", modUrl);
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
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse mod download link from: {ModUrl}", modUrl);
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

        await Task.Delay(_requestDelay);
        var html = await _httpClient!.GetStringAsync(url);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<XmaMods>();
        var modCards = doc.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card')]");

        if (modCards == null)
        {
            Logger.LogDebug("No mod-card blocks found for page {PageNumber}.", pageNumber);
            return results;
        }

        foreach (var modCard in modCards)
        {
            try
            {
                var mod = ParseModFromCard(modCard);
                if (mod != null)
                {
                    results.Add(mod);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to parse mod card on page {PageNumber}", pageNumber);
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
            Logger.LogWarning("Skipping mod due to missing image URL, Name={Name}", normalizedName);
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
            Logger.LogDebug("Cookie changed. Invalidating cached data.");

            // Remove XMA-specific cache file
            if (File.Exists(_xmaCacheFilePath))
            {
                try
                {
                    File.Delete(_xmaCacheFilePath);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to delete old XMA cache file while invalidating cache.");
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
            if (!File.Exists(_xmaCacheFilePath))
                return null;

            var bytes = File.ReadAllBytes(_xmaCacheFilePath);
            return MessagePackSerializer.Deserialize<XmaCacheData>(bytes);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load XMA cache from file.");
            return null;
        }
    }

    private void SaveXmaCacheToFile(XmaCacheData data)
    {
        try
        {
            var bytes = MessagePackSerializer.Serialize(data);
            File.WriteAllBytes(_xmaCacheFilePath, bytes);

            Logger.LogDebug("XMA cache saved to {FilePath}, valid until {ExpirationTime}.",
                _xmaCacheFilePath, data.ExpirationTime.ToString("u"));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save XMA cache to file.");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        await base.DisposeAsync();
    }
}
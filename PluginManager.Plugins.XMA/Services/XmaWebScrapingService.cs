using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using PluginManager.Plugins.XMA.Models;
using System.Net;
using System.Globalization;

namespace PluginManager.Plugins.XMA.Services;

public class XmaWebScrapingService
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly XmaConfigurationService _configService;
    private readonly CancellationToken _cancellationToken;

    public XmaWebScrapingService(ILogger logger, HttpClient httpClient, XmaConfigurationService configService, CancellationToken cancellationToken)
    {
        _logger = logger;
        _httpClient = httpClient;
        _configService = configService;
        _cancellationToken = cancellationToken;
    }

    public async Task<List<XmaMods>> FetchModsAsync()
    {
        var retryCount = 0;

        while (retryCount <= _configService.MaxRetries)
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (retryCount > 0)
                    await Task.Delay(_configService.RequestDelay * retryCount, _cancellationToken);

                List<XmaMods> allResults;

                if (_configService.ParallelPageFetching && _configService.MaxPages > 1)
                {
                    _logger.LogInformation($"Fetching mods from {_configService.MaxPages} pages in parallel");
                    allResults = await FetchPagesInParallel();
                }
                else
                {
                    _logger.LogInformation($"Fetching mods from {_configService.MaxPages} pages sequentially");
                    allResults = await FetchPagesSequentially();
                }

                _logger.LogInformation($"Total mods fetched before deduplication: {allResults.Count}");

                var distinctMods = allResults
                    .GroupBy(m => m.ImageUrl)
                    .Select(g => g.First())
                    .ToList();

                return distinctMods;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Fetch operation was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogWarning(ex, $"Attempt {retryCount}/{_configService.MaxRetries} failed to fetch mods from XIV Mod Archive");

                if (retryCount > _configService.MaxRetries)
                {
                    _logger.LogError(ex, $"Failed to fetch mods after {_configService.MaxRetries} retries");
                    throw;
                }
            }
        }
        return new List<XmaMods>();
    }

    private async Task<List<XmaMods>> FetchPagesInParallel()
    {
        var pageTasks = Enumerable.Range(1, _configService.MaxPages)
            .Select(async page =>
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _logger.LogDebug($"Fetching page {page} of {_configService.MaxPages}");
                var pageResults = await ParsePageAsync(page);
                _logger.LogDebug($"Page {page} returned {pageResults.Count} mods");
                return pageResults;
            });

        var pageResultsArray = await Task.WhenAll(pageTasks);
        return pageResultsArray.SelectMany(results => results).ToList();
    }

    private async Task<List<XmaMods>> FetchPagesSequentially()
    {
        var allResults = new List<XmaMods>();

        for (int page = 1; page <= _configService.MaxPages; page++)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            _logger.LogDebug($"Fetching page {page} of {_configService.MaxPages}");
            var pageResults = await ParsePageAsync(page);
            _logger.LogDebug($"Page {page} returned {pageResults.Count} mods");
            allResults.AddRange(pageResults);
        }

        return allResults;
    }

    private async Task<List<XmaMods>> ParsePageAsync(int pageNumber)
    {
        var url = BuildSearchUrl(pageNumber);

        if (_configService.ReducedDelayForParallel && _configService.ParallelPageFetching && _configService.MaxPages > 1)
        {
            await Task.Delay(_configService.RequestDelay / 2, _cancellationToken);
        }
        else
        {
            await Task.Delay(_configService.RequestDelay, _cancellationToken);
        }

        var html = await _httpClient.GetStringAsync(url, _cancellationToken);

        _cancellationToken.ThrowIfCancellationRequested();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<XmaMods>();
        var modCards = doc.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card')]");

        if (modCards == null)
        {
            _logger.LogDebug($"No mod-card blocks found for page {pageNumber}.");
            return results;
        }

        foreach (var modCard in modCards)
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();

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
                _logger.LogWarning(ex, $"Failed to parse mod card on page {pageNumber}");
            }
        }

        return results;
    }

    private string BuildSearchUrl(int pageNumber)
    {
        var searchParams = _configService.SearchParameters;
        var url = $"{_configService.BaseUrl}/search?sortby={searchParams.SortBy}&sortorder={searchParams.SortOrder}&dt_compat={searchParams.DtCompatibility}&page={pageNumber}";

        if (searchParams.ModTypes.Count > 0)
        {
            var typeParams = string.Join(",", searchParams.ModTypes);
            var encodedTypeParams = Uri.EscapeDataString(typeParams);
            url += $"&types={encodedTypeParams}";
        }

        _logger.LogDebug($"Built search URL for page {pageNumber}: {url}");
        return url;
    }

    private XmaMods? ParseModFromCard(HtmlNode modCard)
    {
        var linkNode = modCard.SelectSingleNode(".//a[@href]");
        var linkAttr = linkNode?.GetAttributeValue("href", "") ?? "";
        var fullLink = string.IsNullOrWhiteSpace(linkAttr) ? "" : _configService.BaseUrl + linkAttr;

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
            _logger.LogWarning($"Skipping mod due to missing image URL, Name={normalizedName}");
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

    private static string NormalizeModName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return string.Empty;

        return WebUtility.HtmlDecode(rawName.Trim());
    }

    public async Task<string?> GetModDownloadLinkAsync(string modUrl)
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (!modUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                modUrl = _configService.BaseUrl + modUrl;
            }

            if (_configService.ReducedDelayForParallel && _configService.ConcurrentDownloadRequests > 1)
            {
                await Task.Delay(_configService.RequestDelay / 2, _cancellationToken);
            }
            else
            {
                await Task.Delay(_configService.RequestDelay, _cancellationToken);
            }

            var html = await _httpClient.GetStringAsync(modUrl, _cancellationToken);

            _cancellationToken.ThrowIfCancellationRequested();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var downloadNode = doc.DocumentNode.SelectSingleNode("//a[@id='mod-download-link']");
            if (downloadNode == null)
            {
                _logger.LogWarning($"No download anchor found on: {modUrl}");
                return null;
            }

            var hrefValue = downloadNode.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(hrefValue))
            {
                _logger.LogWarning($"Download link was empty or missing on: {modUrl}");
                return null;
            }

            hrefValue = WebUtility.HtmlDecode(hrefValue);

            if (hrefValue.StartsWith("/"))
            {
                hrefValue = _configService.BaseUrl + hrefValue;
            }

            hrefValue = Uri.UnescapeDataString(hrefValue);

            hrefValue = hrefValue
                .Replace(" ", "%20")
                .Replace("'", "%27");

            return hrefValue;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug($"Download link parsing was cancelled for: {modUrl}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to parse mod download link from: {modUrl}");
            return null;
        }
    }
    
    public async Task<(string? downloadLink, List<string> tags, DateTime? lastVersionUpdate, string version)> GetModDetailsAsync(string modUrl)
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (!modUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                modUrl = _configService.BaseUrl + modUrl;
            }

            if (_configService is {ReducedDelayForParallel: true, ConcurrentDownloadRequests: > 1})
            {
                await Task.Delay(_configService.RequestDelay / 2, _cancellationToken);
            }
            else
            {
                await Task.Delay(_configService.RequestDelay, _cancellationToken);
            }

            var html = await _httpClient.GetStringAsync(modUrl, _cancellationToken);

            _cancellationToken.ThrowIfCancellationRequested();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            
            string? downloadLink = null;
            var downloadNode = doc.DocumentNode.SelectSingleNode("//a[@id='mod-download-link']");
            if (downloadNode != null)
            {
                var hrefValue = downloadNode.GetAttributeValue("href", "");
                if (!string.IsNullOrWhiteSpace(hrefValue))
                {
                    hrefValue = WebUtility.HtmlDecode(hrefValue);

                    if (hrefValue.StartsWith("/"))
                    {
                        hrefValue = _configService.BaseUrl + hrefValue;
                    }

                    hrefValue = Uri.UnescapeDataString(hrefValue);
                    hrefValue = hrefValue
                        .Replace(" ", "%20")
                        .Replace("'", "%27");

                    downloadLink = hrefValue;
                }
            }
            
            var version = "";
            var versionNode = doc.DocumentNode.SelectSingleNode("//code[@class='text-light' and contains(text(), 'Version:')]");
            if (versionNode != null)
            {
                var versionText = versionNode.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(versionText))
                {
                    var versionMatch = versionText.Replace("Version:", "").Trim();
                    if (!string.IsNullOrWhiteSpace(versionMatch))
                    {
                        version = versionMatch;
                    }
                }
            }
            
            var tags = new List<string>();
            _logger.LogDebug($"Attempting to parse tags for mod: {modUrl}");
            
            var tagsDivs = doc.DocumentNode.SelectNodes("//div[contains(@class, 'mod-meta-block') and contains(normalize-space(text()), 'Tags :')]");
            _logger.LogDebug($"Found {tagsDivs?.Count ?? 0} potential tags divs");
            
            if (tagsDivs != null)
            {
                foreach (var tagsDiv in tagsDivs)
                {
                    _logger.LogDebug($"Tags div HTML: {tagsDiv.OuterHtml}");
                    
                    var tagLinksInDiv = tagsDiv.SelectNodes(".//a[starts-with(@href, '/search?tags=')]");
                    if (tagLinksInDiv == null) continue;
                    _logger.LogDebug($"Found {tagLinksInDiv.Count} tag links in this div");
                        
                    foreach (var tagLink in tagLinksInDiv)
                    {
                        var tagText = tagLink.InnerText?.Trim();
                        var href = tagLink.GetAttributeValue("href", "");
                            
                        _logger.LogDebug($"Tag link - Text: '{tagText}', Href: '{href}'");
                            
                        if (!string.IsNullOrWhiteSpace(tagText))
                        {
                            tags.Add(tagText);
                        }
                    }
                    break;
                }
            }
            
            if (tags.Count == 0)
            {
                _logger.LogDebug("No tags found with primary approach, trying broader search");
                
                var allTagLinks = doc.DocumentNode.SelectNodes("//a[contains(@href, '/search?tags=')]");
                if (allTagLinks != null)
                {
                    _logger.LogDebug($"Found {allTagLinks.Count} potential tag links across the entire page");
                    
                    foreach (var tagLink in allTagLinks)
                    {
                        var isInMetadataArea = tagLink.Ancestors("div")
                            .Any(div => div.GetAttributeValue("class", "").Contains("mod-meta-block"));
                        
                        if (isInMetadataArea)
                        {
                            var tagText = tagLink.InnerText?.Trim();
                            var href = tagLink.GetAttributeValue("href", "");
                            
                            _logger.LogDebug($"Metadata area tag link - Text: '{tagText}', Href: '{href}'");
                            
                            if (!string.IsNullOrWhiteSpace(tagText))
                            {
                                tags.Add(tagText);
                            }
                        }
                    }
                }
            }
            
            _logger.LogDebug($"Final parsed tags count: {tags.Count}, Tags: [{string.Join(", ", tags)}]");
            
            // Parse last version update and convert to local time
            DateTime? lastVersionUpdate = null;
            var lastUpdateSection = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'mod-meta-block') and contains(text(), 'Last Version Update :')]//code[@class='text-light server-date']");
            
            if (lastUpdateSection != null)
            {
                var dateText = lastUpdateSection.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(dateText))
                {
                    _logger.LogDebug($"Parsing date text: '{dateText}'");
                    // Parse the date format: "Thu Aug 14 2025 12:02:05 GMT+0000 (Coordinated Universal Time)"
                    if (DateTime.TryParse(dateText, null, DateTimeStyles.RoundtripKind, out var parsedDate))
                    {
                        if (parsedDate.Kind == DateTimeKind.Utc)
                        {
                            lastVersionUpdate = parsedDate.ToLocalTime();
                        }
                        else
                        {
                            lastVersionUpdate = DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc).ToLocalTime();
                        }
                        _logger.LogDebug($"Parsed and converted date to local time: {lastVersionUpdate}");
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to parse last version update date: {dateText} for mod: {modUrl}");
                    }
                }
            }
            else
            {
                _logger.LogDebug("No last version update section found");
            }

            _logger.LogDebug($"GetModDetailsAsync completed - Download: {downloadLink != null}, Tags: {tags.Count}, Date: {lastVersionUpdate != null}, Version: '{version}'");

            return (downloadLink, tags, lastVersionUpdate, version);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug($"Mod details parsing was cancelled for: {modUrl}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to parse mod details from: {modUrl}");
            return (null, new List<string>(), null, "");
        }
    }
}
using MessagePack;
using Microsoft.Extensions.Logging;
using PluginManager.Plugins.XMA.Models;

namespace PluginManager.Plugins.XMA.Services;

public class XmaCacheService
{
    private readonly ILogger _logger;
    private readonly string _cacheFilePath;
    private readonly TimeSpan _cacheDuration;
    private readonly CancellationToken _cancellationToken;

    public XmaCacheService(ILogger logger, string cacheFilePath, TimeSpan cacheDuration, CancellationToken cancellationToken)
    {
        _logger = logger;
        _cacheFilePath = cacheFilePath;
        _cacheDuration = cacheDuration;
        _cancellationToken = cancellationToken;
    }

    public XmaCacheData? LoadFromFile()
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            
            if (!File.Exists(_cacheFilePath))
                return null;

            var bytes = File.ReadAllBytes(_cacheFilePath);
            return MessagePackSerializer.Deserialize<XmaCacheData>(bytes);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Cache loading was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load XMA cache from file.");
            return null;
        }
    }

    public void SaveToFile(XmaCacheData data)
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            
            var bytes = MessagePackSerializer.Serialize(data);
            File.WriteAllBytes(_cacheFilePath, bytes);

            _logger.LogDebug($"XMA cache saved to {_cacheFilePath}, valid until {data.ExpirationTime:u}.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Cache saving was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save XMA cache to file.");
        }
    }

    public void InvalidateCache()
    {
        if (File.Exists(_cacheFilePath))
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                File.Delete(_cacheFilePath);
                _logger.LogDebug("XMA cache file deleted");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete XMA cache file.");
            }
        }
    }

    public XmaCacheData CreateCacheData(List<XmaMods> mods)
    {
        return new XmaCacheData
        {
            Mods = mods,
            ExpirationTime = DateTimeOffset.Now.Add(_cacheDuration)
        };
    }
}
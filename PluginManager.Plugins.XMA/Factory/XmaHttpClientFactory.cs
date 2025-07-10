namespace PluginManager.Plugins.XMA.Factory;

public class XmaHttpClientFactory
{
    private readonly string _userAgent;
    private readonly string? _cookieValue;
    private readonly TimeSpan _timeout;

    public XmaHttpClientFactory(string userAgent = "XmaModPlugin", string? cookieValue = null, TimeSpan? timeout = null)
    {
        _userAgent = userAgent;
        _cookieValue = cookieValue;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public HttpClient CreateClient()
    {
        var client = new HttpClient();
        
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", _userAgent);
        
        if (!string.IsNullOrEmpty(_cookieValue))
        {
            client.DefaultRequestHeaders.Add("Cookie", $"connect.sid={_cookieValue}");
        }

        client.Timeout = _timeout;
        
        return client;
    }
}
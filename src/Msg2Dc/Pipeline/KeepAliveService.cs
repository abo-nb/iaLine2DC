namespace Msg2Dc.Pipeline;

public sealed class KeepAliveService : BackgroundService
{
    private readonly ILogger<KeepAliveService> _log;

    public KeepAliveService(ILogger<KeepAliveService> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var baseUrl = Environment.GetEnvironmentVariable("APP_URL")
                      ?? Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");

        var minutes = int.TryParse(Environment.GetEnvironmentVariable("KEEP_ALIVE_MINUTES"), out var parsed)
            ? parsed
            : 10;

        if (string.IsNullOrWhiteSpace(baseUrl) || minutes <= 0)
        {
            _log.LogInformation("Keep-alive disabled (APP_URL/RENDER_EXTERNAL_URL not set or KEEP_ALIVE_MINUTES<=0)");
            return;
        }

        var target = $"{baseUrl.TrimEnd('/')}/healthz";
        _log.LogInformation("Keep-alive will ping {Target} every {Minutes} minute(s)", target, minutes);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var response = await http.GetAsync(target, stoppingToken);
                if (response.IsSuccessStatusCode)
                {
                    _log.LogDebug("Keep-alive ok ({Status})", (int)response.StatusCode);
                }
                else
                {
                    _log.LogWarning("Keep-alive returned {Status}", (int)response.StatusCode);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Keep-alive ping failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

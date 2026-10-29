namespace Msg2Dc.Storage;

public sealed class MongoInitializer : BackgroundService
{
    private readonly MongoStore _store;
    private readonly ILogger<MongoInitializer> _log;

    public MongoInitializer(MongoStore store, ILogger<MongoInitializer> log)
    {
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _store.EnsureIndexesAsync(stoppingToken);
                _log.LogInformation("MongoDB indexes are ready");
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                _log.LogWarning("MongoDB not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 3)), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

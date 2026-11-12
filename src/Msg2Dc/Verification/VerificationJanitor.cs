namespace Msg2Dc.Verification;

/// <summary>
/// Verification codes expire after a few minutes; if the member never confirms, the private
/// thread they were shown the code in has to be removed by us. This sweeps periodically.
/// </summary>
public sealed class VerificationJanitor : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly VerificationService _verify;
    private readonly ILogger<VerificationJanitor> _log;

    public VerificationJanitor(VerificationService verify, ILogger<VerificationJanitor> log)
    {
        _verify = verify;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await _verify.CleanupAsync(stoppingToken);
                if (deleted > 0)
                {
                    _log.LogInformation("Removed {Count} expired verification thread(s)", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Verification cleanup failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

using Microsoft.Extensions.Caching.Memory;

namespace Security_Camera_Backend.Services.Cv;

public sealed class CvServiceHealthService(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    ILogger<CvServiceHealthService> logger) : ICvServiceHealthService
{
    private const string CacheKey = "cv-service-health";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> GetStatusAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out string? cached))
        {
            return cached ?? "down";
        }

        await _gate.WaitAsync(ct);

        try
        {
            if (cache.TryGetValue(CacheKey, out cached))
            {
                return cached ?? "down";
            }

            var status = await CheckAsync(ct);
            cache.Set(CacheKey, status, CacheDuration);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> CheckAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<ICvServiceClient>();

            return await client.IsHealthyAsync(ct) ? "ok" : "down";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CV service health check failed.");
            return "down";
        }
    }
}

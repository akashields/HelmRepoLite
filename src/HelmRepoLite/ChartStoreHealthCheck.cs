using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HelmRepoLite;

internal sealed class ChartStoreHealthCheck(ChartStore store) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            store.IsReady
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("storage scan in progress"));
    }
}

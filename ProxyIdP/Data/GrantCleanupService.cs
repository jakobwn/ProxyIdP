using Microsoft.EntityFrameworkCore;

namespace ProxyIdP.Data;

public sealed class GrantCleanupService(IServiceScopeFactory scopes, TimeProvider time, ILogger<GrantCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ProxyIdPDbContext>();
                var now = time.GetUtcNow().UtcDateTime;
                var grants = await db.Grants.Where(g => g.ExpiresAt < now.AddHours(-1)).ToListAsync(stoppingToken);
                var pending = await db.PendingAuthorizations.Where(p => p.ExpiresAt < now).ToListAsync(stoppingToken);
                db.Grants.RemoveRange(grants);
                db.PendingAuthorizations.RemoveRange(pending);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Grant cleanup failed");
            }
        }
    }
}

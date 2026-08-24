using FileTracert.Contracts.Search;
using FileTracert.Data.Cancellation;
using FileTracert.Data.Indexing;
using FileTracert.Data.Interceptors;
using FileTracert.Data.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FileTracert.Data;

/// <summary>DI wiring for the Data layer: DbContext (SQLite + auditing), bulk writer, and FTS index.</summary>
public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddDataServices(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<AuditingSaveChangesInterceptor>();
        services.AddSingleton<SqliteBusyTimeoutInterceptor>();
        services.AddSingleton<SqliteReadCancellationInterceptor>();

        // TryAdd: a composition with no host (tests, the hardware harness, the design-time
        // factory) gets a signal that never fires; the Host replaces it with the real
        // ApplicationStopping token. See DatabaseShutdownSignal.
        services.TryAddSingleton(DatabaseShutdownSignal.None);

        services.AddDbContext<FileTracertDbContext>((sp, options) =>
            options
                .UseSqlite(connectionString)
                .AddInterceptors(
                    sp.GetRequiredService<AuditingSaveChangesInterceptor>(),
                    sp.GetRequiredService<SqliteBusyTimeoutInterceptor>(),
                    sp.GetRequiredService<SqliteReadCancellationInterceptor>()));

        services.AddScoped<IBulkIndexWriter, BulkIndexWriter>();
        services.AddScoped<IFileSearchIndex, FileSearchIndex>();

        return services;
    }
}

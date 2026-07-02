using FileTracert.Contracts.Search;
using FileTracert.Data.Indexing;
using FileTracert.Data.Interceptors;
using FileTracert.Data.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Data;

/// <summary>DI wiring for the Data layer: DbContext (SQLite + auditing), bulk writer, and FTS index.</summary>
public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddDataServices(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<AuditingSaveChangesInterceptor>();
        services.AddSingleton<SqliteBusyTimeoutInterceptor>();

        services.AddDbContext<FileTracertDbContext>((sp, options) =>
            options
                .UseSqlite(connectionString)
                .AddInterceptors(
                    sp.GetRequiredService<AuditingSaveChangesInterceptor>(),
                    sp.GetRequiredService<SqliteBusyTimeoutInterceptor>()));

        services.AddScoped<IBulkIndexWriter, BulkIndexWriter>();
        services.AddScoped<IFileSearchIndex, FileSearchIndex>();

        return services;
    }
}

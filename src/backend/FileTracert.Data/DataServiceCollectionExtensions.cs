using FileTracert.Data.Indexing;
using FileTracert.Data.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Data;

/// <summary>DI wiring for the Data layer: DbContext (SQLite + auditing) and the bulk writer.</summary>
public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddDataServices(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<AuditingSaveChangesInterceptor>();

        services.AddDbContext<FileTracertDbContext>((sp, options) =>
            options
                .UseSqlite(connectionString)
                .AddInterceptors(sp.GetRequiredService<AuditingSaveChangesInterceptor>()));

        services.AddScoped<IBulkIndexWriter, BulkIndexWriter>();

        return services;
    }
}

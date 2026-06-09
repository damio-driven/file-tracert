using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FileTracert.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (migrations). The runtime host
/// wires up the real connection string and the auditing interceptor via DI; this
/// factory only needs to produce a usable context for migration scaffolding.
/// </summary>
public class FileTracertDbContextFactory : IDesignTimeDbContextFactory<FileTracertDbContext>
{
    public FileTracertDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FileTracertDbContext>();
        optionsBuilder.UseSqlite("Data Source=./filetracert-dev.db");
        return new FileTracertDbContext(optionsBuilder.Options);
    }
}

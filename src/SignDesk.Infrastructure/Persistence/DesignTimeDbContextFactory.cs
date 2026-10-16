using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SignDesk.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SignDeskDbContext>
{
    public SignDeskDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SignDeskDbContext>();
        DependencyInjection.ConfigureDb(options, "Host=localhost;Port=54331;Username=postgres;Database=signdesk");
        return new SignDeskDbContext(options.Options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace XafRag.Module.BusinessObjects;

// Exists only so `dotnet ef migrations add` can construct RagDbContext without booting the whole
// XAF host (which needs an OpenAI key and a running database). Not used at runtime - the app
// resolves RagDbContext from DI, wired in Startup.ConfigureServices.
public class RagDbContextDesignTimeFactory : IDesignTimeDbContextFactory<RagDbContext>
{
    public RagDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RagDbContext>()
            .UseSqlServer("Server=localhost,14333;Database=XafRagSQL;User Id=sa;Password=Dev_Spike_Pw_2025!;TrustServerCertificate=True")
            .Options;
        return new RagDbContext(options);
    }
}

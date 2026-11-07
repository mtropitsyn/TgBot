using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TgBroadcast.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<BotDbContext>()
        .UseSqlite("Data Source=tgbroadcast-design.db").Options);
}

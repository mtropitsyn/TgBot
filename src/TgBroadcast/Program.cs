using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using TgBroadcast.Configuration;
using TgBroadcast.Data;
using TgBroadcast.Services;
using TgBroadcast.Telegram;
using TgBroadcast.Workers;

var initializeOnly = args is ["--init-db"];
var importPath = args is ["--import-subscribers", var file] ? file : null;
if (args.Length != 0 && !initializeOnly && importPath is null)
{
    Console.Error.WriteLine("Usage: TgBroadcast [--init-db | --import-subscribers path/to/subscribers.json]");
    return 2;
}
var builder = Host.CreateApplicationBuilder();
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Configuration.AddUserSecrets<BotOptions>(optional: true).AddEnvironmentVariables();
var options = builder.Configuration.GetSection("Bot").Get<BotOptions>() ?? new BotOptions();
try { options.Validate(!initializeOnly && importPath is null); }
catch (InvalidOperationException error) { Console.Error.WriteLine(error.Message); return 2; }
var databasePath = Path.GetFullPath(options.DatabasePath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var connection = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 30 }.ToString();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContextFactory<BotDbContext>(db => db.UseSqlite(connection));
builder.Services.AddSingleton<ITelegramBotClient>(_ =>
    new TelegramBotClient(new TelegramBotClientOptions(options.Token) { RetryCount = 0 }));
builder.Services.AddSingleton<IBotTransport, TelegramTransport>();
builder.Services.AddSingleton<BotStore>();
builder.Services.AddSingleton<BroadcastProcessor>();
builder.Services.AddSingleton<DraftNotifier>();
builder.Services.AddSingleton<UpdateHandler>();
builder.Services.AddHostedService<PollingWorker>();
builder.Services.AddHostedService<DraftWorker>();
builder.Services.AddHostedService<DeliveryWorker>();
using var host = builder.Build();
await using (var db = await host.Services.GetRequiredService<IDbContextFactory<BotDbContext>>().CreateDbContextAsync())
    await db.Database.MigrateAsync();
if (importPath is not null)
{
    try
    {
        var ids = JsonSerializer.Deserialize<long[]>(await File.ReadAllTextAsync(importPath))
            ?? throw new InvalidDataException("Expected a JSON array of private-chat IDs.");
        var imported = await host.Services.GetRequiredService<BotStore>().ImportSubscribersAsync(ids, CancellationToken.None);
        Console.WriteLine($"Imported {imported} subscribers. Telegram was not contacted.");
    }
    catch (Exception error) when (error is IOException or JsonException or ArgumentException)
    { Console.Error.WriteLine("Import failed: expected an existing JSON file with positive numeric chat IDs."); return 2; }
    return 0;
}
if (initializeOnly) { Console.WriteLine("SQLite migrations applied. Telegram was not contacted."); return 0; }
await host.RunAsync();
return 0;

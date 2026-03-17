using Microsoft.EntityFrameworkCore;
using TimeTrackerBot;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var dbPath = Path.Combine(baseDir, "timetracking.db");
        var logPath = Path.Combine(baseDir, "logs", "bot.log");
        var connectionString = $"Data Source={dbPath}";

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var builder = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddFile(logPath);
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .ConfigureAppConfiguration((ctx, config) =>
            {
                config.AddInMemoryCollection(
                    new Dictionary<string, string?> {
                { "ConnectionStrings:DefaultConnection", connectionString }
                    });
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddDbContext<AppDb>(opts => opts.UseSqlite(connectionString));
                services.Configure<BotConfig>(ctx.Configuration.GetSection("BotConfig"));
                services.Configure<EmailConfig>(ctx.Configuration.GetSection("Email"));
                services.AddSingleton<IEmailService, MailKitEmailService>();
                services.AddScoped<TrackingLogic>();
                services.AddHostedService<TimeTrackingWorker>();
                services.AddSingleton<TrayApplicationContext>();
            });

        var host = builder.Build();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Database.EnsureCreated();
            
            await RecoverActiveSessionAsync(db);
        }

        await host.StartAsync();

        try
        {
            var appContext = host.Services.GetRequiredService<TrayApplicationContext>();
            Application.Run(appContext);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    static async Task RecoverActiveSessionAsync(AppDb db)
    {
        var state = await db.States.FindAsync(1);
        if (state?.ActiveSessionId.HasValue == true)
        {
            var session = await db.Sessions.FindAsync(state.ActiveSessionId.Value);
            if (session != null)
            {
                var delta = (long)(DateTime.Now - session.End).TotalSeconds;
                if (delta > 0)
                {
                    session.DurationSeconds += delta;
                    session.End = DateTime.Now;
                    await db.SaveChangesAsync();
                }
            }

            state.ActiveSessionId = null;
            await db.SaveChangesAsync();
        }
    }
}
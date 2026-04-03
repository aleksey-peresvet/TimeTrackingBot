using Microsoft.EntityFrameworkCore;

namespace TimeTrackerBot;

public class TaskSession
{
    public int Id { get; set; }
    public string Project { get; set; } = "";
    public string Stage { get; set; } = "";
    public string TaskName { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public long DurationSeconds { get; set; }
    public DateTime Date { get; set; }
}

public class DailyReportRow
{
    public string Project { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Task { get; set; } = "";
    public DateTime FirstStart { get; set; }
    public long Seconds { get; set; }
}

public class UserState
{
    public int Id { get; set; }
    public int? ActiveSessionId { get; set; }
    public DateTime LastPromptTime { get; set; }
    public string? LastPromptText { get; set; }
    public string? LastResponseText { get; set; }
    public DateTime LastResponseTime { get; set; }
    public bool IsPaused { get; set; }
}

public class AppDb : DbContext
{
    public DbSet<TaskSession> Sessions { get; set; } = null!;
    public DbSet<UserState> States { get; set; } = null!;
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskSession>(entity =>
        {
            entity.HasIndex(s => s.Date);
            entity.HasIndex(s => s.Start);
        });
        
        modelBuilder.Entity<UserState>(entity =>
        {
            entity.HasIndex(s => s.Id).IsUnique();
        });
    }
}
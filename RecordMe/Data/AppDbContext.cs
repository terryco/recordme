using Microsoft.EntityFrameworkCore;
using RecordMe.Models;
using System;
using System.IO;

namespace RecordMe.Data;

public class AppDbContext : DbContext
{
    public DbSet<Recording> Recordings => Set<Recording>();

    public string DbPath { get; } = string.Empty;

    public AppDbContext()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RecordMe");
        Directory.CreateDirectory(appData);
        DbPath = Path.Combine(appData, "recordme.db");
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (!options.IsConfigured)
        {
            options.UseSqlite($"Data Source={DbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Recording>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.FollowUp);
        });
    }
}

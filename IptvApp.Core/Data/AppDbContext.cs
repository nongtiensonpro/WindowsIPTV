using Microsoft.EntityFrameworkCore;
using IptvApp.Core.Models;
using System.IO;
using System;

namespace IptvApp.Core.Data;

public class AppDbContext : DbContext
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<EpgProgram> EpgPrograms => Set<EpgProgram>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var appFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IptvApp");
        if (!Directory.Exists(appFolder))
        {
            Directory.CreateDirectory(appFolder);
        }
        var dbPath = Path.Combine(appFolder, "iptv.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
    }
}

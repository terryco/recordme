using Microsoft.EntityFrameworkCore;
using RecordMe.Data;
using RecordMe.ViewModels;
using System.Windows;

namespace RecordMe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
    }

    public static AppDbContext CreateDbContext()
    {
        return new AppDbContext();
    }
}

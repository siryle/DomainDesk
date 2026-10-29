using System.Windows;

namespace DomainDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (MainWindow is null) new MainWindow().Show();
    }
}

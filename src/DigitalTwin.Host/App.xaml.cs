using System.Windows;
using DigitalTwin.Host.Configuration;
using DigitalTwin.Host.Windows;

namespace DigitalTwin.Host;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var options = ClientOptions.Load(AppContext.BaseDirectory);
            var launch = LaunchOptions.Parse(e.Args, options);
            var window = new MainWindow(options, launch);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"应用启动失败。\n\n{exception.Message}",
                "Digital Twin Web Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

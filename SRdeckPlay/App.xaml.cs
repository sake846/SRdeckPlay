using System.Windows;

namespace SRdeckPlay;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length == 1) window.OpenRecording(e.Args[0]);
    }
}

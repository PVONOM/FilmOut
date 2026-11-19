using System.Windows;

namespace FilmOut.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // During a shoot an unexpected error should be reported, not end the session.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "FilmOut", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}

using System.Windows;
using Beam.Desktop.Services;

namespace Beam.Desktop;

/// <summary>Ponto de entrada da aplicação WPF.</summary>
public partial class App : System.Windows.Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		try
		{
			WindowsStartupService.EnsureRegistered();
		}
		catch (Exception ex)
		{
			StartupLog.Write($"Startup registration failed: {ex}");
		}

		MainWindow = new MainWindow();
		MainWindow.Show();
		StartupLog.Write("MainWindow created and shown.");
	}
}

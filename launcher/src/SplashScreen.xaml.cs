using System;
using System.Windows;
using System.IO;
using System.Windows.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using LauncherConfig;

namespace CanaryLauncherUpdate
{
	public partial class SplashScreen : Window
	{
		DispatcherTimer timer = new DispatcherTimer();

		private static string GetLauncherPath(bool onlyBaseDirectory = false)
		{
			string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
			if (string.IsNullOrEmpty(ClientConfig.Current.clientFolder) || onlyBaseDirectory) {
				return baseDirectory;
			}

			return Path.Combine(baseDirectory, ClientConfig.Current.clientFolder);
		}

		public SplashScreen()
		{
			InitializeComponent();
			Loaded += SplashScreen_Loaded;
		}

		private async void SplashScreen_Loaded(object sender, RoutedEventArgs e)
		{
			string error = null;
			bool loaded = await Task.Run(() => ClientConfig.TryLoadRemote(out error));
			if (!loaded)
			{
				MessageBox.Show("Could not load launcher configuration.\nCheck your internet connection and try again.\n\n" + error,
					"XXXOTS Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
				Application.Current.Shutdown();
				return;
			}

			// Start the client right away if it is installed and up to date
			string executable = Path.Combine(GetLauncherPath(), "bin", ClientConfig.Current.clientExecutable);
			string actualVersion = ClientConfig.GetLocalClientVersion(GetLauncherPath(true));
			if (actualVersion == ClientConfig.Current.clientVersion && File.Exists(executable))
			{
				ClientConfig.ClearStaleServerCache(GetLauncherPath());
				Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable) });
				Application.Current.Shutdown();
				return;
			}

			timer.Tick += timer_SplashScreen;
			timer.Interval = TimeSpan.FromSeconds(2);
			timer.Start();
		}

		private void timer_SplashScreen(object sender, EventArgs e)
		{
			timer.Stop();
			if (!Directory.Exists(GetLauncherPath()))
			{
				Directory.CreateDirectory(GetLauncherPath());
			}
			MainWindow mainWindow = new MainWindow();
			mainWindow.Show();
			this.Close();
		}
	}
}

using System;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Linq;
using LauncherConfig;

namespace CanaryLauncherUpdate
{
	public partial class SplashScreen : Window
	{
		DispatcherTimer timer = new DispatcherTimer();

		public SplashScreen()
		{
			InitializeComponent();
			Loaded += SplashScreen_Loaded;
		}

		private async void SplashScreen_Loaded(object sender, RoutedEventArgs e)
		{
			string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
			bool justUpdated = args.Contains(LauncherUpdater.UpdatedFlag);

			await Task.Run(() => LauncherUpdater.CleanupPreviousUpdate());

			foreach (ServerProfile profile in ServerProfile.All)
			{
				string prefix = "--config-" + profile.Key + "=";
				string over = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
				if (over != null)
				{
					profile.ConfigOverride = over.Substring(prefix.Length);
				}
			}

			// 2.0: load both servers. XXX is required (the launcher updates itself from its config),
			// ULTRA may be unavailable - it is then shown as such in the server choice.
			await Task.WhenAll(
				Task.Run(() => ServerProfile.Xxx.TryLoad()),
				Task.Run(() => ServerProfile.Ultra.TryLoad()));

			if (!ServerProfile.Xxx.IsAvailable)
			{
				MessageBox.Show("Could not load launcher configuration.\nCheck your internet connection and try again.\n\n" + ServerProfile.Xxx.LoadError,
					"XXXOTS Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
				Application.Current.Shutdown();
				return;
			}

			// Replace the launcher itself when a newer one is published (at most once per start)
			if (!justUpdated && LauncherUpdater.IsUpdateAvailable(ServerProfile.Xxx.Config))
			{
				labelStatus.Text = "Updating launcher...";
				labelStatus.Visibility = Visibility.Visible;
				string updateError = null;
				bool updated = await Task.Run(() => LauncherUpdater.TryUpdate(ServerProfile.Xxx.Config, args, out updateError));
				if (updated)
				{
					Application.Current.Shutdown();
					return;
				}
				labelStatus.Visibility = Visibility.Collapsed;
			}

			// No automatic client start any more - the player picks the server in the main window
			timer.Tick += timer_SplashScreen;
			timer.Interval = TimeSpan.FromSeconds(1);
			timer.Start();
		}

		private void timer_SplashScreen(object sender, EventArgs e)
		{
			timer.Stop();
			MainWindow mainWindow = new MainWindow();
			mainWindow.Show();
			this.Close();
		}
	}
}

using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Net;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Security.Cryptography;
using LauncherConfig;

namespace CanaryLauncherUpdate
{
	public partial class MainWindow : Window
	{
		// 2.0: the selected server; both profiles are loaded by SplashScreen before this window is created
		ServerProfile current;

		ClientConfig clientConfig => current.Config;
		string clientExecutableName => clientConfig.clientExecutable;
		string urlClient => clientConfig.newClientUrl;
		static string programVersion
		{
			get
			{
				Version v = LauncherUpdater.CurrentVersion;
				return v.Major + "." + v.Minor;
			}
		}

		string newVersion = "";
		bool needUpdate = false;
		bool downloading = false;

		WebClient webClient = new WebClient();

		// 2.1: XXXBOT (only for the XXX server; offered when the XXX config has bot fields)
		BotManager bot;
		bool botBusy = false;
		bool botUiLoading = false;

		private static string LauncherDirectory => AppDomain.CurrentDomain.BaseDirectory;

		private string GetLauncherPath(bool onlyBaseDirectory = false)
		{
			if (onlyBaseDirectory || clientConfig == null || string.IsNullOrEmpty(clientConfig.clientFolder))
			{
				return LauncherDirectory;
			}

			return Path.Combine(LauncherDirectory, clientConfig.clientFolder);
		}

		private string LocalConfigPath => Path.Combine(LauncherDirectory, current.LocalConfigName);

		private string GetClientExecutablePath()
		{
			return Path.Combine(GetLauncherPath(), "bin", clientExecutableName);
		}

		private string GetInstalledVersion()
		{
			return ClientConfig.GetLocalClientVersion(LauncherDirectory, current.LocalConfigName);
		}

		private BitmapImage Img(string packUri)
		{
			return new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), packUri));
		}

		private void StartClient()
		{
			string executable = GetClientExecutablePath();
			if (!File.Exists(executable))
			{
				MessageBox.Show("Client executable not found:\n" + executable, "XXXOTS Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
				return;
			}

			ClientConfig.ClearStaleServerCache(GetLauncherPath());
			Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable) });
			StartBotWithClient();
			this.Close();
		}

		public MainWindow()
		{
			InitializeComponent();
			webClient.DownloadProgressChanged += Client_DownloadProgressChanged;
			webClient.DownloadFileCompleted += Client_DownloadFileCompleted;
		}

		static void CreateShortcut()
		{
			try
			{
				string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
				string shortcutPath = Path.Combine(desktopPath, "XXXOTS.lnk");
				string launcherPath = Assembly.GetExecutingAssembly().Location;
				ShellLink.Create(shortcutPath, launcherPath, Path.GetDirectoryName(launcherPath), "XXXOTS Launcher");
			}
			catch (Exception)
			{
				// A missing desktop shortcut must never break the update
			}
		}

		private void TibiaLauncher_Load(object sender, RoutedEventArgs e)
		{
			labelVersion.Text = "v" + programVersion;
			bot = new BotManager(LauncherDirectory, ServerProfile.Xxx.Config);
			tileXxxName.Text = ServerProfile.Xxx.DisplayName;
			tileXxxSub.Text = ServerProfile.Xxx.Subtitle;
			tileUltraName.Text = ServerProfile.Ultra.DisplayName;
			tileUltraSub.Text = ServerProfile.Ultra.Subtitle;

			ServerProfile start = ServerProfile.LoadLastChoice(LauncherDirectory);
			if (start == null || !start.IsAvailable)
			{
				start = ServerProfile.Xxx;
			}
			SelectServer(start);
			RefreshStatusAsync();
		}

		// Online/players for both servers in the tiles (does not block the window)
		private async void RefreshStatusAsync()
		{
			foreach (ServerProfile profile in ServerProfile.All)
			{
				ServerProfile p = profile;
				string text;
				if (!p.IsAvailable)
				{
					text = "Unavailable";
				}
				else if (p.IsBeforeLaunch)
				{
					DateTime local = p.LaunchUtc.Value.ToLocalTime();
					text = "Start " + local.ToString("dd.MM, HH:mm");
				}
				else
				{
					int players = await Task.Run(() => p.FetchPlayersOnline());
					text = players >= 0 ? "Online: " + players : "Offline";
				}

				if (p == ServerProfile.Xxx) tileXxxStatus.Text = text; else tileUltraStatus.Text = text;
			}
		}

		private void tileXxx_MouseDown(object sender, MouseButtonEventArgs e)
		{
			SelectServer(ServerProfile.Xxx);
		}

		private void tileUltra_MouseDown(object sender, MouseButtonEventArgs e)
		{
			SelectServer(ServerProfile.Ultra);
		}

		private void SelectServer(ServerProfile profile)
		{
			if (downloading || profile == null)
			{
				return;
			}

			current = profile;
			ServerProfile.SaveLastChoice(LauncherDirectory, profile);

			// Look of the selected server
			backgroundBrush.ImageSource = Img(profile.Asset("background.jpg"));
			ImageLogoServer.Source = Img(profile.Asset("logo.png"));
			bgButtonBrush.ImageSource = Img(profile.Asset("bg_button_update.png"));
			bgHintsBrush.ImageSource = Img(profile.Asset("bg_hints.png"));

			Brush accent = (Brush)new BrushConverter().ConvertFromString(profile.AccentColor);
			Brush idle = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80));
			tileXxx.BorderBrush = profile == ServerProfile.Xxx ? accent : idle;
			tileUltra.BorderBrush = profile == ServerProfile.Ultra ? accent : idle;
			// wlasciciel 25.10: niewybrany kafelek bez przygaszenia - wybrany wyroznia tylko ramka w kolorze serwera

			RefreshClientState();
			RefreshBot();
		}

		// Play / Update / Download for the selected server
		private void RefreshClientState()
		{
			progressbarDownload.Visibility = Visibility.Collapsed;
			labelDownloadPercent.Visibility = Visibility.Collapsed;

			if (!current.IsAvailable)
			{
				buttonPlay.Visibility = Visibility.Collapsed;
				labelClientVersion.Content = "Unavailable";
				labelClientVersion.Visibility = Visibility.Visible;
				return;
			}

			newVersion = clientConfig.clientVersion;
			string actualVersion = GetInstalledVersion();
			bool installed = File.Exists(LocalConfigPath) && File.Exists(GetClientExecutablePath());

			buttonPlay.Visibility = Visibility.Visible;
			labelClientVersion.Visibility = Visibility.Visible;
			if (!installed)
			{
				SetButton(false);
				labelClientVersion.Content = "Download";
				buttonPlay_tooltip.Text = "Download";
				needUpdate = true;
			}
			else if (newVersion != actualVersion)
			{
				SetButton(false);
				labelClientVersion.Content = "Update";
				buttonPlay_tooltip.Text = "Update to " + newVersion;
				needUpdate = true;
			}
			else
			{
				SetButton(true);
				// wlasciciel 25.10: zamiast wersji klienta (15.00.249ccc...) nazwa swiata
				labelClientVersion.Content = current.ReadyLabel;
				buttonPlay_tooltip.Text = actualVersion;
				needUpdate = false;
			}
		}

		private void SetButton(bool play, bool hover = false)
		{
			string name = play ? (hover ? "button_hover_play.png" : "button_play.png") : (hover ? "button_hover_update.png" : "button_update.png");
			var brush = new ImageBrush(Img(current.Asset(name)));
			RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
			buttonPlay.Background = brush;
			buttonPlayIcon.Source = Img("pack://application:,,,/Assets/" + (play ? "icon_play.png" : "icon_update.png"));
		}

		private void UpdateClient()
		{
			Directory.CreateDirectory(GetLauncherPath());
			downloading = true;
			labelDownloadPercent.Visibility = Visibility.Visible;
			progressbarDownload.Visibility = Visibility.Visible;
			labelClientVersion.Visibility = Visibility.Collapsed;
			buttonPlay.Visibility = Visibility.Collapsed;
			webClient.DownloadFileAsync(new Uri(urlClient), Path.Combine(GetLauncherPath(), "tibia.zip"));
		}

		private async void buttonPlay_Click(object sender, RoutedEventArgs e)
		{
			if (!current.IsAvailable || botBusy || downloading)
			{
				return;
			}

			if (needUpdate || !File.Exists(GetClientExecutablePath()))
			{
				try
				{
					UpdateClient();
				}
				catch (Exception ex)
				{
					downloading = false;
					labelVersion.Text = ex.GetBaseException().Message;
				}
				return;
			}

			// 2.1: "start XXXBOT with the client" also keeps it up to date - the new version is fetched first
			if (BotGoesWithClient() && bot.NeedsInstall && !bot.RunningFromOurFolder())
			{
				buttonPlay.IsEnabled = false;
				labelClientVersion.Content = "XXXBOT…";
				try
				{
					await InstallBotAsync(false);
				}
				catch (Exception)
				{
					// The game always starts; the old bot (if any) is used
				}
				buttonPlay.IsEnabled = true;
			}

			StartClient();
		}

		// ------------------------------------------------------------ XXXBOT (2.1)

		// Wlasciciel 04.11: jeden launcher dla 2 serwerow i bot przy obu - bot sam rozpoznaje klienta XXXOTS / ULTRA
		private bool BotOffered => bot != null && current != null && current.IsAvailable && bot.IsOffered;

		private bool BotGoesWithClient()
		{
			return BotOffered && bot.LaunchWithClient;
		}

		private void StartBotWithClient()
		{
			try
			{
				if (BotGoesWithClient() && bot.IsInstalled && !bot.AnyRunning())
				{
					bot.Start();
				}
			}
			catch (Exception)
			{
				// The bot never blocks the game start
			}
		}

		private void RefreshBot()
		{
			if (!BotOffered)
			{
				tileBot.Visibility = Visibility.Collapsed;
				botPanel.Visibility = Visibility.Collapsed;
				return;
			}

			tileBot.Visibility = Visibility.Visible;
			string latest = BotManager.Short(bot.LatestVersion);
			bool withClient = bot.LaunchWithClient;
			tileBotSub.Text = withClient ? "Uruchamia się z klientem" : "Alpha Test · za darmo";

			if (botBusy)
			{
				tileBotStatus.Text = "Pobieranie…";
				tileBotStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF6, 0xCF, 0x7A));
			}
			else if (!bot.IsInstalled)
			{
				tileBotStatus.Text = "Pobierz " + latest;
				tileBotStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF6, 0xCF, 0x7A));
			}
			else if (bot.UpdateAvailable)
			{
				tileBotStatus.Text = "Nowa wersja " + latest + "!";
				tileBotStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x3C));
			}
			else
			{
				tileBotStatus.Text = "Wersja " + BotManager.Short(bot.InstalledVersion) + " ✓";
				tileBotStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xE3, 0x3A));
			}

			// Panel
			string installed = bot.IsInstalled ? BotManager.Short(bot.InstalledVersion) : "brak";
			botVersionText.Text = "Zainstalowana: " + installed + "   ·   Najnowsza: " + latest;
			if (!bot.IsInstalled)
			{
				botMainButton.Content = "Pobierz XXXBOT";
			}
			else if (bot.UpdateAvailable)
			{
				botMainButton.Content = "Aktualizuj do " + latest;
			}
			else
			{
				botMainButton.Content = "Uruchom bota";
			}
			botMainButton.IsEnabled = !botBusy;
			botManualButton.IsEnabled = !botBusy && File.Exists(bot.ManualPath);
			botProgressBox.Visibility = botBusy ? Visibility.Visible : Visibility.Collapsed;

			botUiLoading = true;
			botLaunchCheck.IsChecked = withClient;
			botUiLoading = false;
		}

		private void tileBot_Click(object sender, RoutedEventArgs e)
		{
			if (!BotOffered)
			{
				return;
			}
			RefreshBot();
			botPanel.Visibility = Visibility.Visible;
		}

		private void botPanel_MouseDown(object sender, MouseButtonEventArgs e)
		{
			// Only a click on the dark background closes it, not a click inside the window
			if (e.OriginalSource == botPanel && !botBusy)
			{
				botPanel.Visibility = Visibility.Collapsed;
			}
		}

		private void botClose_Click(object sender, RoutedEventArgs e)
		{
			botPanel.Visibility = Visibility.Collapsed;
		}

		private async void botMainButton_Click(object sender, RoutedEventArgs e)
		{
			if (!BotOffered || botBusy)
			{
				return;
			}

			if (bot.NeedsInstall)
			{
				if (bot.RunningFromOurFolder())
				{
					MessageBox.Show("XXXBOT jest uruchomiony.\nZamknij go (także ikonkę przy zegarze), a potem kliknij „Aktualizuj” jeszcze raz.",
						"XXXBOT", MessageBoxButton.OK, MessageBoxImage.Information);
					return;
				}
				try
				{
					await InstallBotAsync(true);
				}
				catch (Exception ex)
				{
					MessageBox.Show("Nie udało się pobrać XXXBOT:\n" + ex.GetBaseException().Message, "XXXBOT", MessageBoxButton.OK, MessageBoxImage.Error);
				}
				return;
			}

			if (bot.AnyRunning())
			{
				MessageBox.Show("XXXBOT już działa (sprawdź też ikonkę przy zegarze).", "XXXBOT", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			try
			{
				bot.Start();
			}
			catch (Exception ex)
			{
				MessageBox.Show("Nie udało się uruchomić XXXBOT:\n" + ex.GetBaseException().Message, "XXXBOT", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		private async Task InstallBotAsync(bool fromPanel)
		{
			botBusy = true;
			botProgress.Value = 0;
			botProgressText.Text = "Pobieranie…";
			RefreshBot();
			try
			{
				await bot.InstallAsync(percent => Dispatcher.BeginInvoke(new Action(() =>
				{
					botProgress.Value = percent;
					botProgressText.Text = percent >= 100 ? "Instalowanie…" : "Pobieranie… " + percent + "%";
				})));
			}
			finally
			{
				botBusy = false;
				RefreshBot();
			}
		}

		private void botManualButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (bot != null && File.Exists(bot.ManualPath))
				{
					Process.Start(new ProcessStartInfo(bot.ManualPath) { UseShellExecute = true });
				}
			}
			catch (Exception)
			{
			}
		}

		private void botPageButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (bot != null)
				{
					Process.Start(new ProcessStartInfo(bot.PageUrl) { UseShellExecute = true });
				}
			}
			catch (Exception)
			{
			}
		}

		private void botLaunchCheck_Changed(object sender, RoutedEventArgs e)
		{
			if (botUiLoading || bot == null)
			{
				return;
			}
			bot.LaunchWithClient = botLaunchCheck.IsChecked == true;
			RefreshBot();
		}

		// Extracts with overwrite and refuses entries that would land outside the client folder
		private void ExtractZip(string path)
		{
			string root = Path.GetFullPath(GetLauncherPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			using (ZipArchive archive = ZipFile.OpenRead(path))
			{
				foreach (ZipArchiveEntry entry in archive.Entries)
				{
					string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
					if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException("Invalid path in client archive: " + entry.FullName);
					}

					if (string.IsNullOrEmpty(entry.Name))
					{
						Directory.CreateDirectory(target);
						continue;
					}

					Directory.CreateDirectory(Path.GetDirectoryName(target));
					if (File.Exists(target))
					{
						File.SetAttributes(target, FileAttributes.Normal);
					}
					entry.ExtractToFile(target, true);
				}
			}
		}

		private static string ComputeSha256(string path)
		{
			using (SHA256 sha = SHA256.Create())
			using (FileStream stream = File.OpenRead(path))
			{
				return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
			}
		}

		private static bool IsPlainFolderName(string name)
		{
			return !string.IsNullOrWhiteSpace(name)
				&& name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
				&& name != "." && name != "..";
		}

		private void ShowUpdateFailed(string message)
		{
			downloading = false;
			string zipPath = Path.Combine(GetLauncherPath(), "tibia.zip");
			try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch (Exception) { }

			MessageBox.Show("Update failed:\n" + message, "XXXOTS Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
			RefreshClientState();
		}

		private async void Client_DownloadFileCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
		{
			if (e.Error != null || e.Cancelled)
			{
				ShowUpdateFailed(e.Error != null ? e.Error.GetBaseException().Message : "Download cancelled.");
				return;
			}

			ClientConfig config = clientConfig;
			string clientPath = GetLauncherPath();
			string localPath = LocalConfigPath;
			try
			{
				string zipPath = Path.Combine(clientPath, "tibia.zip");

				labelDownloadPercent.Content = "Verifying...";
				await Task.Run(() =>
				{
					// Only install the exact archive that was published
					if (!string.IsNullOrEmpty(config.clientSha256)
						&& !string.Equals(ComputeSha256(zipPath), config.clientSha256, StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException("Downloaded client is corrupted (checksum mismatch). Please try again.");
					}

					if (config.replaceFolders && config.replaceFolderName != null)
					{
						foreach (ReplaceFolderName folderName in config.replaceFolderName)
						{
							if (folderName == null || !IsPlainFolderName(folderName.name))
							{
								continue;
							}

							string folderPath = Path.Combine(clientPath, folderName.name);
							if (Directory.Exists(folderPath))
							{
								Directory.Delete(folderPath, true);
							}
						}
					}

					Directory.CreateDirectory(clientPath);
					ExtractZip(zipPath);
					File.Delete(zipPath);
				});
				progressbarDownload.Value = 100;

				// Remember the installed version of this server next to the launcher
				File.WriteAllText(localPath, JsonConvert.SerializeObject(config, Formatting.Indented));
			}
			catch (Exception ex)
			{
				ShowUpdateFailed(ex.GetBaseException().Message);
				return;
			}

			CreateShortcut();
			downloading = false;
			RefreshClientState();
		}

		private void Client_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
		{
			progressbarDownload.Value = e.ProgressPercentage;
			if (progressbarDownload.Value == 100) {
				labelDownloadPercent.Content = "Finishing, wait...";
			} else {
				labelDownloadPercent.Content = SizeSuffix(e.BytesReceived) + " / " + SizeSuffix(e.TotalBytesToReceive);
			}
		}

		static readonly string[] SizeSuffixes = { "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
		static string SizeSuffix(Int64 value, int decimalPlaces = 1)
		{
			if (decimalPlaces < 0) { throw new ArgumentOutOfRangeException("decimalPlaces"); }
			if (value < 0) { return "-" + SizeSuffix(-value, decimalPlaces); }
			if (value == 0) { return string.Format("{0:n" + decimalPlaces + "} bytes", 0); }

			int mag = (int)Math.Log(value, 1024);
			decimal adjustedSize = (decimal)value / (1L << (mag * 10));

			if (Math.Round(adjustedSize, decimalPlaces) >= 1000)
			{
				mag += 1;
				adjustedSize /= 1024;
			}
			return string.Format("{0:n" + decimalPlaces + "} {1}",
				adjustedSize,
				SizeSuffixes[mag]);
		}

		private void buttonPlay_MouseEnter(object sender, MouseEventArgs e)
		{
			if (current.IsAvailable) SetButton(!needUpdate, true);
		}

		private void buttonPlay_MouseLeave(object sender, MouseEventArgs e)
		{
			if (current.IsAvailable) SetButton(!needUpdate, false);
		}

		private void CloseButton_Click(object sender, RoutedEventArgs e)
		{
			if (botBusy)
			{
				return;
			}
			Close();
		}

		private void MinimizeButton_Click(object sender, RoutedEventArgs e)
		{
			WindowState = WindowState.Minimized;
		}
	}
}

using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Security.Cryptography;
using LauncherConfig;

namespace CanaryLauncherUpdate
{
	public partial class MainWindow : Window
	{
		// Loaded by SplashScreen before this window is created
		static ClientConfig clientConfig => ClientConfig.Current;

		static string clientExecutableName => clientConfig.clientExecutable;
		static string urlClient => clientConfig.newClientUrl;
		static string programVersion => clientConfig.launcherVersion;

		string newVersion = "";
		bool clientDownloaded = false;
		bool needUpdate = false;

		WebClient webClient = new WebClient();

		private string GetLauncherPath(bool onlyBaseDirectory = false)
		{
			string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
			if (string.IsNullOrEmpty(clientConfig.clientFolder) || onlyBaseDirectory) {
				return baseDirectory;
			}

			return Path.Combine(baseDirectory, clientConfig.clientFolder);
		}

		private string GetClientExecutablePath()
		{
			return Path.Combine(GetLauncherPath(), "bin", clientExecutableName);
		}

		private void StartClient()
		{
			string executable = GetClientExecutablePath();
			if (!File.Exists(executable))
			{
				MessageBox.Show("Client executable not found:\n" + executable, "XXXOTS Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
				return;
			}

			Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable) });
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
			ImageLogoServer.Source = new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/logo.png"));
			ImageLogoCompany.Source = new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/logo_company.png"));

			newVersion = clientConfig.clientVersion;
			progressbarDownload.Visibility = Visibility.Collapsed;
			labelClientVersion.Visibility = Visibility.Collapsed;
			labelDownloadPercent.Visibility = Visibility.Collapsed;

			if (File.Exists(Path.Combine(GetLauncherPath(true), ClientConfig.LocalConfigName)))
			{
				// Read actual client version
				string actualVersion = GetClientVersion(GetLauncherPath(true));
				labelVersion.Text = "v" + programVersion;

				if (newVersion != actualVersion)
				{
					buttonPlay.Background = new ImageBrush(new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/button_update.png")));
					buttonPlayIcon.Source = new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/icon_update.png"));
					labelClientVersion.Content = newVersion;
					labelClientVersion.Visibility = Visibility.Visible;
					buttonPlay.Visibility = Visibility.Visible;
					buttonPlay_tooltip.Text = "Update";
					needUpdate = true;
				}
			}
			if (!File.Exists(Path.Combine(GetLauncherPath(true), ClientConfig.LocalConfigName)) || Directory.Exists(GetLauncherPath()) && Directory.GetFiles(GetLauncherPath()).Length == 0 && Directory.GetDirectories(GetLauncherPath()).Length == 0)
			{
				labelVersion.Text = "v" + programVersion;
				buttonPlay.Background = new ImageBrush(new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/button_update.png")));
				buttonPlayIcon.Source = new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/icon_update.png"));
				labelClientVersion.Content = "Download";
				labelClientVersion.Visibility = Visibility.Visible;
				buttonPlay.Visibility = Visibility.Visible;
				buttonPlay_tooltip.Text = "Download";
				needUpdate = true;
			}
		}

		static string GetClientVersion(string path)
		{
			return ClientConfig.GetLocalClientVersion(path);
		}

		private void UpdateClient()
		{
			if (!Directory.Exists(GetLauncherPath(true)))
			{
				Directory.CreateDirectory(GetLauncherPath());
			}
			labelDownloadPercent.Visibility = Visibility.Visible;
			progressbarDownload.Visibility = Visibility.Visible;
			labelClientVersion.Visibility = Visibility.Collapsed;
			buttonPlay.Visibility = Visibility.Collapsed;
			webClient.DownloadFileAsync(new Uri(urlClient), Path.Combine(GetLauncherPath(), "tibia.zip"));
		}

		private void buttonPlay_Click(object sender, RoutedEventArgs e)
		{
			if (needUpdate == true || !Directory.Exists(GetLauncherPath()))
			{
				try
				{
					UpdateClient();
				}
				catch (Exception ex)
				{
					labelVersion.Text = ex.ToString();
				}
			}
			else
			{
				if (clientDownloaded == true || File.Exists(GetClientExecutablePath()))
				{
					StartClient();
				}
				else
				{
					try
					{
						UpdateClient();
					}
					catch (Exception ex)
					{
						labelVersion.Text = ex.ToString();
					}
				}
			}
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
			string zipPath = Path.Combine(GetLauncherPath(), "tibia.zip");
			try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch (Exception) { }

			MessageBox.Show("Update failed:\n" + message, "XXXOTS Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
			progressbarDownload.Visibility = Visibility.Collapsed;
			labelDownloadPercent.Visibility = Visibility.Collapsed;
			labelClientVersion.Visibility = Visibility.Visible;
			buttonPlay.Visibility = Visibility.Visible;
		}

		private async void Client_DownloadFileCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
		{
			if (e.Error != null || e.Cancelled)
			{
				ShowUpdateFailed(e.Error != null ? e.Error.GetBaseException().Message : "Download cancelled.");
				return;
			}

			try
			{
				string zipPath = Path.Combine(GetLauncherPath(), "tibia.zip");

				// Adds the task to a secondary task to prevent the program from crashing while this is running
				labelDownloadPercent.Content = "Verifying...";
				await Task.Run(() =>
				{
					// Only install the exact archive that was published
					if (!string.IsNullOrEmpty(clientConfig.clientSha256)
						&& !string.Equals(ComputeSha256(zipPath), clientConfig.clientSha256, StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException("Downloaded client is corrupted (checksum mismatch). Please try again.");
					}

					if (clientConfig.replaceFolders && clientConfig.replaceFolderName != null)
					{
						foreach (ReplaceFolderName folderName in clientConfig.replaceFolderName)
						{
							if (folderName == null || !IsPlainFolderName(folderName.name))
							{
								continue;
							}

							string folderPath = Path.Combine(GetLauncherPath(), folderName.name);
							if (Directory.Exists(folderPath))
							{
								Directory.Delete(folderPath, true);
							}
						}
					}

					Directory.CreateDirectory(GetLauncherPath());
					ExtractZip(zipPath);
					File.Delete(zipPath);
				});
				progressbarDownload.Value = 100;

				// Remember the installed version next to the launcher
				string localPath = Path.Combine(GetLauncherPath(true), ClientConfig.LocalConfigName);
				File.WriteAllText(localPath, JsonConvert.SerializeObject(clientConfig, Formatting.Indented));
			}
			catch (Exception ex)
			{
				ShowUpdateFailed(ex.GetBaseException().Message);
				return;
			}

			buttonPlay.Background = new ImageBrush(new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/button_play.png")));
			buttonPlayIcon.Source = new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/icon_play.png"));

			CreateShortcut();

			needUpdate = false;
			clientDownloaded = true;
			labelClientVersion.Content = GetClientVersion(GetLauncherPath(true));
			buttonPlay_tooltip.Text = GetClientVersion(GetLauncherPath(true));
			labelClientVersion.Visibility = Visibility.Visible;
			buttonPlay.Visibility = Visibility.Visible;
			progressbarDownload.Visibility = Visibility.Collapsed;
			labelDownloadPercent.Visibility = Visibility.Collapsed;
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
			if (File.Exists(Path.Combine(GetLauncherPath(true), ClientConfig.LocalConfigName)))
			{
				string actualVersion = GetClientVersion(GetLauncherPath(true));
				if (newVersion != actualVersion)
				{
					buttonPlay.Background = new ImageBrush(new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/button_hover_update.png")));
				}
				if (newVersion == actualVersion)
				{
					buttonPlay.Background = new ImageBrush(new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/button_hover_play.png")));
				}
			}
			else
			{
				buttonPlay.Background = new ImageBrush(new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/button_hover_update.png")));
			}
		}

		private void buttonPlay_MouseLeave(object sender, MouseEventArgs e)
		{
			if (File.Exists(Path.Combine(GetLauncherPath(true), ClientConfig.LocalConfigName)))
			{
				string actualVersion = GetClientVersion(GetLauncherPath(true));
				if (newVersion != actualVersion)
				{
					buttonPlay.Background = new ImageBrush(new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/button_update.png")));
				}
				if (newVersion == actualVersion)
				{
					buttonPlay.Background = new ImageBrush(new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/button_play.png")));
				}
			}
			else
			{
				buttonPlay.Background = new ImageBrush(new BitmapImage(new Uri(BaseUriHelper.GetBaseUri(this), "pack://application:,,,/Assets/button_update.png")));
			}
		}

		private void CloseButton_Click(object sender, RoutedEventArgs e)
		{
			Close();
		}

		private void RestoreButton_Click(object sender, RoutedEventArgs e)
		{
			if (ResizeMode != ResizeMode.NoResize)
			{
				if (WindowState == WindowState.Normal)
					WindowState = WindowState.Maximized;
				else
					WindowState = WindowState.Normal;
			}
		}

		private void MinimizeButton_Click(object sender, RoutedEventArgs e)
		{
			WindowState = WindowState.Minimized;
		}

	}
}

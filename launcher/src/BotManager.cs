using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace LauncherConfig
{
	// 2.1: XXXBOT - our own bot for the XXXOTS and XXXOTS ULTRA clients (no cavebot, no follow). The launcher downloads it,
	// keeps it up to date and can start it together with the client. Everything comes from the XXX config
	// (botVersion / botUrl / botSha256); without these fields the bot is not offered at all.
	// Bot settings live in %APPDATA%\XXXBOT, so replacing the program files never touches them.
	public class BotManager
	{
		private const string SettingsFile = "launcher_xxxbot.json";
		private const string DownloadName = "xxxbot-update.zip";
		private const string StagingName = "xxxbot-update";
		private const string OldSuffix = ".old";

		private readonly string launcherDirectory;
		private readonly ClientConfig config;

		public BotManager(string launcherDirectory, ClientConfig config)
		{
			this.launcherDirectory = launcherDirectory;
			this.config = config;
		}

		public bool IsOffered => config != null
			&& !string.IsNullOrEmpty(config.botUrl)
			&& !string.IsNullOrEmpty(config.botSha256)
			&& LatestVersion != null;

		public string Folder => Path.Combine(launcherDirectory, IsPlainName(config?.botFolder) ? config.botFolder : "XXXBOT");
		public string ExeName => IsPlainName(config?.botExecutable) ? config.botExecutable : "XXXBOT.exe";
		public string ExePath => Path.Combine(Folder, ExeName);
		public string ManualPath => Path.Combine(Folder, "INSTRUKCJA.txt");
		public string PageUrl => string.IsNullOrEmpty(config?.botPageUrl) ? "https://xxxots.pl/index.php/downloads#xxxbot" : config.botPageUrl;

		public bool IsInstalled => File.Exists(ExePath);

		public Version LatestVersion
		{
			get
			{
				Version v;
				return Version.TryParse(config?.botVersion, out v) ? Normalize(v) : null;
			}
		}

		public Version InstalledVersion
		{
			get
			{
				if (!IsInstalled)
				{
					return null;
				}
				try
				{
					FileVersionInfo info = FileVersionInfo.GetVersionInfo(ExePath);
					return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
				}
				catch (Exception)
				{
					return null;
				}
			}
		}

		public bool UpdateAvailable
		{
			get
			{
				if (!IsOffered || !IsInstalled)
				{
					return false;
				}
				Version installed = InstalledVersion;
				return installed == null || LatestVersion > Normalize(installed);
			}
		}

		// Missing or older than the published version
		public bool NeedsInstall => IsOffered && (!IsInstalled || UpdateAvailable);

		public static string Short(Version v)
		{
			if (v == null)
			{
				return "?";
			}
			return v.Build > 0 ? v.Major + "." + v.Minor + "." + v.Build : v.Major + "." + v.Minor;
		}

		// ------------------------------------------------------------ settings next to the launcher

		private class Settings
		{
			public bool launchWithClient { get; set; }
		}

		public bool LaunchWithClient
		{
			get
			{
				try
				{
					string path = Path.Combine(launcherDirectory, SettingsFile);
					if (File.Exists(path))
					{
						Settings s = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path));
						return s != null && s.launchWithClient;
					}
				}
				catch (Exception)
				{
				}
				return false;
			}
			set
			{
				try
				{
					File.WriteAllText(Path.Combine(launcherDirectory, SettingsFile),
						JsonConvert.SerializeObject(new Settings { launchWithClient = value }, Formatting.Indented));
				}
				catch (Exception)
				{
					// The choice is a convenience only
				}
			}
		}

		// ------------------------------------------------------------ running bot

		// Any XXXBOT is running (also one the player downloaded by hand into another folder).
		// The launcher never starts a second one: one bot already follows the client.
		public bool AnyRunning()
		{
			return RunningPaths().Count > 0;
		}

		// A bot started from our folder blocks the update; a process whose path cannot be read counts as ours
		public bool RunningFromOurFolder()
		{
			string ours = Path.GetFullPath(ExePath);
			foreach (string path in RunningPaths())
			{
				if (path == null || string.Equals(Path.GetFullPath(path), ours, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		private List<string> RunningPaths()
		{
			var paths = new List<string>();
			foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
			{
				try
				{
					paths.Add(ImagePath(p.Id));
				}
				finally
				{
					p.Dispose();
				}
			}
			return paths;
		}

		public void Start()
		{
			Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = Folder, UseShellExecute = true });
		}

		// ------------------------------------------------------------ download / update

		// Downloads the published archive, checks its SHA-256 and replaces the program files.
		// Any failure leaves the installed version as it was.
		public async Task InstallAsync(Action<int> progress)
		{
			string zipPath = Path.Combine(launcherDirectory, DownloadName);
			string stagingPath = Path.Combine(launcherDirectory, StagingName);
			TryDeleteFile(zipPath);
			TryDeleteDirectory(stagingPath);

			try
			{
				using (WebClient webClient = new WebClient())
				{
					if (progress != null)
					{
						webClient.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
					}
					await webClient.DownloadFileTaskAsync(new Uri(config.botUrl), zipPath);
				}

				await Task.Run(() =>
				{
					if (!string.Equals(ComputeSha256(zipPath), config.botSha256, StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException("Pobrany plik jest uszkodzony (zła suma kontrolna). Spróbuj ponownie.");
					}

					ExtractZip(zipPath, stagingPath);
					string source = FindProgramFolder(stagingPath);
					if (source == null)
					{
						throw new InvalidDataException("W pobranej paczce nie ma " + ExeName + ".");
					}

					if (RunningFromOurFolder())
					{
						throw new IOException("XXXBOT jest uruchomiony. Zamknij go (także ikonkę przy zegarze) i spróbuj ponownie.");
					}

					ReplaceFiles(source, Folder);
				});
			}
			finally
			{
				TryDeleteFile(zipPath);
				TryDeleteDirectory(stagingPath);
			}
		}

		// The archive keeps the program in "XXXBOT/" (as on the website) or directly at its root
		private string FindProgramFolder(string stagingPath)
		{
			if (File.Exists(Path.Combine(stagingPath, ExeName)))
			{
				return stagingPath;
			}
			foreach (string dir in Directory.GetDirectories(stagingPath))
			{
				if (File.Exists(Path.Combine(dir, ExeName)))
				{
					return dir;
				}
			}
			return null;
		}

		// Moves every file of the new version into place; the previous files are restored when anything fails
		private static void ReplaceFiles(string source, string target)
		{
			string sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			var replaced = new List<string>();
			var created = new List<string>();
			try
			{
				foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
				{
					string relative = Path.GetFullPath(file).Substring(sourceRoot.Length);
					string destination = Path.Combine(target, relative);
					Directory.CreateDirectory(Path.GetDirectoryName(destination));
					if (File.Exists(destination))
					{
						string backup = destination + OldSuffix;
						TryDeleteFile(backup);
						File.SetAttributes(destination, FileAttributes.Normal);
						File.Move(destination, backup);
						replaced.Add(destination);
					}
					else
					{
						created.Add(destination);
					}
					File.Move(file, destination);
				}
			}
			catch (Exception)
			{
				foreach (string path in created)
				{
					TryDeleteFile(path);
				}
				foreach (string path in replaced)
				{
					try
					{
						TryDeleteFile(path);
						File.Move(path + OldSuffix, path);
					}
					catch (Exception)
					{
					}
				}
				throw;
			}

			foreach (string path in replaced)
			{
				TryDeleteFile(path + OldSuffix);
			}
		}

		// Extracts and refuses entries that would land outside the staging folder
		private static void ExtractZip(string zipPath, string stagingPath)
		{
			string root = Path.GetFullPath(stagingPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			Directory.CreateDirectory(root);
			using (ZipArchive archive = ZipFile.OpenRead(zipPath))
			{
				foreach (ZipArchiveEntry entry in archive.Entries)
				{
					string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
					if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException("Nieprawidłowa ścieżka w paczce: " + entry.FullName);
					}
					if (string.IsNullOrEmpty(entry.Name))
					{
						Directory.CreateDirectory(destination);
						continue;
					}
					Directory.CreateDirectory(Path.GetDirectoryName(destination));
					entry.ExtractToFile(destination, true);
				}
			}
		}

		// ------------------------------------------------------------ helpers

		private static bool IsPlainName(string name)
		{
			return !string.IsNullOrWhiteSpace(name)
				&& name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
				&& name != "." && name != "..";
		}

		private static Version Normalize(Version v)
		{
			return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
		}

		private static string ComputeSha256(string path)
		{
			using (SHA256 sha = SHA256.Create())
			using (FileStream stream = File.OpenRead(path))
			{
				return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
			}
		}

		private static void TryDeleteFile(string path)
		{
			try
			{
				if (File.Exists(path))
				{
					File.SetAttributes(path, FileAttributes.Normal);
					File.Delete(path);
				}
			}
			catch (Exception)
			{
			}
		}

		private static void TryDeleteDirectory(string path)
		{
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, true);
				}
			}
			catch (Exception)
			{
			}
		}

		// Works for 64-bit processes from a 32-bit launcher (Process.MainModule does not)
		private static string ImagePath(int pid)
		{
			IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
			if (handle == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				var sb = new StringBuilder(1024);
				int length = sb.Capacity;
				return QueryFullProcessImageName(handle, 0, sb, ref length) ? sb.ToString(0, length) : null;
			}
			finally
			{
				CloseHandle(handle);
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CloseHandle(IntPtr handle);
	}
}

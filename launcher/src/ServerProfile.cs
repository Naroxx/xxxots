using System;
using System.IO;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

namespace LauncherConfig
{
	// XXXOTS Launcher 2.0: one launcher, two servers. Each server keeps its own config repo, client folder,
	// local version file and look; the launcher itself updates from the XXX config (launcherVersion/launcherUrl).
	public class ServerProfile
	{
		public string Key { get; private set; }
		public string DisplayName { get; private set; }
		public string Subtitle { get; private set; }
		public string ConfigUrl { get; private set; }
		// Installed client version is remembered next to the launcher; XXX keeps the 1.x file name,
		// so existing XXX installs stay "up to date" after the launcher updates itself to 2.0
		public string LocalConfigName { get; private set; }
		public string StatusUrl { get; private set; }
		// Before this moment the server is shown as "coming soon" (ULTRA is in closed mode until launch)
		public DateTime? LaunchUtc { get; private set; }
		// Suffix of the asset files for this server ("" = default XXX assets, "_ultra" = ULTRA assets)
		public string AssetSuffix { get; private set; }
		public string AccentColor { get; private set; }
		// Shown under the button when the client is installed and up to date (instead of the client version)
		public string ReadyLabel { get; private set; }

		public ClientConfig Config { get; set; }
		public string LoadError { get; set; }

		public bool IsAvailable => Config != null;
		public bool IsBeforeLaunch => LaunchUtc.HasValue && DateTime.UtcNow < LaunchUtc.Value;

		public static readonly ServerProfile Xxx = new ServerProfile
		{
			Key = "xxx",
			DisplayName = "XXXOTS",
			Subtitle = "No Resets, NO-PVP",
			ConfigUrl = "https://raw.githubusercontent.com/Naroxx/xxxots/main/config/launcher.json",
			LocalConfigName = "launcher_config.json",
			StatusUrl = "http://51.75.53.196/login.php",
			LaunchUtc = null,
			AssetSuffix = "",
			AccentColor = "#E8B04A",
			ReadyLabel = "Long Term",
		};

		public static readonly ServerProfile Ultra = new ServerProfile
		{
			Key = "ultra",
			DisplayName = "XXXOTS ULTRA",
			Subtitle = "Fast Attack, -50% CD, PVP",
			ConfigUrl = "https://raw.githubusercontent.com/Naroxx/xxxotsultra/main/config/launcher.json",
			LocalConfigName = "launcher_config_ultra.json",
			StatusUrl = "http://51.178.194.29/login.php",
			// 13.11.2026 18:00 Europe/Warsaw (CET, UTC+1)
			LaunchUtc = new DateTime(2026, 11, 13, 17, 0, 0, DateTimeKind.Utc),
			AssetSuffix = "_ultra",
			AccentColor = "#8BE33A",
			ReadyLabel = "ULTRA",
		};

		public static readonly ServerProfile[] All = { Xxx, Ultra };

		public static ServerProfile ByKey(string key)
		{
			foreach (ServerProfile profile in All)
			{
				if (string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase))
				{
					return profile;
				}
			}
			return null;
		}

		public string Asset(string fileName)
		{
			if (string.IsNullOrEmpty(AssetSuffix))
			{
				return "pack://application:,,,/Assets/" + fileName;
			}
			string ext = Path.GetExtension(fileName);
			return "pack://application:,,,/Assets/" + Path.GetFileNameWithoutExtension(fileName) + AssetSuffix + ext;
		}

		// Testing only: "--config-xxx=<file or url>" / "--config-ultra=<file or url>" replaces the config source
		public string ConfigOverride { get; set; }

		public bool TryLoad()
		{
			try
			{
				Config = ClientConfig.loadFromUrl(string.IsNullOrEmpty(ConfigOverride) ? ConfigUrl : ConfigOverride);
				if (Config == null || string.IsNullOrEmpty(Config.clientVersion) || string.IsNullOrEmpty(Config.newClientUrl))
				{
					Config = null;
					LoadError = "launcher config is missing clientVersion or newClientUrl.";
					return false;
				}
				LoadError = null;
				return true;
			}
			catch (Exception ex)
			{
				Config = null;
				LoadError = ex.GetBaseException().Message;
				return false;
			}
		}

		// Players online from the server's login.php ("cacheinfo"); -1 when the server does not answer
		public int FetchPlayersOnline()
		{
			try
			{
				using (HttpClient client = new HttpClient())
				{
					client.Timeout = TimeSpan.FromSeconds(5);
					var content = new StringContent("{\"type\":\"cacheinfo\"}", Encoding.UTF8, "application/json");
					string body = client.PostAsync(StatusUrl, content).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();
					JToken players = JObject.Parse(body)["playersonline"];
					return players != null ? players.Value<int>() : -1;
				}
			}
			catch (Exception)
			{
				return -1;
			}
		}

		// Last chosen server, remembered next to the launcher
		private const string LastServerFile = "launcher_last_server.txt";

		public static ServerProfile LoadLastChoice(string launcherDirectory)
		{
			try
			{
				string path = Path.Combine(launcherDirectory, LastServerFile);
				if (File.Exists(path))
				{
					return ByKey(File.ReadAllText(path).Trim());
				}
			}
			catch (Exception)
			{
			}
			return null;
		}

		public static void SaveLastChoice(string launcherDirectory, ServerProfile profile)
		{
			try
			{
				File.WriteAllText(Path.Combine(launcherDirectory, LastServerFile), profile.Key);
			}
			catch (Exception)
			{
				// Remembering the choice is a convenience only
			}
		}
	}
}

using System;
using System.IO;
using System.Net.Http;
using Newtonsoft.Json;

namespace LauncherConfig
{
	public class ClientConfig
	{
		// Remote launcher_config.json, the single source of truth for versions and download links
		public const string ConfigUrl = "https://raw.githubusercontent.com/Naroxx/xxxots/main/launcher_config.json";
		public const string LocalConfigName = "launcher_config.json";

		public string clientVersion { get; set; }
		public string launcherVersion { get; set; }
		public bool replaceFolders { get; set; }
		public ReplaceFolderName[] replaceFolderName { get; set; }
		public string clientFolder { get; set; }
		public string newClientUrl { get; set; }
		// SHA-256 (hex) of the archive at newClientUrl; the download is rejected when it does not match
		public string clientSha256 { get; set; }
		public string clientExecutable { get; set; }

		// Loaded once by SplashScreen and shared with MainWindow
		public static ClientConfig Current { get; private set; }

		public static ClientConfig loadFromUrl(string url)
		{
			using (HttpClient client = new HttpClient())
			{
				client.Timeout = TimeSpan.FromSeconds(15);
				// Avoid stale cached copies of the config
				client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
				string jsonString = client.GetStringAsync(url).GetAwaiter().GetResult();
				return JsonConvert.DeserializeObject<ClientConfig>(jsonString);
			}
		}

		public static bool TryLoadRemote(out string error)
		{
			try
			{
				ClientConfig config = loadFromUrl(ConfigUrl);
				if (config == null || string.IsNullOrEmpty(config.clientVersion) || string.IsNullOrEmpty(config.newClientUrl))
				{
					error = "launcher_config.json is missing clientVersion or newClientUrl.";
					return false;
				}
				Current = config;
				error = null;
				return true;
			}
			catch (Exception ex)
			{
				error = ex.GetBaseException().Message;
				return false;
			}
		}

		// Version of the client installed next to the launcher, or "" when unknown
		public static string GetLocalClientVersion(string directory)
		{
			string path = Path.Combine(directory, LocalConfigName);
			if (!File.Exists(path))
			{
				return "";
			}

			try
			{
				ClientConfig local = JsonConvert.DeserializeObject<ClientConfig>(File.ReadAllText(path));
				return local?.clientVersion ?? "";
			}
			catch (Exception)
			{
				return "";
			}
		}
	}

	public class ReplaceFolderName
	{
		public string name { get; set; }
	}
}

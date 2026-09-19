using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using LauncherConfig;

namespace CanaryLauncherUpdate
{
	// Replaces the launcher's own files with a newer published version.
	// Any failure leaves the current version in place so the game can always start.
	internal static class LauncherUpdater
	{
		public const string UpdatedFlag = "--updated";
		private const string OldSuffix = ".old";
		private const string DownloadName = "launcher-update.zip";
		private const string StagingName = "launcher-update";

		private static string LauncherDirectory => AppDomain.CurrentDomain.BaseDirectory;
		private static string LauncherExePath => Assembly.GetExecutingAssembly().Location;

		public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

		// Removes leftovers of a previous update; the old process may still be exiting
		public static void CleanupPreviousUpdate()
		{
			for (int attempt = 0; attempt < 10; attempt++)
			{
				bool pending = false;
				var leftovers = new List<string>(Directory.GetFiles(LauncherDirectory, "*.exe" + OldSuffix));
				leftovers.AddRange(Directory.GetFiles(LauncherDirectory, "*.dll" + OldSuffix));
				foreach (string file in leftovers)
				{
					try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
					catch (Exception) { pending = true; }
				}
				if (!pending)
				{
					break;
				}
				Thread.Sleep(300);
			}

			TryDeleteFile(Path.Combine(LauncherDirectory, DownloadName));
			TryDeleteDirectory(Path.Combine(LauncherDirectory, StagingName));
		}

		public static bool IsUpdateAvailable(ClientConfig config)
		{
			if (config == null || string.IsNullOrEmpty(config.launcherUrl) || string.IsNullOrEmpty(config.launcherSha256))
			{
				return false;
			}

			Version remote;
			if (!Version.TryParse(config.launcherVersion, out remote))
			{
				return false;
			}

			return Normalize(remote) > Normalize(CurrentVersion);
		}

		// Returns true when the new launcher was installed and started; the caller must exit
		public static bool TryUpdate(ClientConfig config, string[] args, out string error)
		{
			error = null;
			string zipPath = Path.Combine(LauncherDirectory, DownloadName);
			string stagingPath = Path.Combine(LauncherDirectory, StagingName);
			var replaced = new List<string>();

			try
			{
				TryDeleteFile(zipPath);
				TryDeleteDirectory(stagingPath);

				using (WebClient webClient = new WebClient())
				{
					webClient.DownloadFile(new Uri(config.launcherUrl), zipPath);
				}

				if (!string.Equals(ComputeSha256(zipPath), config.launcherSha256, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("launcher checksum mismatch");
				}

				ZipFile.ExtractToDirectory(zipPath, stagingPath);
				string exeName = Path.GetFileName(LauncherExePath);
				if (!File.Exists(Path.Combine(stagingPath, exeName)))
				{
					throw new InvalidDataException("launcher archive does not contain " + exeName);
				}

				// A running exe/dll can be renamed but not overwritten
				foreach (string newFile in Directory.GetFiles(stagingPath))
				{
					string target = Path.Combine(LauncherDirectory, Path.GetFileName(newFile));
					if (File.Exists(target))
					{
						string backup = target + OldSuffix;
						TryDeleteFile(backup);
						File.Move(target, backup);
					}
					replaced.Add(target);
					File.Move(newFile, target);
				}

				TryDeleteFile(zipPath);
				TryDeleteDirectory(stagingPath);

				var startInfo = new ProcessStartInfo(LauncherExePath)
				{
					WorkingDirectory = LauncherDirectory,
					Arguments = BuildArguments(args),
					UseShellExecute = false
				};
				Process.Start(startInfo);
				return true;
			}
			catch (Exception ex)
			{
				error = ex.GetBaseException().Message;
				Rollback(replaced);
				TryDeleteFile(zipPath);
				TryDeleteDirectory(stagingPath);
				return false;
			}
		}

		private static void Rollback(List<string> replaced)
		{
			foreach (string target in replaced)
			{
				try
				{
					string backup = target + OldSuffix;
					if (File.Exists(backup))
					{
						TryDeleteFile(target);
						File.Move(backup, target);
					}
				}
				catch (Exception)
				{
				}
			}
		}

		private static string BuildArguments(string[] args)
		{
			var parts = new List<string>();
			foreach (string arg in args)
			{
				if (arg != UpdatedFlag)
				{
					parts.Add("\"" + arg.Replace("\"", "") + "\"");
				}
			}
			parts.Add(UpdatedFlag);
			return string.Join(" ", parts);
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
	}
}

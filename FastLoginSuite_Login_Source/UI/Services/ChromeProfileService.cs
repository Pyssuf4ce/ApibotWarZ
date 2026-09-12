using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApibotWarZ.UI.Services
{
    public class ChromeProfileInfo
    {
        public int WorkerId { get; set; } = 1;
        public string DirectoryName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Path { get; set; } = "";
        public bool ExistsOnDisk { get; set; } = false;
    }

    public static class ChromeProfileService
    {
        public static string GetBotProfilesBaseDir()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\BotProfiles");
            Directory.CreateDirectory(path);
            return path;
        }

        public static string GetChromeExePath()
        {
            string localApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe");
            if (File.Exists(localApp)) return localApp;

            string progFiles = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
            if (File.Exists(progFiles)) return progFiles;

            string progFilesX86 = @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe";
            if (File.Exists(progFilesX86)) return progFilesX86;

            return "chrome.exe";
        }

        public static string GetExtensionPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string extDir = Path.Combine(baseDir, "extension");
            if (Directory.Exists(extDir)) return Path.GetFullPath(extDir);

            string devExt = Path.Combine(baseDir, "..", "..", "..", "extension");
            if (Directory.Exists(devExt)) return Path.GetFullPath(devExt);

            string devExt2 = Path.Combine(baseDir, "..", "..", "..", "..", "FastLoginSuite_Login_Source", "extension");
            if (Directory.Exists(devExt2)) return Path.GetFullPath(devExt2);

            return extDir;
        }

        public static List<ChromeProfileInfo> GetBotProfiles(int expectedCount = 0)
        {
            var list = new List<ChromeProfileInfo>();
            string baseDir = GetBotProfilesBaseDir();
            if (!Directory.Exists(baseDir)) return list;

            var dirs = Directory.GetDirectories(baseDir, "worker_*");
            var sortedDirs = new List<(int wid, string dir)>();

            foreach (var dir in dirs)
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("worker_") && int.TryParse(name.Substring("worker_".Length), out int wid))
                {
                    sortedDirs.Add((wid, dir));
                }
            }

            sortedDirs.Sort((a, b) => a.wid.CompareTo(b.wid));

            foreach (var item in sortedDirs)
            {
                list.Add(new ChromeProfileInfo
                {
                    WorkerId = item.wid,
                    DirectoryName = $"worker_{item.wid}",
                    DisplayName = $"HOF Bot {item.wid}",
                    Path = item.dir,
                    ExistsOnDisk = true
                });
            }

            return list;
        }

        public static bool CreateBotProfiles(int count)
        {
            try
            {
                string baseDir = GetBotProfilesBaseDir();
                string extDir = GetExtensionPath();

                for (int i = 1; i <= count; i++)
                {
                    InitializeWorkerSession(i, baseDir, extDir);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating bot profiles: {ex.Message}");
                return false;
            }
        }

        public static string InitializeWorkerSession(int wid, string baseDir, string extDir)
        {
            string profilePath = Path.Combine(baseDir, $"worker_{wid}");
            string defaultDir = Path.Combine(profilePath, "Default");
            string workerExt = Path.Combine(profilePath, "extension");

            Directory.CreateDirectory(profilePath);
            Directory.CreateDirectory(defaultDir);
            Directory.CreateDirectory(workerExt);

            // 1. Skip First Run
            string firstRunFile = Path.Combine(profilePath, "First Run");
            if (!File.Exists(firstRunFile))
            {
                try { File.WriteAllText(firstRunFile, ""); } catch { }
            }

            // 2. Copy extension files into worker profile
            try
            {
                if (Directory.Exists(extDir))
                {
                    foreach (var file in Directory.GetFiles(extDir))
                    {
                        File.Copy(file, Path.Combine(workerExt, Path.GetFileName(file)), true);
                    }
                }
            }
            catch { }

            // 3. Pre-configure Preferences (Dev Mode ON, no first run popups, clean session)
            string prefFile = Path.Combine(defaultDir, "Preferences");
            try
            {
                var prefs = new JsonObject();
                if (File.Exists(prefFile))
                {
                    try
                    {
                        var text = File.ReadAllText(prefFile);
                        var node = JsonNode.Parse(text);
                        if (node is JsonObject jo) prefs = jo;
                    }
                    catch { }
                }

                if (!prefs.ContainsKey("extensions")) prefs["extensions"] = new JsonObject();
                var extObj = prefs["extensions"] as JsonObject ?? new JsonObject();
                if (!extObj.ContainsKey("ui")) extObj["ui"] = new JsonObject();
                var uiObj = extObj["ui"] as JsonObject ?? new JsonObject();
                uiObj["developer_mode"] = true;
                extObj["ui"] = uiObj;
                prefs["extensions"] = extObj;

                prefs["credentials_enable_service"] = false;

                if (!prefs.ContainsKey("profile")) prefs["profile"] = new JsonObject();
                var profObj = prefs["profile"] as JsonObject ?? new JsonObject();
                profObj["password_manager_enabled"] = false;
                profObj["exit_type"] = "Normal";
                profObj["exited_cleanly"] = true;
                prefs["profile"] = profObj;

                if (!prefs.ContainsKey("browser")) prefs["browser"] = new JsonObject();
                var brObj = prefs["browser"] as JsonObject ?? new JsonObject();
                brObj["has_seen_welcome_page"] = true;
                brObj["check_default_browser"] = false;
                prefs["browser"] = brObj;

                File.WriteAllText(prefFile, prefs.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }

            // 4. Pre-configure Local State
            string localStateFile = Path.Combine(profilePath, "Local State");
            if (!File.Exists(localStateFile))
            {
                try
                {
                    var ls = new JsonObject
                    {
                        ["browser"] = new JsonObject
                        {
                            ["has_seen_welcome_page"] = true,
                            ["enabled_labs_experiments"] = new JsonArray()
                        }
                    };
                    File.WriteAllText(localStateFile, ls.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
            }

            return workerExt;
        }

        public static bool DeleteBotProfiles()
        {
            try
            {
                string baseDir = GetBotProfilesBaseDir();
                if (Directory.Exists(baseDir))
                {
                    Directory.Delete(baseDir, true);
                    Directory.CreateDirectory(baseDir);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting bot profiles: {ex.Message}");
                return false;
            }
        }

        public static bool DeleteSingleBotProfile(int wid)
        {
            try
            {
                string baseDir = GetBotProfilesBaseDir();
                string profDir = Path.Combine(baseDir, $"worker_{wid}");
                if (Directory.Exists(profDir))
                {
                    Directory.Delete(profDir, true);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting worker_{wid}: {ex.Message}");
                return false;
            }
        }

        public static void LaunchProfile(int wid, string url = "")
        {
            string chromeExe = GetChromeExePath();
            string extDir = GetExtensionPath();
            string baseDir = GetBotProfilesBaseDir();
            string profilePath = Path.Combine(baseDir, $"worker_{wid}");

            // Ensure real session and extension files exist
            string workerExt = InitializeWorkerSession(wid, baseDir, extDir);

            string targetUrl = string.IsNullOrEmpty(url)
                ? $"https://passport.thehof.gg/hall-of-fame-web/login#wid={wid}"
                : url;

            int x = ((wid - 1) % 4) * 460 + 10;
            int y = (((wid - 1) / 4) % 2) * 50 + 10;

            // Direct Chrome launch with unpacked extension forced active
            var psi = new ProcessStartInfo
            {
                FileName = chromeExe,
                UseShellExecute = false,
                Arguments = $"--user-data-dir=\"{profilePath}\" --profile-directory=\"Default\" --no-profile-picker --load-extension=\"{workerExt}\" --disable-extensions-except=\"{workerExt}\" --window-position={x},{y} --window-size=800,720 --no-first-run --no-default-browser-check \"{targetUrl}\""
            };

            try
            {
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to launch Chrome for worker {wid}: {ex.Message}");
            }
        }
    }
}

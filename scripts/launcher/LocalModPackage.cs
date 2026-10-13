using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ScamWYF.Launcher
{
    // This local extension never changes the original launcher's catalogue or executable.
    internal static class LocalModPackage
    {
        private sealed class PackageDefinition
        {
            internal readonly string Marker, FileName, DisplayName, Tag, Summary, InstallScript, BackupFolder;
            internal readonly ModFile[] Files;
            internal readonly string[] PluginAssets;
            internal readonly string[] WorkerFiles;
            internal readonly string WorkerManifestSha256;

            internal PackageDefinition(string marker, string fileName, string displayName, string tag,
                string summary, string installScript, string backupFolder, ModFile[] files, string[] pluginAssets = null,
                string[] workerFiles = null, string workerManifestSha256 = null)
            {
                Marker = marker; FileName = fileName; DisplayName = displayName; Tag = tag;
                Summary = summary; InstallScript = installScript; BackupFolder = backupFolder; Files = files;
                PluginAssets = pluginAssets ?? new string[0];
                WorkerFiles = workerFiles ?? new string[0];
                WorkerManifestSha256 = workerManifestSha256;
            }
        }

        private static readonly PackageDefinition[] Packages = {
            new PackageDefinition("local-package:elevenlabs-agents", "ElevenLabs-Agents-1.0.1.zip",
                "ElevenLabs Agents", "v1.0.1 (local)",
                "Local package: ElevenLabs Agents handles conversation, model, and voice. Requires the local bridge and an Agent ID.",
                "Install-Mod.ps1", "BepInEx\\elevenlabs-agents-backups", new[] {
                    new ModFile("ScamWYF.ElevenLabsAgents.dll", ModFolder.Plugins, true),
                    new ModFile("ScamWYF.AiBackend.dll", ModFolder.Plugins, true),
                    new ModFile("ScamWYF.Modding.Core.dll", ModFolder.Core, false)
                }),
            new PackageDefinition("local-package:requested-payout", "Requested-Payout-1.2.0.zip",
                "Requested Payout", "v1.2.0 (local)",
                "After success: credit cards pay the original reward plus the clearly offered amount; gift cards pay the agreed price.",
                "Install-PayoutMod.ps1", "BepInEx\\requested-payout-backups", new[] {
                    new ModFile("ScamWYF.RequestedPayout.dll", ModFolder.Plugins, true),
                    new ModFile("ScamWYF.Modding.Core.dll", ModFolder.Core, false)
                }),
            new PackageDefinition("local-package:tiktok-pocket", "TikTok-Pocket-1.0.1.zip",
                "TikTok Pocket", "v1.0.1 (local)",
                "Adds a purchasable TikTok app to Scamazon with its own icon and a portrait browser window.",
                "Install-TikTokMod.ps1", "BepInEx\\tiktok-pocket-backups", new[] {
                    new ModFile("ScamWYF.TikTokPocket.dll", ModFolder.Plugins, true),
                    new ModFile("ScamWYF.Modding.Core.dll", ModFolder.Core, false)
                }, new[] { "TikTokPocket/tiktok-pocket.png" }, new[] {
                    "ScamWYF.TikTokBrowser.exe",
                    "Licenses/CsWinRT-LICENSE.txt",
                    "Licenses/DEPENDENCIES.md",
                    "Licenses/DotNet-LICENSE.txt",
                    "Licenses/DotNet-ThirdPartyNotices.txt",
                    "Licenses/DotNet-WindowsDesktop-LICENSE.txt",
                    "Licenses/Microsoft-WebView2-LICENSE.txt",
                    "Licenses/Microsoft-WebView2-NOTICE.txt",
                    "Licenses/SharpGen-LICENSE.txt",
                    "Licenses/System-IO-Pipelines-LICENSE.txt",
                    "Licenses/System-IO-Pipelines-NOTICES.txt",
                    "Licenses/System-Text-Encodings-Web-LICENSE.txt",
                    "Licenses/System-Text-Encodings-Web-NOTICES.txt",
                    "Licenses/System-Text-Json-LICENSE.txt",
                    "Licenses/System-Text-Json-NOTICES.txt",
                    "Licenses/Vortice-Mathematics-LICENSE.txt",
                    "Licenses/Vortice-Windows-LICENSE.txt",
                    "Licenses/Windows-SDK-LICENSE.txt"
                }, "__TIKTOK_WORKER_MANIFEST_SHA256__")
        };

        internal static bool IsLocal(ReleaseInfo release)
        {
            return release != null && IsLocalAsset(release.Package);
        }

        internal static bool IsLocalAsset(ReleaseAsset asset)
        {
            if (asset == null) return false;
            foreach (var definition in Packages) if (definition.Marker == asset.DownloadUrl) return true;
            return false;
        }

        private static PackageDefinition Definition(ReleaseAsset asset)
        {
            if (asset != null)
                foreach (var definition in Packages) if (definition.Marker == asset.DownloadUrl) return definition;
            throw new IOException("Unknown local package.");
        }

        private static string PackagePath(PackageDefinition definition)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LocalPackages", definition.FileName);
        }

        internal static ReleaseInfo[] Available(ICollection<string> problems)
        {
            var found = new List<ReleaseInfo>();
            foreach (var definition in Packages)
            {
                try
                {
                    var package = PackagePath(definition);
                    if (!File.Exists(package)) throw new IOException("The bundled package is missing.");
                    var source = new ModSource("(local " + definition.DisplayName + ")", definition.DisplayName,
                        definition.Summary, definition.Files);
                    found.Add(new ReleaseInfo(source, definition.Tag, null,
                        new ReleaseAsset(definition.FileName, new FileInfo(package).Length, definition.Marker, Sha256(package))));
                }
                catch (Exception ex) { if (problems != null) problems.Add(definition.DisplayName + ": " + ex.Message); }
            }
            return found.ToArray();
        }

        internal static string BackupDescription(ReleaseInfo release)
        {
            return "Existing files will be backed up in " + Definition(release.Package).BackupFolder + ".";
        }

        internal static void DescribeAssets(InstallPlan plan, StringBuilder text)
        {
            if (!IsLocal(plan.Release)) return;
            var definition = Definition(plan.Release.Package);
            foreach (var relative in definition.PluginAssets)
            {
                text.AppendLine("  " + relative);
                text.AppendLine("    -> BepInEx\\plugins\\" + relative.Replace('/', '\\'));
            }
            if (definition.WorkerFiles.Length != 0)
            {
                text.AppendLine("Includes a bundled Microsoft Edge WebView2 helper for the in-game browser.");
                text.AppendLine("  ScamWYF.TikTokBrowser.exe, worker-files.json and " + (definition.WorkerFiles.Length - 1) + " license files");
                text.AppendLine("    -> BepInEx\\plugins\\TikTokPocket\\BrowserWorker");
            }
        }

        private static string Sha256(string path)
        {
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static ModInstaller.Fetched Fetch(ReleaseAsset asset, string token, Action<string> status)
        {
            var definition = Definition(asset);
            var folder = ModInstaller.WorkingFolder(token);
            try
            {
                if (status != null) status("Reading bundled " + definition.DisplayName + " package...");
                var copied = Path.Combine(folder, definition.FileName);
                File.Copy(PackagePath(definition), copied, false);
                var actual = Sha256(copied);
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The bundled package changed since it was listed. Check for updates again.");
                ValidateArchiveEntries(definition, copied);
                var unpacked = Path.Combine(folder, "unpacked");
                Directory.CreateDirectory(unpacked);
                ModInstaller.Unpack(copied, unpacked);
                return new ModInstaller.Fetched { WorkingFolder = folder, UnpackedFolder = unpacked, Sha256 = actual };
            }
            catch
            {
                ModInstaller.Discard(folder);
                throw;
            }
        }

        internal static string[] Install(InstallPlan plan)
        {
            string running;
            if (ModManager.GameRunning(out running)) throw new IOException("Close the game before installing this mod.");
            var definition = Definition(plan.Release.Package);
            var script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "scripts", definition.InstallScript));
            if (!File.Exists(script)) throw new IOException("The package's scripts/" + definition.InstallScript + " is missing.");
            var payload = Path.Combine(Path.GetDirectoryName(plan.Steps[0].SourcePath), "install-payload");
            var written = new List<string>();
            foreach (var step in plan.Steps)
            {
                var folder = step.File.Folder == ModFolder.Core ? "core" : "plugins";
                var destination = Path.Combine(payload, "BepInEx", folder, step.File.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(step.SourcePath, destination, true);
                written.Add(step.File.FileName);
            }
            CopyPluginAssets(definition, plan, payload, written);
            var game = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(plan.Steps[0].DestinationPath)));
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell,
                "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) +
                " -GameDirectory " + Quote(game) + " -PackageDirectory " + Quote(payload))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var errors = new StringBuilder();
            using (var process = new Process { StartInfo = start })
            {
                process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (errors) errors.AppendLine(args.Data); };
                process.Start();
                process.BeginErrorReadLine();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new IOException("Local installation failed. " + errors.ToString().Trim());
            }
            return written.ToArray();
        }

        private static void CopyPluginAssets(PackageDefinition definition, InstallPlan plan, string payload, List<string> written)
        {
            if (definition.PluginAssets.Length == 0 && definition.WorkerFiles.Length == 0) return;
            var unpacked = Path.GetDirectoryName(plan.Steps[0].SourcePath);
            var zip = Path.Combine(Path.GetDirectoryName(unpacked), definition.FileName);
            if (!File.Exists(zip) || !string.Equals(Sha256(zip), plan.Release.Package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The bundled package changed after it was fetched. Nothing was installed.");
            ValidateArchiveEntries(definition, zip);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var assets = AssetPaths(definition);
                var manifest = ReadWorkerManifest(definition, archive, unpacked);
                foreach (var relative in assets)
                {
                    var source = ChildPath(unpacked, relative);
                    var actual = VerifyAsset(archive, unpacked, relative);
                    WorkerFile expected;
                    if (manifest.TryGetValue(relative, out expected) &&
                        (new FileInfo(source).Length != expected.Size || !string.Equals(actual, expected.Sha256, StringComparison.Ordinal)))
                        throw new IOException("The browser helper file does not match its manifest: " + relative + ".");
                }
                foreach (var relative in assets)
                {
                    var source = ChildPath(unpacked, relative);
                    var destination = ChildPath(Path.Combine(payload, "BepInEx", "plugins"), relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(source, destination, true);
                    written.Add(relative);
                }
            }
        }

        private const string WorkerPrefix = "TikTokPocket/BrowserWorker/";
        private const string WorkerManifest = WorkerPrefix + "worker-files.json";
        private sealed class WorkerFile { internal long Size; internal string Sha256; }

        private static void ValidateArchiveEntries(PackageDefinition definition, string zip)
        {
            if (definition.WorkerFiles.Length == 0) return;
            var allowed = new HashSet<string>(AssetPaths(definition), StringComparer.Ordinal);
            foreach (var file in definition.Files) allowed.Add(file.FileName);
            var found = new HashSet<string>(StringComparer.Ordinal);
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries)
                    if (!allowed.Contains(entry.FullName) || !found.Add(entry.FullName))
                        throw new IOException("The bundled browser package contains an unexpected file: " + entry.FullName + ".");
            }
            if (found.Count != allowed.Count) throw new IOException("The bundled browser package is missing a required file.");
        }

        private static List<string> AssetPaths(PackageDefinition definition)
        {
            var paths = new List<string>(definition.PluginAssets);
            if (definition.WorkerFiles.Length != 0)
            {
                paths.Add(WorkerManifest);
                foreach (var file in definition.WorkerFiles) paths.Add(WorkerPrefix + file);
            }
            return paths;
        }

        private static string VerifyAsset(ZipArchive archive, string unpacked, string relative)
        {
            var source = ChildPath(unpacked, relative);
            var entry = archive.GetEntry(relative);
            if (entry == null || !File.Exists(source)) throw new IOException("The bundled package is missing " + relative + ".");
            string expected;
            using (var hash = SHA256.Create())
            using (var stream = entry.Open())
                expected = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            var actual = Sha256(source);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new IOException("The unpacked asset does not match the verified package: " + relative + ".");
            return actual;
        }

        private static Dictionary<string, WorkerFile> ReadWorkerManifest(PackageDefinition definition, ZipArchive archive, string unpacked)
        {
            var found = new Dictionary<string, WorkerFile>(StringComparer.Ordinal);
            if (definition.WorkerFiles.Length == 0) return found;
            var manifestHash = VerifyAsset(archive, unpacked, WorkerManifest);
            if (!string.Equals(manifestHash, definition.WorkerManifestSha256, StringComparison.Ordinal))
                throw new IOException("The browser helper manifest differs from the approved package.");
            var path = ChildPath(unpacked, WorkerManifest);
            if (new FileInfo(path).Length > 131072) throw new IOException("The browser helper manifest is too large.");
            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer { MaxJsonLength = 131072 }.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch (ArgumentException ex) { throw new IOException("The browser helper manifest could not be read.", ex); }
            object version, appVersion, executable, rawFiles;
            if (root == null || root.Count != 4 || !root.TryGetValue("schemaVersion", out version) || !(version is int) || (int)version != 1 ||
                !root.TryGetValue("appVersion", out appVersion) || !string.Equals(appVersion as string, "1.0.1", StringComparison.Ordinal) ||
                !root.TryGetValue("executable", out executable) || !string.Equals(executable as string, "ScamWYF.TikTokBrowser.exe", StringComparison.Ordinal) ||
                !root.TryGetValue("files", out rawFiles) || !(rawFiles is object[]))
                throw new IOException("Invalid browser helper manifest format or version.");
            var allowed = new HashSet<string>(definition.WorkerFiles, StringComparer.Ordinal);
            long totalSize = 0;
            foreach (var raw in (object[])rawFiles)
            {
                var file = raw as Dictionary<string, object>;
                object rawPath, rawSize, rawSha;
                if (file == null || file.Count != 3 || !file.TryGetValue("path", out rawPath) || !file.TryGetValue("size", out rawSize) ||
                    !file.TryGetValue("sha256", out rawSha) || !(rawPath is string) || !(rawSize is int || rawSize is long) ||
                    !allowed.Contains((string)rawPath) || found.ContainsKey(WorkerPrefix + (string)rawPath))
                    throw new IOException("The browser helper manifest contains an unexpected file.");
                var sha = rawSha as string;
                var size = Convert.ToInt64(rawSize);
                if (size <= 0 || size > 268435456 || sha == null || sha.Length != 64) throw new IOException("Invalid browser helper file size or digest.");
                totalSize += size;
                if (totalSize > 268435456) throw new IOException("The browser helper payload is too large.");
                foreach (var c in sha) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    throw new IOException("Invalid browser helper digest.");
                found.Add(WorkerPrefix + (string)rawPath, new WorkerFile { Size = size, Sha256 = sha });
            }
            if (found.Count != allowed.Count) throw new IOException("The browser helper manifest is missing a required file.");
            return found;
        }

        private static string ChildPath(string root, string relative)
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(prefix, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid bundled asset path.");
            return path;
        }

        private static string Quote(string value)
        {
            // Windows paths cannot contain a double quote. Reject it rather than interpreting an argument.
            if (value == null || value.IndexOf('"') >= 0) throw new IOException("Invalid installation path.");
            return "\"" + value.TrimEnd('\\') + "\"";
        }
    }
}

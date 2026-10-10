using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ScamWYF.Launcher
{
    // This local extension never changes the original launcher's catalogue or executable.
    internal static class LocalModPackage
    {
        private sealed class PackageDefinition
        {
            internal readonly string Marker, FileName, DisplayName, Tag, Summary, InstallScript, BackupFolder;
            internal readonly ModFile[] Files;

            internal PackageDefinition(string marker, string fileName, string displayName, string tag,
                string summary, string installScript, string backupFolder, ModFile[] files)
            {
                Marker = marker; FileName = fileName; DisplayName = displayName; Tag = tag;
                Summary = summary; InstallScript = installScript; BackupFolder = backupFolder; Files = files;
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
            new PackageDefinition("local-package:requested-payout", "Wunschsumme-1.1.2.zip",
                "Wunschsumme", "v1.1.2 (local)",
                "After success: credit cards pay the original reward plus the clearly offered amount; gift cards pay the agreed price.",
                "Install-PayoutMod.ps1", "BepInEx\\requested-payout-backups", new[] {
                    new ModFile("ScamWYF.RequestedPayout.dll", ModFolder.Plugins, true),
                    new ModFile("ScamWYF.Modding.Core.dll", ModFolder.Core, false)
                })
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

        private static string Quote(string value)
        {
            // Windows paths cannot contain a double quote. Reject it rather than interpreting an argument.
            if (value == null || value.IndexOf('"') >= 0) throw new IOException("Invalid installation path.");
            return "\"" + value.TrimEnd('\\') + "\"";
        }
    }
}

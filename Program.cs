using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using Quaver.Steam.Deploy.Configuration;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Quaver.Steam.Deploy
{
    internal static class Program
    {
        private static readonly string CurrentDirectory = AppContext.BaseDirectory;

        private static string CompiledBuildPath => Path.Combine(CurrentDirectory, "build");

        private static string SourceCodePath => Path.Combine(CurrentDirectory, "quaver");

        private static string ClientProjectPath => Path.Combine(SourceCodePath, "Quaver", "Quaver.csproj");

        private static string SteamCmdPath => Path.Combine(CurrentDirectory, "steamcmd");

        private static string Version { get; set; }

        private static string RepoBranch { get; set; }

        private static Config Configuration { get; set; }
        
        private static List<GameBuild> GameBuilds { get; set; } = new();

        private static string[] Platforms { get; } =
        {
            "win-x64",
            "linux-x64",
            "osx-x64",
            "osx-arm64",
        };

        /// <summary>
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            Directory.SetCurrentDirectory(CurrentDirectory);
            Configuration = Config.Deserialize(Path.Combine(CurrentDirectory, "config.json"));
            SetupSteamCMD();
            //CleanUp();
            GameVersion();
            Branch();
            //CloneProject();
            BuildProject();
            //ObfuscateClient();
            MacAppPackager.Package(CurrentDirectory, CompiledBuildPath, SourceCodePath, Version, Configuration);
            //HashProject();
            //SubmitHashes();
            Deploy();

            // Avoid closing console
            Console.WriteLine("Press any key to close");
            Console.ReadLine();
        }

        private static void CleanUp()
        {
            // Delete source code
            DeleteAndCreate(SourceCodePath);
            // Delete builds
            DeleteAndCreate(CompiledBuildPath);
            // Delete app_build.vdf
            var appBuildPath = Path.Combine(CurrentDirectory, "Scripts", "app_build.vdf");
            if (File.Exists(appBuildPath))
                File.Delete(appBuildPath);
        }

        private static void DeleteAndCreate(string path)
        {
            if (Directory.Exists(path))
            {
                // This resolves not allowing us to delete git
                var directory = new DirectoryInfo(path) { Attributes = FileAttributes.Normal };

                foreach (var info in directory.GetFileSystemInfos("*", SearchOption.AllDirectories))
                {
                    info.Attributes = FileAttributes.Normal;
                }
                
                Directory.Delete(path, true);
            }

            if (path != null) Directory.CreateDirectory(path);
        }

        private static void GameVersion()
        {
            Console.Write("Enter a version number for the client (e.g. 1.5.1.1): ");

            while (string.IsNullOrEmpty(Version))
                Version = Console.ReadLine();
        }

        private static void Branch()
        {
            Console.Write("Enter which branch we are building: ");

            while (string.IsNullOrEmpty(RepoBranch))
                RepoBranch = Console.ReadLine();
        }

        private static void CloneProject()
        {
            var scriptContent =
                $"git clone --recurse-submodules -b {RepoBranch} --single-branch {Configuration.Repository} {SourceCodePath}";
            
            RunCommandInNewTerminal(scriptContent);
            
            Console.WriteLine("Press enter when it finishes to continue!");
            Console.ReadLine();
        }

        private static void BuildProject()
        {
            // Update project version
            // Temporary fix until we ship Monogame dll instead of submodule
            UpdateProjectVersion(ClientProjectPath, Version);

            foreach (var platform in Platforms)
            {
                Console.WriteLine($"Starting compiling {platform}!");
                var dir = Path.Combine(CompiledBuildPath, $"content-{platform}");

                var succeeded = RunCommand("dotnet", new[]
                {
                    "publish",
                    ClientProjectPath,
                    "-f",
                    Configuration.NetFramework,
                    "-r",
                    platform,
                    "-c",
                    Configuration.NetConfiguration,
                    "-o",
                    dir,
                    "--self-contained"
                }, true);

                if (!succeeded)
                    throw new InvalidOperationException($"Failed to compile {platform}. See the dotnet publish output above.");
            }

            Console.WriteLine("Successfully finished compiling for all platforms!");
        }

        private static void ObfuscateClient()
        {
            if (!Configuration.RunReactor)
            {                
                Console.WriteLine("Obfuscating client is disabled in the config file. Skipping...");
                return;
            }
            
            Console.WriteLine("Starting obfuscating client");
            // Run .NET Reactor for win-x64
            var contentPath = Path.Combine(CompiledBuildPath, "content-win-x64");

            var commandline =
                $"-licensed -file {Path.Combine(contentPath, "Quaver.dll")} -files {Path.Combine(contentPath, "Quaver.Server.Client.dll")} -antitamp 1 -anti_debug 1 -hide_calls 1 -hide_calls_internals 1 -control_flow 1 -flow_level 9 -resourceencryption 1 -antistrong 1 -virtualization 1 -necrobit 1 -mapping_file 1";

            RunCommand(Configuration.NetReactor, commandline);

            var quaverServerClient = Path.Combine(contentPath, "Quaver.Server.Client_Secure", "Quaver.Server.Client.dll");

            foreach (var platform in Platforms)
            {
                var path = Path.Combine(CompiledBuildPath, $"content-{platform}");
                File.Copy(quaverServerClient, Path.Combine(path, "Quaver.Server.Client.dll"), true);
            }
            
            Console.WriteLine("Finished obfuscating");
        }

        private static void HashProject()
        {
            foreach (var platform in Platforms)
            {
                var gameBuild = new GameBuild
                {
                    Name = Version,
                    QuaverSharedMd5 = GetHash(Path.Combine(CompiledBuildPath, $"content-{platform}", "Quaver.Shared.dll")),
                    QuaverApiMd5 = GetHash(Path.Combine(CompiledBuildPath, $"content-{platform}", "Quaver.API.dll")),
                    QuaverServerClientMd5 = GetHash(Path.Combine(CompiledBuildPath, $"content-{platform}", "Quaver.Server.Client.dll"))
                };
                GameBuilds.Add(gameBuild);
            }
        }

        private static string GetHash(string path)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(path);
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static void SubmitHashes()
        {
            Console.WriteLine("Submitting hashes");
            
            if(!Configuration.DeployToSteam)
            {
                Console.WriteLine("Deploying to Steam is disabled in the config file. Skipping...");
                return;
            }
            
            foreach (var gameBuild in GameBuilds)
            {
                Console.WriteLine(gameBuild);
                gameBuild.SendBuild(Configuration.QuaverApijwt);
            }
        }

        private static void Deploy()
        {
            if(!Configuration.DeployToSteam)
            {
                Console.WriteLine("Deploying to Steam is disabled in the config file. Skipping...");
                return;
            }
            
            // Create app_build.vdf
            var scriptsPath = Path.Combine(CurrentDirectory, "Scripts");
            var appBuildPath = Path.Combine(scriptsPath, "app_build.vdf");
            var appBuildTemplate = File.ReadAllText(Path.Combine(scriptsPath, "app_build.template.vdf"));
            var appBuild = appBuildTemplate.Replace("{build_desc}", $"{Version}");
            File.Create(appBuildPath).Dispose();
            File.WriteAllText(appBuildPath, appBuild);
            
            Console.Write("Enter Steam Two Factor Authentication Code: ");
            var code = Console.ReadLine();
            
            // Delete the reactor folders
            string contentPath = Path.Combine(CompiledBuildPath, "content-win-x64");

            if (Directory.Exists(Path.Combine(contentPath, "Quaver_Secure")))
            {
                Directory.Delete(Path.Combine(contentPath, "Quaver_Secure"), true);
            }

            if (Directory.Exists(Path.Combine(contentPath, "Quaver.Server.Client_Secure")))
            {
                Directory.Delete(Path.Combine(contentPath, "Quaver.Server.Client_Secure"), true);
            }

            Console.WriteLine("Deploying to Steam...");
            
            // Deploy to Steam
            RunCommand(Path.Combine(SteamCmdPath, GetSteamCmdExecutableName()), new[]
            {
                "+login",
                Configuration.SteamUsername,
                Configuration.SteamPassword,
                code,
                "+run_app_build_http",
                appBuildPath,
                "+quit"
            }, true);

            Console.WriteLine("Finished deploying!");
        }

        private static string GetSteamCmdExecutableName()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "steamcmd.exe" : "steamcmd.sh";
        }

        private static (string Url, string ArchiveName, bool IsZip) GetSteamCmdPackage()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip", "steamcmd.zip", true);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return ("https://steamcdn-a.akamaihd.net/client/installer/steamcmd_osx.tar.gz", "steamcmd_osx.tar.gz", false);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return ("https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz", "steamcmd_linux.tar.gz", false);

            throw new PlatformNotSupportedException("SteamCMD is only supported on Windows, macOS, and Linux.");
        }

        private static bool RunCommand(string command, string args, bool showOutput = true)
        {
            var processStartInfo = new ProcessStartInfo(command, args)
            {
                WorkingDirectory = CurrentDirectory,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            return RunProcess(processStartInfo, showOutput);
        }

        private static bool RunCommand(string command, IEnumerable<string> args, bool showOutput = true)
        {
            var processStartInfo = new ProcessStartInfo(command)
            {
                WorkingDirectory = CurrentDirectory,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            foreach (var arg in args)
                processStartInfo.ArgumentList.Add(arg);

            return RunProcess(processStartInfo, showOutput);
        }

        private static bool RunProcess(ProcessStartInfo processStartInfo, bool showOutput)
        {
            var process = Process.Start(processStartInfo);

            if (process == null)
                return false;

            var output = "";

            output += process.StandardOutput.ReadToEnd();
            output += process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode == 0)
                return true;

            if (showOutput)
                Console.WriteLine(output);

            return false;
        }

        private static void RunCommandInNewTerminal(string command)
        {
            ProcessStartInfo processStartInfo;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                processStartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = CurrentDirectory,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                processStartInfo.ArgumentList.Add("/K");
                processStartInfo.ArgumentList.Add(command);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                processStartInfo = new ProcessStartInfo
                {
                    FileName = "osascript",
                    WorkingDirectory = CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                processStartInfo.ArgumentList.Add("-e");
                processStartInfo.ArgumentList.Add($"tell application \"Terminal\" to do script \"{EscapeAppleScriptString(GetUnixTerminalCommand(command))}\"");
            }
            else
            {
                processStartInfo = CreateLinuxTerminalStartInfo(command);
            }

            using var process = new Process();
            process.StartInfo = processStartInfo;
            process.Start();
        }

        private static ProcessStartInfo CreateLinuxTerminalStartInfo(string command)
        {
            string[] terminalCommands =
            {
                "x-terminal-emulator",
                "gnome-terminal",
                "konsole",
                "xfce4-terminal",
                "xterm"
            };

            foreach (var terminalCommand in terminalCommands)
            {
                if (!CommandExists(terminalCommand))
                    continue;

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = terminalCommand,
                    WorkingDirectory = CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                var terminalCommandLine = $"{GetUnixTerminalCommand(command)}; exec bash";

                switch (terminalCommand)
                {
                    case "gnome-terminal":
                        processStartInfo.ArgumentList.Add("--");
                        processStartInfo.ArgumentList.Add("bash");
                        processStartInfo.ArgumentList.Add("-lc");
                        processStartInfo.ArgumentList.Add(terminalCommandLine);
                        break;
                    case "konsole":
                    case "xfce4-terminal":
                        processStartInfo.ArgumentList.Add("-e");
                        processStartInfo.ArgumentList.Add("bash");
                        processStartInfo.ArgumentList.Add("-lc");
                        processStartInfo.ArgumentList.Add(terminalCommandLine);
                        break;
                    case "xterm":
                    case "x-terminal-emulator":
                        processStartInfo.ArgumentList.Add("-e");
                        processStartInfo.ArgumentList.Add("bash");
                        processStartInfo.ArgumentList.Add("-lc");
                        processStartInfo.ArgumentList.Add(terminalCommandLine);
                        break;
                }

                return processStartInfo;
            }

            throw new PlatformNotSupportedException("Could not find a supported terminal emulator. Install x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal, or xterm.");
        }

        private static bool CommandExists(string command)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "which",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            processStartInfo.ArgumentList.Add(command);

            using var process = Process.Start(processStartInfo);
            if (process == null)
                return false;

            process.WaitForExit();
            return process.ExitCode == 0;
        }

        private static string EscapeAppleScriptString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string GetUnixTerminalCommand(string command)
        {
            return $"cd {QuoteUnixShellArgument(CurrentDirectory)}; {command}";
        }

        private static string QuoteUnixShellArgument(string value)
        {
            return $"'{value.Replace("'", "'\\''")}'";
        }
        
        private static void UpdateProjectVersion(string projectFilePath, string newVersion)
        {
            try
            {
                // Load the project file
                XDocument projFile = XDocument.Load(projectFilePath);

                // Find the <Version> element and update its value
                XElement versionElement = projFile.Descendants()
                    .FirstOrDefault(d => d.Name.LocalName == "Version");

                if (versionElement != null)
                {
                    versionElement.Value = newVersion;
                }
                else
                {
                    // If <Version> element doesn't exist, create it under the <PropertyGroup> node
                    XElement propertyGroup = projFile.Descendants()
                        .FirstOrDefault(d => d.Name.LocalName == "PropertyGroup");

                    if (propertyGroup != null)
                    {
                        propertyGroup.Add(new XElement("Version", newVersion));
                    }
                    else
                    {
                        throw new InvalidOperationException("No <PropertyGroup> found in the .csproj file.");
                    }
                }

                // Save the modified project file
                projFile.Save(projectFilePath);
                Console.WriteLine($"Version updated successfully to {newVersion} in {projectFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating version: {ex.Message}");
            }
        }
        
        private static void SetupSteamCMD()
        {
            var steamCmdPackage = GetSteamCmdPackage();
            var steamCmdArchivePath = Path.Combine(CurrentDirectory, steamCmdPackage.ArchiveName);
            var steamCmdExecutable = Path.Combine(SteamCmdPath, GetSteamCmdExecutableName());

            if (!File.Exists(steamCmdExecutable))
            {
                Console.WriteLine("Downloading SteamCMD...");
                DownloadFile(steamCmdPackage.Url, steamCmdArchivePath);
                Directory.CreateDirectory(SteamCmdPath);

                if (steamCmdPackage.IsZip)
                    ZipFile.ExtractToDirectory(steamCmdArchivePath, SteamCmdPath, true);
                else
                    ExtractTarGzToDirectory(steamCmdArchivePath, SteamCmdPath);

                EnsureSteamCmdIsExecutable(steamCmdExecutable);

                Console.WriteLine("Installing SteamCMD...");
                RunCommand(steamCmdExecutable, "+quit", false);
            }

            EnsureSteamCmdIsExecutable(steamCmdExecutable);

            if (File.Exists(steamCmdArchivePath))
            {
                File.Delete(steamCmdArchivePath);
            }
        }

        private static void EnsureSteamCmdIsExecutable(string steamCMDExecutable)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var mode = File.GetUnixFileMode(steamCMDExecutable);
            File.SetUnixFileMode(steamCMDExecutable, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        private static void ExtractTarGzToDirectory(string archivePath, string destinationDirectory)
        {
            using var archiveStream = File.OpenRead(archivePath);
            using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzipStream, destinationDirectory, true);
        }
        
        static void DownloadFile(string url, string filePath)
        {
            using HttpClient client = new HttpClient();
            using HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).Result;
            response.EnsureSuccessStatusCode();

            using Stream stream = response.Content.ReadAsStream();
            using FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(fileStream);
        }
    }
}

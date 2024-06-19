﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Quaver.Steam.Deploy.Configuration;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Quaver.Steam.Deploy
{
    internal static class Program
    {
        private static readonly string CurrentDirectory = Directory.GetCurrentDirectory();

        private static string CompiledBuildPath => CurrentDirectory + "\\build";

        private static string SourceCodePath => CurrentDirectory + "\\quaver";

        private static string SteamCMDPath => CurrentDirectory + "\\steamcmd";

        private static string Version { get; set; }

        private static string RepoBranch { get; set; }

        private static Config Configuration { get; set; }
        
        private static List<GameBuild> GameBuilds { get; set; } = new();

        private static string[] Platforms { get; } =
        {
            "win-x64",
            "linux-x64",
            "osx-x64"
        };

        private static string[] DllFiles { get; } =
        {
            "Quaver.Shared.dll",
            "Quaver.API.dll",
            "Quaver.Server.Common.dll",
            "Quaver.Server.Client.dll",
        };

        /// <summary>
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            Configuration = Config.Deserialize();
            // ToDo SteamCmd download
            CleanUp();
            GameVersion();
            Branch();
            CloneProject();
            BuildProject();
            EncryptClient();
            HashProject();
            SubmitHashes();
            Deploy();

            // Avoid closing console
            Console.WriteLine("Press any key to close");
            Console.ReadLine();
        }

        private static void CleanUp()
        {
            // Delete cloned project
            // await DeleteAndCreate(SourceCodePath);
            // Delete builds
            DeleteAndCreate(CompiledBuildPath);
        }

        private static void DeleteAndCreate(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);

            if (path != null) Directory.CreateDirectory(path);
        }

        private static void GameVersion()
        {
            Console.Write("Enter a version number for the client: ");

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

            Console.WriteLine("Please run this commant in new terminal!");
            Console.WriteLine(scriptContent);
            Console.WriteLine("Press enter when it finished to continue!");
            Console.ReadLine();
        }

        private static void BuildProject()
        {
            // Update project version
            // Temporary fix until we ship Monogame dll instead submodule
            UpdateProjectVersion($"{SourceCodePath}/Quaver/Quaver.csproj", Version);

            foreach (var platform in Platforms)
            {
                Console.WriteLine($"Starting compiling {platform}!");
                var dir = $"{CompiledBuildPath}/content-{platform}";

                RunCommand("dotnet",
                    $"publish {SourceCodePath} -f {Configuration.NetFramework} -r {platform} -c Public -o {dir} --self-contained",
                    false);
            }

            Console.WriteLine("Successfully finished compiling for all platforms!");
        }

        private static void EncryptClient()
        {
            Console.WriteLine("Starting encrypting client");
            // Run .NET Reactor for win-x64
            var contentPath = $"{CompiledBuildPath}\\content-{Platforms[0]}";

            var commandline =
                $"-licensed -file {contentPath}\\Quaver.dll -files {contentPath}\\Quaver.Server.Client.dll;{contentPath}\\Quaver.Server.Common.dll -antitamp 1 -anti_debug 1 -hide_calls 1 -hide_calls_internals 1 -control_flow 1 -flow_level 9 -resourceencryption 1 -antistrong 1 -virtualization 1 -necrobit 1 -mapping_file 1";

            RunCommand(Configuration.NetReactor, commandline);

            var quaverServerClient = $"{contentPath}\\Quaver.Server.Client_Secure\\Quaver.Server.Client.dll";
            var quaverServerCommon = $"{contentPath}\\Quaver.Server.Common_Secure\\Quaver.Server.Common.dll";

            foreach (var platform in Platforms)
            {
                var path = $"{CompiledBuildPath}\\content-{platform}";
                File.Copy(quaverServerClient, $"{path}\\Quaver.Server.Client.dll", true);
                File.Copy(quaverServerCommon, $"{path}\\Quaver.Server.Common.dll", true);
            }
            
            // ToDo webhook upload mapping files to ac2 or to db
            Console.WriteLine("Finished encrypting");
        }

        private static void HashProject()
        {
            foreach (var platform in Platforms)
            {
                var gameBuild = new GameBuild
                {
                    Name = Version,
                    QuaverSharedMd5 = GetHash($"{CompiledBuildPath}/content-{platform}/Quaver.dll"),
                    QuaverApiMd5 = GetHash($"{CompiledBuildPath}/content-{platform}/Quaver.API.dll"),
                    QuaverServerCommonMd5 = GetHash($"{CompiledBuildPath}/content-{platform}/Quaver.Server.Common.dll"),
                    QuaverServerClientMd5 = GetHash($"{CompiledBuildPath}/content-{platform}/Quaver.Server.Client.dll")
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
            // ToDo
            Console.WriteLine("Submitting hashes to Quaver's database WIP");
            // Temporarily until API is ready
            foreach (var gameBuild in GameBuilds)
            {
                Console.WriteLine(gameBuild);
            }
        }

        private static void Deploy()
        {
            // ToDo
            // Delete the reactor folders
            string contentPath = $"{CompiledBuildPath}\\content-{Platforms[0]}";
            Directory.Delete($"{contentPath}/Quaver_Secure", true);
            Directory.Delete($"{contentPath}/Quaver.Server.Client_Secure", true);
            Directory.Delete($"{contentPath}/Quaver.Server.Common_Secure", true);
            
            Console.WriteLine("Deploying to Steam... (not yet lol)");
        }

        private static bool RunCommand(string command, string args, bool showOutput = true)
        {
            var psi = new ProcessStartInfo(command, args)
            {
                WorkingDirectory = Environment.CurrentDirectory,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var p = Process.Start(psi);

            if (p == null)
                return false;

            var output = "";

            output += p.StandardOutput.ReadToEnd();
            output += p.StandardError.ReadToEnd();

            p.WaitForExit();

            if (p.ExitCode == 0)
                return true;

            if (showOutput)
                Console.WriteLine(output);

            return false;
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

        // private static async Task DetectSteamCMD()
        // {
        //     var steamCMDUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
        //     var steamCMDPath = "./steamcmd";
        //     var steamCMDName = "steamcmd.zip";
        //
        //     if (!Directory.Exists(steamCMDPath))
        //     {
        //         await DownloadFileAsync(steamCMDUrl, steamCMDName);
        //         ZipFile.ExtractToDirectory($"./{steamCMDName}", steamCMDPath);
        //     }
        //
        //     if (File.Exists($"./{steamCMDName}"))
        //     {
        //         File.Delete($"./{steamCMDName}");
        //     }
        // }
        //
        // static async Task DownloadFileAsync(string url, string fileName)
        // {
        //     using (HttpClient client = new HttpClient())
        //     {
        //         HttpResponseMessage response = await client.GetAsync(url);
        //         response.EnsureSuccessStatusCode();
        //
        //         using (FileStream fs = new FileStream($"./{fileName}", FileMode.Create, FileAccess.Write, FileShare.None))
        //         {
        //             await response.Content.CopyToAsync(fs);
        //         }
        //     }
        // }
    }
}
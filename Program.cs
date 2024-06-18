using System;
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

        private static string[] Platforms { get; } =
        {
            "win-x64",
            "linux-x64",
            "osx-x64"
        };

        private static string[] DllFiles { get; } =
        {
            "Quaver.API.dll",
            "Quaver.Server.Client.dll",
            "Quaver.Server.Common.dll",
            "Quaver.Shared.dll",
        };

        /// <summary>
        /// </summary>
        /// <param name="args"></param>
        static async Task Main(string[] args)
        {
            Configuration = Config.Deserialize();
            // ToDo SteamCmd download
            await CleanUp();
            await GameVersion();
            await Branch();
            await CloneProject();
            await BuildProject();
            await EncryptClient();
            await HashProject();
            await SubmitHashes();
            await Deploy();

            // Avoid closing console
            Console.WriteLine("Press any key to close");
            Console.ReadLine();
        }

        private static async Task CleanUp()
        {
            // Delete cloned project
            // await DeleteAndCreate(SourceCodePath);
            // Delete builds
            await DeleteAndCreate(CompiledBuildPath);
        }

        private static async Task DeleteAndCreate(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);

            if (path != null) Directory.CreateDirectory(path);
        }

        private static async Task GameVersion()
        {
            Console.Write("Enter a version number for the client: ");

            while (string.IsNullOrEmpty(Version))
                Version = Console.ReadLine();
        }

        private static async Task Branch()
        {
            Console.Write("Enter which branch we are building: ");

            while (string.IsNullOrEmpty(RepoBranch))
                RepoBranch = Console.ReadLine();
        }

        private static async Task CloneProject()
        {
            var scriptContent =
                $"git clone --recurse-submodules -b {RepoBranch} --single-branch {Configuration.Repository} {SourceCodePath}";

            Console.WriteLine("Please clone the project");
            Console.WriteLine(scriptContent);
            Console.WriteLine("Press enter when it finished to continue!");
            Console.ReadLine();
        }

        private static async Task BuildProject()
        {
            // Update project version
            // Temporary fix until we ship Monogame dll instead submodule
            UpdateProjectVersion($"{SourceCodePath}/Quaver/Quaver.csproj", Version);

            foreach (var platform in Platforms)
            {
                var version = $"'{Version}' for {platform}";
                var dir = $"{CompiledBuildPath}/content-{platform}";

                RunCommand("dotnet",
                    $"publish {SourceCodePath} -f {Configuration.NetFramework} -r {platform} -c Public -o {dir} --self-contained",
                    false);

                Console.WriteLine($"Finished compiling {version}!");
            }

            Console.WriteLine("Successfully finished compiling all Quaver versions!");
        }

        private static async Task EncryptClient()
        {
            Console.WriteLine("Starting encrypting client");
            // ToDo Run Reactor & move encrypted dlls to all platforms
            // Run .NET Reactor for win-x64
            string contentPath = $"{CompiledBuildPath}\\content-{Platforms[0]}";

            string commandline =
                $"-licensed -file {contentPath}\\Quaver.dll -files {contentPath}\\Quaver.Server.Client.dll;{contentPath}\\Quaver.Server.Common.dll -antitamp 1 -anti_debug 1 -hide_calls 1 -hide_calls_internals 1 -control_flow 1 -flow_level 9 -resourceencryption 1 -antistrong 1 -virtualization 1 -necrobit 1 -mapping_file 1";

            RunCommand(Configuration.NetReactor, commandline);

            string quaverServerClient = $"{contentPath}\\Quaver.Server.Client_Secure\\Quaver.Server.Client.dll";
            string quaverServerCommon = $"{contentPath}\\Quaver.Server.Common_Secure\\Quaver.Server.Common.dll";

            foreach (var platform in Platforms)
            {
                string path = $"{CompiledBuildPath}\\content-{platform}";
                File.Copy(quaverServerClient, $"{path}\\Quaver.Server.Client.dll", true);
                File.Copy(quaverServerCommon, $"{path}\\Quaver.Server.Common.dll", true);
            }
            
            // ToDo webhook upload mapping files to ac2 or to db
            Console.WriteLine("Finished encrypting");
        }

        private static async Task HashProject()
        {
            foreach (var platform in Platforms)
            {
                Console.WriteLine($"Platform: {Version} {platform}");
                
                var dir = $"{CompiledBuildPath}/content-{platform}";
            
                var hashes = new List<string>();
                
                foreach (string dllFile in DllFiles)
                {
                    using (var md5 = MD5.Create())
                    {
                        using (var stream = File.OpenRead($"{dir}/{dllFile}"))
                        {
                            byte[] hash = md5.ComputeHash(stream);
                            string hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                            Console.WriteLine($"{Path.GetFileName(dllFile)}: {hashString}");
                            hashes.Add(hashString);
                        }
                    }
                }
            }
        }

        private static async Task SubmitHashes()
        {
            // ToDo
            Console.WriteLine("Submitting hashes to Quaver's database WIP");
        }

        private static async Task Deploy()
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

        // /// <summary>
        // ///     Print out hashes for each platform
        // /// </summary>
        // private static void MD5Hashes()
        // {
        //     foreach (var platform in Platforms)
        //     {
        //         Console.WriteLine($"Platform: {Version} {platform}");
        //         
        //         var dir = Configuration.DeployToSteam ? $"{Configuration.ContentBuilderDirectory}/content-{platform}" : $"{OutputDir}/{platform}";
        //
        //         var hashes = new List<string>();
        //         
        //         foreach (string dllFile in DllFiles)
        //         {
        //             using (var md5 = MD5.Create())
        //             {
        //                 using (var stream = File.OpenRead($"{dir}/{dllFile}"))
        //                 {
        //                     byte[] hash = md5.ComputeHash(stream);
        //                     string hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        //                     Console.WriteLine($"{Path.GetFileName(dllFile)}: {hashString}");
        //                     hashes.Add(hashString);
        //                 }
        //             }
        //         }
        //     }
        // }
        //
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
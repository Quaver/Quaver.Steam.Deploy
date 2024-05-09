using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Quaver.Steam.Deploy.Configuration;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;

namespace Quaver.Steam.Deploy
{
    internal static class Program
    {
        /// <summary>
        ///     The output directory of the build
        /// </summary>
        private static string OutputDir => Directory.GetCurrentDirectory() + "/build";

        /// <summary>
        ///     The version of the client that's being built
        /// </summary>
        private static string Version { get; set; }

        /// <summary>
        ///     Config setup for the deploy script to use
        /// </summary>
        private static Config Configuration { get; set; }

        /// <summary>
        ///     The platforms the game is being built to
        /// </summary>
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
            "Quaver.dll",
        };

        /// <summary>
        /// </summary>
        /// <param name="args"></param>
        internal static void Main(string[] args)
        {
            Configuration = Config.Deserialize();
            DeleteExistingBuild();
            
            Version = GetVersion();
            
            // Reset sql file
            File.WriteAllText($"{Directory.GetCurrentDirectory()}/sql.sql", $"");

            CompileClients();
            MD5Hashes();
            
            // Avoid closing console
            Console.WriteLine("Press any key to close");
            Console.ReadLine();
        }

        /// <summary>
        ///     Deletes the output folder if it exists to start fresh.
        /// </summary>
        private static void DeleteExistingBuild()
        {
            if (Directory.Exists(OutputDir))
                Directory.Delete(OutputDir, true);

            Directory.CreateDirectory(OutputDir);
        }
        
        /// <summary>
        ///     Asks the user for a version number of the client
        /// </summary>
        /// <returns></returns>
        private static string GetVersion()
        {
            Console.Write("Enter a version number for the client: ");

            string version = null;

            while (string.IsNullOrEmpty(version))
                version = Console.ReadLine();

            return version;
        }

        /// <summary>
        ///     Compiles the client for each platform
        /// </summary>
        private static void CompileClients()
        {
            foreach (var platform in Platforms)
            {
                var ver = $"'{Version}' for {platform}";

                Console.WriteLine($"Compiling Quaver...");

                var dir = Configuration.DeployToSteam ? $"{Configuration.ContentBuilderDirectory}/content-{platform}" : $"{OutputDir}/{platform}";
                
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
                
                Directory.CreateDirectory(dir);

                var cmd = $"publish {Configuration.QuaverProjectDirectory} -f net6.0 -r {platform} -c Public -o {dir} -p:DefineConstants="PUBLIC" --sc true";

                RunCommand("dotnet", cmd, false);

                Console.WriteLine($"Finished compiling Quaver!");

                if (!Configuration.ZipBuilds) 
                    continue;
                
                Console.WriteLine($"Archiving Quaver version {ver} to a zip file...");

                // For debug/offline builds on Steam. Needs a Spacewar file
                File.WriteAllText($"{dir}/steam_appid.txt", "480");
                
                using (var archive = ZipArchive.Create())
                {
                    archive.AddAllFromDirectory(dir);
                    archive.SaveTo($"{Directory.GetCurrentDirectory()}/quaver-{Version}-{platform}.zip", CompressionType.Deflate);
                }
                
                Console.WriteLine($"Finished archiving Quaver version {ver} to a zip file!");
            }
            
            Console.WriteLine("Successfully finished compiling all Quaver versions!");
        }
        
        /// <summary>
        ///     Runs a CLI command
        /// </summary>
        /// <param name="command"></param>
        /// <param name="args"></param>
        /// <returns></returns>
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

            Console.WriteLine(output);
            return false;
        }

        /// <summary>
        ///     Print out hashes for each platform
        /// </summary>
        private static void MD5Hashes()
        {
            foreach (var platform in Platforms)
            {
                Console.WriteLine($"Platform: {Version} {platform}");
                
                var dir = Configuration.DeployToSteam ? $"{Configuration.ContentBuilderDirectory}/content-{platform}" : $"{OutputDir}/{platform}";

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
                
                HashesToSql(Version, platform, hashes[0], hashes[1], hashes[3], hashes[4]);
            }
        }

        private static void HashesToSql(string version, string platform, string quaverAPI, string quaverServerClient, string quaverServerCommon, string quaverShared)
        {
            File.AppendAllText($"{Directory.GetCurrentDirectory()}/sql.sql",
                $"INSERT INTO `game_builds` " +
                $"(`version`, `quaver_dll`, `quaver_api_dll`, `quaver_server_client_dll`, `quaver_server_common_dll`, `quaver_shared_dll`, `allowed`, `timestamp`)\n" +
                $" VALUES " +
                $"('{version} {platform}', 'NONE', '{quaverAPI}', '{quaverServerClient}', '{quaverServerCommon}', '{quaverShared}', 1, {DateTimeOffset.UtcNow.ToUnixTimeSeconds()})\n");
        }
    }
}

using System.IO;
using Newtonsoft.Json;

namespace Quaver.Steam.Deploy.Configuration
{
    public class Config
    {
        /// <summary>
        ///     SSH URL of the repository
        /// </summary>
        public string Repository { get; set; } = "git@github.com:Quaver/Quaver.git";
        
        /// <summary>
        ///     .NET Framework
        /// </summary>
        public string NetFramework { get; set; } = "net6.0";
        
        /// <summary>
        ///     Path to .NET Reactor runnable
        /// </summary>
        public string NetReactor { get; set; } = "";
        
        /// <summary>
        ///     Quaver API Key
        /// </summary>
        public string QuaverAPIKey { get; set; } = "";
        
        /// <summary>
        ///     Whether or not the script will deploy the builds to Steam
        /// </summary>
        public bool DeployToSteam { get; set; }
        
        /// <summary>
        ///     The path of the config file.
        /// </summary>
        public static string Path => $"{Directory.GetCurrentDirectory()}/config.json";

        /// <summary>
        ///     Deserializes the config into an object.
        /// </summary>
        /// <returns></returns>
        public static Config Deserialize()
        {
            const string path = "./config.json";

            // If the file doesn't exist, then we'll want to create the file, then throw a FileNotFoundException
            if (!File.Exists(path))
            {
                var config = new Config();
                config.Save();

                throw new FileNotFoundException("config.json file was not found. A template has been created for you.");
            }

            Config parsedConfig;

            // Deserialize it if it already exists.
            using (var file = File.OpenText(path))
            {
                var serializer = new JsonSerializer();
                parsedConfig = (Config)serializer.Deserialize(file, typeof(Config));
            }

            // Do an initial save on the config.
            parsedConfig.Save();

            return parsedConfig;
        }
        
        /// <summary>
        ///     Saves the configuration
        /// </summary>
        private void Save()
        {
            using (var sw = new StreamWriter(Path))
            {
                var output = JsonConvert.SerializeObject(this, Formatting.Indented);
                sw.Write(output);
            }
        }
    }
}
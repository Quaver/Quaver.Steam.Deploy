using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Quaver.Steam.Deploy.Configuration;

namespace Quaver.Steam.Deploy;

internal static class MacAppPackager
{
    private const string AppName = "Quaver.app";

    private const string BundleIdentifier = "com.quavergame.Quaver";

    internal static void Package(string currentDirectory, string compiledBuildPath, string sourceCodePath, string version, Config configuration)
    {
        Console.WriteLine("Creating universal macOS build...");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("Universal macOS packaging requires lipo and must be run on macOS.");

        var macAppBuildPath = Path.Combine(compiledBuildPath, "content-osx");
        DeleteAndCreate(macAppBuildPath);

        var x64BuildPath = Path.Combine(compiledBuildPath, "content-osx-x64");
        var arm64BuildPath = Path.Combine(compiledBuildPath, "content-osx-arm64");

        CopyDirectory(arm64BuildPath, macAppBuildPath);
        CreateUniversalMachOBinaries(x64BuildPath, arm64BuildPath, macAppBuildPath, currentDirectory);

        var executablePath = Path.Combine(macAppBuildPath, "Quaver");
        var executableBinaryPath = Path.Combine(macAppBuildPath, "Quaver.bin");
        File.Move(executablePath, executableBinaryPath, true);
        File.WriteAllText(executablePath, CreateRootLauncherScript());

        var appPath = Path.Combine(macAppBuildPath, AppName);
        var contentsPath = Path.Combine(appPath, "Contents");
        var macOsPath = Path.Combine(contentsPath, "MacOS");
        var resourcesPath = Path.Combine(contentsPath, "Resources");

        Directory.CreateDirectory(macOsPath);
        Directory.CreateDirectory(resourcesPath);

        var iconFileName = CopyAppIcon(resourcesPath, currentDirectory, sourceCodePath, configuration);

        var launcherPath = Path.Combine(macOsPath, "Quaver");
        CreateAppLauncher(launcherPath, macOsPath, currentDirectory);
        RunCommand("chmod", new[] { "+x", launcherPath }, currentDirectory);
        RunCommand("chmod", new[] { "+x", executablePath }, currentDirectory);
        RunCommand("chmod", new[] { "+x", executableBinaryPath }, currentDirectory);

        File.WriteAllText(Path.Combine(contentsPath, "Info.plist"), CreateInfoPlist(version, iconFileName));

        Console.WriteLine($"Created universal macOS build at {macAppBuildPath}");
    }

    private static void CreateAppLauncher(string launcherPath, string buildDirectory, string currentDirectory)
    {
        var sourcePath = Path.Combine(buildDirectory, "QuaverLauncher.m");
        File.WriteAllText(sourcePath, CreateAppLauncherSource());

        RunCommand("xcrun", new[]
        {
            "clang",
            "-fobjc-arc",
            "-framework",
            "Cocoa",
            "-mmacosx-version-min=10.15",
            "-arch",
            "x86_64",
            "-arch",
            "arm64",
            sourcePath,
            "-o",
            launcherPath
        }, currentDirectory);

        File.Delete(sourcePath);
    }

    private static string CreateAppLauncherSource()
    {
        return """
               #import <Cocoa/Cocoa.h>

               @interface QuaverAppDelegate : NSObject <NSApplicationDelegate>
               @property(nonatomic) BOOL launchedGame;
               @end

               @implementation QuaverAppDelegate

               - (NSString *)installDirectory {
                   NSURL *bundleURL = [[NSBundle mainBundle] bundleURL];
                   return [[[bundleURL URLByDeletingLastPathComponent] path] stringByStandardizingPath];
               }

               - (void)launchQuaverWithArguments:(NSArray<NSString *> *)arguments {
                   NSString *installDirectory = [self installDirectory];
                   NSString *launcherPath = [installDirectory stringByAppendingPathComponent:@"Quaver"];

                   NSTask *task = [[NSTask alloc] init];
                   task.executableURL = [NSURL fileURLWithPath:launcherPath];
                   task.currentDirectoryURL = [NSURL fileURLWithPath:installDirectory isDirectory:YES];
                   task.arguments = arguments ?: @[];

                   NSMutableDictionary *environment = [[[NSProcessInfo processInfo] environment] mutableCopy];
                   environment[@"QUAVER_INSTALL_DIR"] = installDirectory;
                   task.environment = environment;

                   NSError *error = nil;
                   if (![task launchAndReturnError:&error]) {
                       NSLog(@"Failed to launch Quaver: %@", error);
                   }

                   self.launchedGame = YES;
               }

               - (void)applicationDidFinishLaunching:(NSNotification *)notification {
                   dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.2 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
                       if (!self.launchedGame) {
                           [self launchQuaverWithArguments:@[]];
                       }

                       [NSApp terminate:nil];
                   });
               }

               - (void)application:(NSApplication *)application openURLs:(NSArray<NSURL *> *)urls {
                   NSMutableArray<NSString *> *arguments = [NSMutableArray arrayWithCapacity:urls.count];

                   for (NSURL *url in urls) {
                       [arguments addObject:url.absoluteString];
                   }

                   [self launchQuaverWithArguments:arguments];
                   [NSApp terminate:nil];
               }

               - (BOOL)application:(NSApplication *)sender openFile:(NSString *)filename {
                   [self launchQuaverWithArguments:@[filename]];
                   [NSApp terminate:nil];
                   return YES;
               }

               @end

               int main(int argc, const char * argv[]) {
                   @autoreleasepool {
                       NSApplication *application = [NSApplication sharedApplication];
                       QuaverAppDelegate *delegate = [[QuaverAppDelegate alloc] init];
                       application.delegate = delegate;
                       [application run];
                   }

                   return 0;
               }
               """;
    }

    private static string CreateRootLauncherScript()
    {
        return """
               #!/bin/sh
               set -e

               INSTALL_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
               cd "$INSTALL_DIR"
               export QUAVER_INSTALL_DIR="$INSTALL_DIR"

               if [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = "1" ]; then
                   exec /usr/bin/arch -arm64 "$INSTALL_DIR/Quaver.bin" "$@"
               fi

               exec "$INSTALL_DIR/Quaver.bin" "$@"
               """;
    }

    private static string CreateInfoPlist(string version, string iconFileName)
    {
        var plist = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist",
                new XAttribute("version", "1.0"),
                new XElement("dict",
                    PlistKeyValue("CFBundleDevelopmentRegion", "en"),
                    PlistKeyValue("CFBundleDisplayName", "Quaver"),
                    PlistKeyValue("CFBundleExecutable", "Quaver"),
                    PlistKeyValue("CFBundleIdentifier", BundleIdentifier),
                    PlistKeyValue("CFBundleName", "Quaver"),
                    PlistKeyValue("CFBundlePackageType", "APPL"),
                    PlistKeyValue("CFBundleShortVersionString", version),
                    PlistKeyValue("CFBundleVersion", version),
                    PlistKeyValue("LSMinimumSystemVersion", "10.15"),
                    PlistKeyValue("NSHighResolutionCapable", true),
                    CreateUrlTypes(),
                    CreateDocumentTypes()
                )
            )
        );

        if (!string.IsNullOrEmpty(iconFileName))
            plist.Root?.Element("dict")?.AddFirst(PlistKeyValue("CFBundleIconFile", iconFileName));

        return plist.ToString();
    }

    private static object[] PlistKeyValue(string key, string value)
    {
        return new object[] { new XElement("key", key), new XElement("string", value) };
    }

    private static object[] PlistKeyValue(string key, bool value)
    {
        return new object[] { new XElement("key", key), new XElement(value ? "true" : "false") };
    }

    private static object[] CreateDocumentTypes()
    {
        return new object[]
        {
            new XElement("key", "CFBundleDocumentTypes"),
            new XElement("array",
                CreateDocumentType("Quaver Package", "qp", "com.quavergame.package"),
                CreateDocumentType("Quaver Skin", "qs", "com.quavergame.skin"))
        };
    }

    private static object[] CreateUrlTypes()
    {
        return new object[]
        {
            new XElement("key", "CFBundleURLTypes"),
            new XElement("array",
                new XElement("dict",
                    PlistKeyValue("CFBundleURLName", "Quaver URL"),
                    PlistKeyValue("CFBundleURLRole", "Viewer"),
                    new XElement("key", "CFBundleURLSchemes"),
                    new XElement("array", new XElement("string", "quaver"))))
        };
    }

    private static XElement CreateDocumentType(string name, string extension, string uti)
    {
        return new XElement("dict",
            PlistKeyValue("CFBundleTypeName", name),
            new XElement("key", "CFBundleTypeExtensions"),
            new XElement("array", new XElement("string", extension)),
            PlistKeyValue("CFBundleTypeRole", "Viewer"),
            new XElement("key", "LSItemContentTypes"),
            new XElement("array", new XElement("string", uti)));
    }

    private static string CopyAppIcon(string resourcesPath, string currentDirectory, string sourceCodePath, Config configuration)
    {
        var iconPath = ResolveAppIconPath(currentDirectory, sourceCodePath, configuration);

        if (string.IsNullOrEmpty(iconPath))
        {
            Console.WriteLine("No macOS app icon was found. Set MacAppIconPath in config.json to include one.");
            return "";
        }

        var extension = Path.GetExtension(iconPath).ToLowerInvariant();
        var iconFileName = extension == ".icns" ? "Quaver.icns" : $"QuaverIcon{extension}";
        File.Copy(iconPath, Path.Combine(resourcesPath, iconFileName), true);

        return iconFileName;
    }

    private static string ResolveAppIconPath(string currentDirectory, string sourceCodePath, Config configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.MacAppIconPath))
        {
            var configuredPath = Path.IsPathRooted(configuration.MacAppIconPath)
                ? configuration.MacAppIconPath
                : Path.Combine(currentDirectory, configuration.MacAppIconPath);

            if (File.Exists(configuredPath))
                return configuredPath;

            Console.WriteLine($"Configured macOS app icon was not found: {configuredPath}");
        }

        var searchRoots = new[]
        {
            sourceCodePath,
            Path.Combine(sourceCodePath, "Quaver"),
            Path.Combine(sourceCodePath, "Quaver", "Assets"),
            Path.Combine(sourceCodePath, "Quaver", "Resources")
        };

        var extensions = new[] { ".icns", ".png", ".ico" };

        foreach (var root in searchRoots.Where(Directory.Exists))
        {
            var icon = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                .OrderByDescending(path => Path.GetExtension(path).Equals(".icns", StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => Path.GetFileName(path).Contains("icon", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();

            if (icon != null)
                return icon;
        }

        return "";
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Could not find directory to copy: {sourceDirectory}");

        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            File.Copy(file, Path.Combine(destinationDirectory, relativePath), true);
        }
    }

    private static void CreateUniversalMachOBinaries(string x64BuildPath, string arm64BuildPath, string outputBuildPath, string currentDirectory)
    {
        foreach (var x64File in Directory.GetFiles(x64BuildPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(x64BuildPath, x64File);
            var arm64File = Path.Combine(arm64BuildPath, relativePath);
            var outputFile = Path.Combine(outputBuildPath, relativePath);

            if (!File.Exists(arm64File) || !IsMachO(x64File, currentDirectory) || !IsMachO(arm64File, currentDirectory))
                continue;

            var x64Architectures = GetArchitectures(x64File, currentDirectory);
            var arm64Architectures = GetArchitectures(arm64File, currentDirectory);

            if (x64Architectures.SequenceEqual(arm64Architectures))
            {
                Console.WriteLine($"Skipping universal merge for {relativePath}; both files contain {string.Join(" ", x64Architectures)}.");
                WarnIfNotUniversal(relativePath, x64Architectures);
                continue;
            }

            if (x64Architectures.Intersect(arm64Architectures).Any())
            {
                if (relativePath.Equals("Quaver", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Cannot create universal Quaver executable because both publishes contain overlapping architectures. x64: {string.Join(" ", x64Architectures)}, arm64: {string.Join(" ", arm64Architectures)}");

                Console.WriteLine($"Skipping universal merge for {relativePath}; architectures overlap. x64: {string.Join(" ", x64Architectures)}, arm64: {string.Join(" ", arm64Architectures)}.");
                WarnIfNotUniversal(relativePath, x64Architectures.Union(arm64Architectures).OrderBy(architecture => architecture, StringComparer.Ordinal).ToArray());
                continue;
            }

            Console.WriteLine($"Creating universal binary: {relativePath}");
            RunCommand("lipo", new[] { "-create", x64File, arm64File, "-output", outputFile }, currentDirectory);
        }
    }

    private static bool IsMachO(string path, string currentDirectory)
    {
        var output = RunCommandWithOutput("file", new[] { path }, currentDirectory);
        return output.Contains("Mach-O", StringComparison.Ordinal);
    }

    private static void WarnIfNotUniversal(string relativePath, string[] architectures)
    {
        if (!architectures.Contains("arm64") || !architectures.Contains("x86_64"))
            Console.WriteLine($"Warning: {relativePath} is not universal after packaging. Architectures: {string.Join(" ", architectures)}.");
    }

    private static string[] GetArchitectures(string path, string currentDirectory)
    {
        var output = RunCommandWithOutput("lipo", new[] { "-archs", path }, currentDirectory);
        return output
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(architecture => architecture, StringComparer.Ordinal)
            .ToArray();
    }

    private static void DeleteAndCreate(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);

        Directory.CreateDirectory(path);
    }

    private static void RunCommand(string command, string[] args, string workingDirectory)
    {
        var processStartInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var arg in args)
            processStartInfo.ArgumentList.Add(arg);

        using var process = Process.Start(processStartInfo);

        if (process == null)
            throw new InvalidOperationException($"Failed to start command: {command}");

        var output = process.StandardOutput.ReadToEnd();
        output += process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{command} failed with exit code {process.ExitCode}: {output}");
    }

    private static string RunCommandWithOutput(string command, string[] args, string workingDirectory)
    {
        var processStartInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var arg in args)
            processStartInfo.ArgumentList.Add(arg);

        using var process = Process.Start(processStartInfo);

        if (process == null)
            throw new InvalidOperationException($"Failed to start command: {command}");

        var output = process.StandardOutput.ReadToEnd();
        output += process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{command} failed with exit code {process.ExitCode}: {output}");

        return output;
    }
}

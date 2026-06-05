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
        Console.WriteLine("Creating macOS app bundle...");

        var macAppBuildPath = Path.Combine(compiledBuildPath, "content-osx");
        DeleteAndCreate(macAppBuildPath);

        var appPath = Path.Combine(macAppBuildPath, AppName);
        var contentsPath = Path.Combine(appPath, "Contents");
        var macOsPath = Path.Combine(contentsPath, "MacOS");
        var resourcesPath = Path.Combine(contentsPath, "Resources");
        var x64PayloadPath = Path.Combine(resourcesPath, "osx-x64");
        var arm64PayloadPath = Path.Combine(resourcesPath, "osx-arm64");

        Directory.CreateDirectory(macOsPath);
        Directory.CreateDirectory(resourcesPath);

        CopyDirectory(Path.Combine(compiledBuildPath, "content-osx-x64"), x64PayloadPath);
        CopyDirectory(Path.Combine(compiledBuildPath, "content-osx-arm64"), arm64PayloadPath);

        var iconFileName = CopyAppIcon(resourcesPath, currentDirectory, sourceCodePath, configuration);

        var launcherPath = Path.Combine(macOsPath, "Quaver");
        File.WriteAllText(launcherPath, CreateLauncherScript());

        var compatibilityLauncherPath = Path.Combine(macAppBuildPath, "Quaver");
        File.WriteAllText(compatibilityLauncherPath, CreateCompatibilityLauncherScript());

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RunCommand("chmod", new[] { "+x", launcherPath }, currentDirectory);
            RunCommand("chmod", new[] { "+x", compatibilityLauncherPath }, currentDirectory);
        }

        File.WriteAllText(Path.Combine(contentsPath, "Info.plist"), CreateInfoPlist(version, iconFileName));

        Console.WriteLine($"Created macOS app bundle at {appPath}");
    }

    private static string CreateLauncherScript()
    {
        return """
               #!/bin/sh
               set -e

               SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
               CONTENTS_DIR="$(dirname "$SCRIPT_DIR")"
               APP_DIR="$(dirname "$CONTENTS_DIR")"
               INSTALL_DIR="$(dirname "$APP_DIR")"

               cd "$INSTALL_DIR"
               export QUAVER_INSTALL_DIR="$INSTALL_DIR"

               if [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = "1" ]; then
                   PAYLOAD_DIR="$CONTENTS_DIR/Resources/osx-arm64"
               else
                   PAYLOAD_DIR="$CONTENTS_DIR/Resources/osx-x64"
               fi

               exec "$PAYLOAD_DIR/Quaver" "$@"
               """;
    }

    private static string CreateCompatibilityLauncherScript()
    {
        return """
               #!/bin/sh
               set -e

               INSTALL_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
               cd "$INSTALL_DIR"
               export QUAVER_INSTALL_DIR="$INSTALL_DIR"

               exec "$INSTALL_DIR/Quaver.app/Contents/MacOS/Quaver" "$@"
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
}

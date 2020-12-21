﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.DotNet.XHarness.Common.Execution;
using Microsoft.DotNet.XHarness.Common.Logging;
using Microsoft.DotNet.XHarness.iOS.Shared.Utilities;

#nullable enable
namespace Microsoft.DotNet.XHarness.iOS.Shared
{
    public interface IAppBundleInformationParser
    {
        Task<AppBundleInformation> ParseFromProject(string projectFilePath, TestTarget target, string buildConfiguration);

        Task<AppBundleInformation> ParseFromAppBundle(string appPackagePath, TestTarget target, ILog log, CancellationToken cancellationToken = default);
    }

    public interface IAppBundleLocator
    {
        Task<string?> LocateAppBundle(XmlDocument projectFile, string projectFilePath, TestTarget target, string buildConfiguration);
    }

    public class AppBundleInformationParser : IAppBundleInformationParser
    {
        private const string PlistBuddyPath = "/usr/libexec/PlistBuddy";
        private const string Armv7 = "armv7";

        private readonly IProcessManager _processManager;
        private readonly IAppBundleLocator? _appBundleLocator;

        public AppBundleInformationParser(IProcessManager processManager, IAppBundleLocator? appBundleLocator = null)
        {
            _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
            _appBundleLocator = appBundleLocator;
        }

        public async Task<AppBundleInformation> ParseFromProject(string projectFilePath, TestTarget target, string buildConfiguration)
        {
            var csproj = new XmlDocument();
            csproj.LoadWithoutNetworkAccess(projectFilePath);

            string projectDirectory = Path.GetDirectoryName(projectFilePath) ?? throw new DirectoryNotFoundException($"Cannot find directory of project '{projectFilePath}'");

            string appName = csproj.GetAssemblyName();
            string infoPlistPath = csproj.GetInfoPListInclude() ?? throw new InvalidOperationException("Couldn't locate PList include tag");

            var infoPlist = new XmlDocument();
            string plistPath = Path.Combine(projectDirectory, infoPlistPath.Replace('\\', Path.DirectorySeparatorChar));
            infoPlist.LoadWithoutNetworkAccess(plistPath);

            string bundleIdentifier = infoPlist.GetCFBundleIdentifier();

            Extension? extension = null;
            string extensionPointIdentifier = infoPlist.GetNSExtensionPointIdentifier();
            if (!string.IsNullOrEmpty(extensionPointIdentifier))
            {
                extension = extensionPointIdentifier.ParseFromNSExtensionPointIdentifier();
            }

            var platform = target.IsSimulator() ? "iPhoneSimulator" : "iPhone";

            string? appPath = null;
            if (_appBundleLocator != null)
            {
                appPath = await _appBundleLocator.LocateAppBundle(csproj, projectFilePath, target, buildConfiguration);
            }

            appPath ??= csproj.GetOutputPath(platform, buildConfiguration)?.Replace('\\', Path.DirectorySeparatorChar);

            appPath = Path.Combine(
                projectDirectory,
                appPath ?? string.Empty,
                appName + (extension != null ? ".appex" : ".app"));

            string? arch = csproj.GetMtouchArch(platform, buildConfiguration);

            bool supports32 = arch != null && (Contains(arch, "ARMv7") || Contains(arch, "i386"));

            if (!Directory.Exists(appPath))
            {
                throw new DirectoryNotFoundException($"The app bundle directory `{appPath}` does not exist");
            }

            string launchAppPath = target.ToRunMode() == RunMode.WatchOS
                ? Directory.GetDirectories(Path.Combine(appPath, "Watch"), "*.app")[0]
                : appPath;

            return new AppBundleInformation(appName, bundleIdentifier, appPath, launchAppPath, supports32, extension);
        }

        public async Task<AppBundleInformation> ParseFromAppBundle(string appPackagePath, TestTarget target, ILog log, CancellationToken cancellationToken = default)
        {
            var plistPath = Path.Combine(appPackagePath, "Info.plist");

            if (!File.Exists(plistPath))
            {
                throw new Exception($"Failed to find Info.plist inside the app bundle at: '{plistPath}'");
            }

            var appName = await GetPlistProperty(plistPath, PListExtensions.BundleNamePropertyName, log, cancellationToken);
            var bundleIdentifier = await GetPlistProperty(plistPath, PListExtensions.BundleIdentifierPropertyName, log, cancellationToken);

            string supports32 = string.Empty;

            try
            {
                supports32 = await GetPlistProperty(plistPath, PListExtensions.RequiredDeviceCapabilities, log, cancellationToken);
            }
            catch
            {
                // The property might not be present
                log.WriteLine("Failed to get the UIRequiredDeviceCapabilities Info.plist property. Assumes 32 bit is not supported");
            }

            string launchAppPath = target.ToRunMode() == RunMode.WatchOS
                ? Directory.GetDirectories(Path.Combine(appPackagePath, "Watch"), "*.app")[0]
                : appPackagePath;

            return new AppBundleInformation(
                appName: appName,
                bundleIdentifier: bundleIdentifier,
                appPath: appPackagePath,
                launchAppPath: launchAppPath,
                supports32b: Contains(supports32, Armv7),
                extension: null);
        }

        private async Task<string> GetPlistProperty(string plistPath, string propertyName, ILog log, CancellationToken cancellationToken = default)
        {
            var args = new[]
            {
                "-c",
                $"Print {propertyName}",
                plistPath,
            };

            var commandOutput = new MemoryLog { Timestamp = false };
            var result = await _processManager.ExecuteCommandAsync(PlistBuddyPath, args, log, commandOutput, commandOutput, TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);

            if (!result.Succeeded)
            {
                throw new Exception($"Failed to get bundle information: {commandOutput}");
            }

            return commandOutput.ToString().Trim();
        }

        // This method was added because .NET Standard 2.0 doesn't have case ignorant Contains() for String.
        private static bool Contains(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.InvariantCultureIgnoreCase) > -1;
        }
    }
}

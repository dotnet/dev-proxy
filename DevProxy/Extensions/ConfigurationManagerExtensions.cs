// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using DevProxy.Commands;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.Configuration;
#pragma warning restore IDE0130

static class ConfigurationManagerExtensions
{
    public static ConfigurationManager ConfigureDevProxyConfig(this ConfigurationManager configuration, DevProxyConfigOptions options)
    {
        configuration.Sources.Clear();
        _ = configuration.SetBasePath(Directory.GetCurrentDirectory());

        string?[] configFiles = [
            // config file specified by the user takes precedence
            // null if not specified
            options.ConfigFile,
            // current directory
            "devproxyrc.jsonc",
            "devproxyrc.json",
            Path.Combine(".devproxy", "devproxyrc.jsonc"),
            Path.Combine(".devproxy", "devproxyrc.json"),
            Path.Combine(ProxyUtils.AppFolder ?? "", "devproxyrc.jsonc"),
            Path.Combine(ProxyUtils.AppFolder ?? "", "devproxyrc.json")
        ];

        foreach (var configFile in configFiles)
        {
            if (!string.IsNullOrEmpty(configFile) && File.Exists(configFile))
            {
                _ = configuration.AddJsonFile(configFile, optional: false, reloadOnChange: true);
                return configuration;
            }
        }

        throw new InvalidOperationException("No configuration file found. Please create a devproxyrc.json file in the current directory.");
    }
}
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

        foreach (var configFile in ProxyUtils.GetConfigFileCandidates(options.ConfigFile))
        {
            if (!string.IsNullOrEmpty(configFile) && File.Exists(configFile))
            {
                _ = configuration.AddConfigFile(configFile, optional: false, reloadOnChange: true);
                return configuration;
            }
        }

        throw new InvalidOperationException("No configuration file found. Please create a devproxyrc.json or devproxyrc.yaml file in the current directory.");
    }
}
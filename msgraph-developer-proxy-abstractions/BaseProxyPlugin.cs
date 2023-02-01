﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace Microsoft.Graph.DeveloperProxy.Abstractions;

public abstract class BaseProxyPlugin: IProxyPlugin {
  protected ISet<Regex>? _urlsToWatch;
  protected ILogger? _logger;

  public virtual string Name => throw new NotImplementedException();
  public virtual void Register(IPluginEvents pluginEvents,
                       IProxyContext context,
                       ISet<Regex> urlsToWatch,
                       IConfigurationSection? configSection = null) {
    if (pluginEvents is null) {
      throw new ArgumentNullException(nameof(pluginEvents));
    }

    if (context is null || context.Logger is null) {
      throw new ArgumentException($"{nameof(context)} must not be null and must supply a non-null Logger", nameof(context));
    
    }

    if (urlsToWatch is null || urlsToWatch.Count == 0) {
      throw new ArgumentException($"{nameof(urlsToWatch)} cannot be null or empty", nameof(urlsToWatch));
    }

    _urlsToWatch = urlsToWatch;
    _logger = context.Logger;
  }
}

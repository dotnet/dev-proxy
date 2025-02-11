// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;

namespace Microsoft.DevProxy.Abstractions;

public interface IProxyPlugin
{
    string Name { get; }
    Option[] GetOptions();
    Command[] GetCommands();
    Task RegisterAsync();
}

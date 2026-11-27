// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;
using DevProxy.Commands;
using System.CommandLine;
using Xunit;

namespace DevProxy.Tests;

public sealed class HelpDocsTests
{
    private static RootCommand CreateRootCommand(out Command pluginCommand)
    {
        var root = new RootCommand();
        var api = new Command("api");
        var show = new Command("show");
        show.SetAction(_ => { });
        api.Subcommands.Add(show);
        root.Subcommands.Add(api);

        pluginCommand = new Command("mocks");
        var pluginSubcommand = new Command("from-http-responses");
        pluginSubcommand.SetAction(_ => { });
        pluginCommand.Subcommands.Add(pluginSubcommand);
        root.Subcommands.Add(pluginCommand);

        return root;
    }

    [Fact]
    public void GetLink_RootCommand_ReturnsCliLink()
    {
        var root = CreateRootCommand(out _);

        Assert.Equal("https://go.devproxy.net?go=cli", HelpDocs.GetLink(root.Parse("")));
    }

    [Fact]
    public void GetLink_LeafCommand_ReturnsCommandPathLink()
    {
        var root = CreateRootCommand(out _);

        Assert.Equal("https://go.devproxy.net?go=cli-api-show", HelpDocs.GetLink(root.Parse("api show")));
    }

    [Fact]
    public void GetLink_GroupCommand_ReturnsCliLink()
    {
        var root = CreateRootCommand(out _);

        Assert.Equal("https://go.devproxy.net?go=cli", HelpDocs.GetLink(root.Parse("api")));
    }

    [Fact]
    public void GetLink_CommandWithExplicitLink_AppliesToSubcommands()
    {
        var root = CreateRootCommand(out var pluginCommand);
        HelpDocs.Set(pluginCommand, DocsLinks.Get("MockResponsePlugin"));

        Assert.Equal("https://go.devproxy.net?go=mockresponseplugin", HelpDocs.GetLink(root.Parse("mocks")));
        Assert.Equal("https://go.devproxy.net?go=mockresponseplugin", HelpDocs.GetLink(root.Parse("mocks from-http-responses")));
    }

    [Fact]
    public void GetLink_CommandWithoutDocs_ReturnsNull()
    {
        var root = CreateRootCommand(out var pluginCommand);
        HelpDocs.Set(pluginCommand, null);

        Assert.Null(HelpDocs.GetLink(root.Parse("mocks from-http-responses")));
    }

    [Fact]
    public void ForPlugin_ThirdPartyPlugin_ReturnsNull()
    {
        Assert.Null(DocsLinks.ForPlugin(typeof(HelpDocsTests)));
    }
}

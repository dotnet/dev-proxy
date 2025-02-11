// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Titanium.Web.Proxy.Http;

namespace Microsoft.DevProxy.Plugins;

internal class MessageUtils
{
    public static string BuildUseSdkForErrorsMessage(Request r) => 
        $"To handle API errors more easily, use the Microsoft Graph SDK. More info at {GetMoveToSdkUrl(r)}";

    public static string BuildUseSdkMessage(Request r) =>
        $"To more easily follow best practices for working with Microsoft Graph, use the Microsoft Graph SDK. More info at {GetMoveToSdkUrl(r)}";

    public static string GetMoveToSdkUrl(Request request)
    {
        // TODO: return language-specific guidance links based on the language detected from the User-Agent
        return "https://aka.ms/devproxy/guidance/move-to-js-sdk";
    }
}

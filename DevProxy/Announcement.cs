// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;

namespace DevProxy;

internal class Announcement(HttpClient httpClient)
{
    private static readonly Uri announcementUrl = new("https://aka.ms/devproxy/announcement");
    private readonly HttpClient _httpClient = httpClient;

    public async Task ShowAsync()
    {
        var announcement = await GetAsync();
        if (!string.IsNullOrEmpty(announcement))
        {
            // Unescape the announcement to remove any escape characters
            // in case we're using ANSI escape codes for color formatting
            announcement = Regex.Unescape(announcement);
            await Console.Error.WriteLineAsync(announcement);
        }
    }

    private async Task<string?> GetAsync()
    {
        try
        {
            return await _httpClient.GetStringAsync(announcementUrl);
        }
        catch
        {
            return null;
        }
    }
}
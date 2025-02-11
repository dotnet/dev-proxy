// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace Microsoft.DevProxy.Abstractions;

// from https://stackoverflow.com/questions/61553962/getting-nested-properties-with-system-text-json
public static partial class JsonExtensions
{
    public static JsonElement? Get(this JsonElement element, string name) => 
        element.ValueKind != JsonValueKind.Null && element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(name, out var value) 
            ? value : null;
    
    public static JsonElement? Get(this JsonElement element, int index)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return null;
        // Throw if index < 0
        return index < element.GetArrayLength() ? element[index] : null;
    }
}
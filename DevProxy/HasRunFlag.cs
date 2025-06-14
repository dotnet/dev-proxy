// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Utils;

namespace DevProxy;

static class HasRunFlag
{
    private static readonly string filename = Path.Combine(ProxyUtils.AppFolder!, ".hasrun");

    public static bool CreateIfMissing()
    {
        if (File.Exists(filename))
        {
            return false;
        }

        return Create();
    }

    private static bool Create()
    {
        try
        {
            File.WriteAllText(filename, "");
        }
        catch
        {
            return false;
        }
        return true;
    }

    public static void Remove()
    {
        try
        {
            if (File.Exists(filename))
            {
                File.Delete(filename);
            }
        }
        catch { }
    }
}

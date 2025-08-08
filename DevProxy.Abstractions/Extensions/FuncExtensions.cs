// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// from: https://github.com/justcoding121/titanium-web-proxy/blob/902504a324425e4e49fc5ba604c2b7fa172e68ce/src/Titanium.Web.Proxy/Extensions/FuncExtensions.cs

#pragma warning disable IDE0130
namespace Unobtanium.Web.Proxy.EventArguments;
#pragma warning restore IDE0130

public static class FuncExtensions
{
    internal static async Task InvokeAsync<T>(this AsyncEventHandler<T> callback, object sender, T args, ExceptionHandler? exceptionFunc)
    {
        var invocationList = callback.GetInvocationList();

        foreach (var @delegate in invocationList)
        {
            await InternalInvokeAsync((AsyncEventHandler<T>)@delegate, sender, args, exceptionFunc);
        }
    }

    private static async Task InternalInvokeAsync<T>(AsyncEventHandler<T> callback, object sender, T e, ExceptionHandler? exceptionFunc)
    {
        try
        {
            await callback(sender, e);
        }
        catch (Exception ex)
        {
            exceptionFunc?.Invoke(new InvalidOperationException("Exception thrown in user event", ex));
        }
    }
}
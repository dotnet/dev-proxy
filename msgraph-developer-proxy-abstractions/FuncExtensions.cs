// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// from: https://github.com/justcoding121/titanium-web-proxy/blob/902504a324425e4e49fc5ba604c2b7fa172e68ce/src/Titanium.Web.Proxy/Extensions/FuncExtensions.cs

using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;

namespace Microsoft.Graph.DeveloperProxy.Abstractions;

public static class FuncExtensions
{
  internal static async Task InvokeAsync<T>(this AsyncEventHandler<T> callback, object sender, T args, ExceptionHandler? exceptionFunc)
  {
    var invocationList = callback.GetInvocationList();

    foreach (var @delegate in invocationList)
      await InternalInvokeAsync((AsyncEventHandler<T>)@delegate, sender, args, exceptionFunc);
  }

  private static async Task InternalInvokeAsync<T>(AsyncEventHandler<T> callback, object sender, T args, ExceptionHandler? exceptionFunc)
  {
    try
    {
      await callback(sender, args);
    }
    catch (Exception e)
    {
      exceptionFunc?.Invoke(new Exception("Exception thrown in user event", e));
    }
  }
}
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Proxy.Http;

/// <summary>
/// Default immutable <see cref="IHttpHeader"/> implementation. Plugins use this
/// to construct headers for mocked responses (the canonical replacement for the
/// engine-specific header type).
/// </summary>
public sealed record HttpHeader(string Name, string Value) : IHttpHeader;

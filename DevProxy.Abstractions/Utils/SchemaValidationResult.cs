// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.Utils;

/// <summary>
/// Outcome of validating a JSON document against a JSON schema.
/// </summary>
public enum SchemaValidationStatus
{
    /// <summary>The document matches the schema, or there was nothing to validate.</summary>
    Valid,

    /// <summary>The document doesn't match the schema.</summary>
    Invalid,

    /// <summary>
    /// The schema couldn't be downloaded or parsed (for example, when there's no network
    /// connection), so the document wasn't validated.
    /// </summary>
    SchemaUnavailable
}

/// <summary>
/// Result of validating a JSON document against a JSON schema.
/// </summary>
/// <param name="Status">Whether the document is valid, invalid, or couldn't be validated.</param>
/// <param name="Errors">
/// The validation errors when the document is <see cref="SchemaValidationStatus.Invalid"/>, or
/// why the schema couldn't be retrieved when it's <see cref="SchemaValidationStatus.SchemaUnavailable"/>.
/// </param>
public sealed record SchemaValidationResult(SchemaValidationStatus Status, IEnumerable<string> Errors);

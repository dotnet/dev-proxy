// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#pragma warning disable IDE0130
namespace System;
#pragma warning restore IDE0130

internal static class StringExtensions
{
    /// <summary>
    /// Truncates the string to the specified maximum length.
    /// </summary>
    /// <param name="input">The input string to truncate.</param>
    /// <param name="maxLength">The maximum allowed length.</param>
    /// <returns>The truncated string if longer than maxLength; otherwise, the original string.</returns>
    /// <example>
    /// <code>
    /// "HelloWorld".MaxLength(5); // returns "Hello"
    /// "Hi".MaxLength(5); // returns "Hi"
    /// </code>
    /// </example>
    internal static string MaxLength(this string input, int maxLength)
    {
        return input.Length <= maxLength ? input : input[..maxLength];
    }

    /// <summary>
    /// Converts the first character of the string to uppercase (PascalCase).
    /// </summary>
    /// <param name="input">The input string.</param>
    /// <returns>The string converted to PascalCase.</returns>
    /// <example>
    /// <code>
    /// "helloWorld".ToPascalCase(); // returns "HelloWorld"
    /// </code>
    /// </example>
    internal static string ToPascalCase(this string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return char.ToUpper(input[0]) + input[1..];
    }

    /// <summary>
    /// Converts the first character of the string to lowercase (camelCase).
    /// </summary>
    /// <param name="str">The input string.</param>
    /// <returns>The string converted to camelCase.</returns>
    /// <example>
    /// <code>
    /// "HelloWorld".ToCamelCase(); // returns "helloWorld"
    /// </code>
    /// </example>
    internal static string ToCamelCase(this string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        return char.ToLowerInvariant(str[0]) + str[1..];
    }

    /// <summary>
    /// Converts the string to kebab-case.
    /// </summary>
    /// <param name="str">The input string.</param>
    /// <returns>The string converted to kebab-case.</returns>
    /// <example>
    /// <code>
    /// "HelloWorld".ToKebabCase(); // returns "hello-world"
    /// </code>
    /// </example>
    internal static string ToKebabCase(this string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        return string.Concat(str.Select((x, i) => i > 0 && char.IsUpper(x) ? "-" + x : x.ToString())).ToLower();
    }

    /// <summary>
    /// Converts a kebab-case string to camelCase.
    /// </summary>
    /// <param name="str">The kebab-case input string.</param>
    /// <returns>The string converted to camelCase.</returns>
    /// <example>
    /// <code>
    /// "hello-world-example".ToCamelFromKebabCase(); // returns "helloWorldExample"
    /// </code>
    /// </example>
    internal static string ToCamelFromKebabCase(this string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        var parts = str.Split('-');
        if (parts.Length == 0)
            return str.ToCamelCase();

        return parts[0] + string.Concat(parts.Skip(1).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
    }

    /// <summary>
    /// Replaces occurrences of a specified string with another string, starting from a given index.
    /// </summary>
    /// <param name="input">The input string.</param>
    /// <param name="oldValue">The string to be replaced.</param>
    /// <param name="newValue">The replacement string.</param>
    /// <param name="startIndex">The index from which to start replacing.</param>
    /// <returns>The modified string after replacements.</returns>
    /// <example>
    /// <code>
    /// "HelloWorldHello".Replace("Hello", "Hi", 5); // returns "HelloWorldHi"
    /// </code>
    /// </example>
    internal static string Replace(this string input, string oldValue, string newValue, int startIndex)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldValue))
        {
            return input;
        }

        if (startIndex < 0 || startIndex >= input.Length)
        {
            return input;
        }

        return input[..startIndex] + input[startIndex..].Replace(oldValue, newValue);
    }
}
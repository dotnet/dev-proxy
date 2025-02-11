// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// from https://github.com/dotnet/runtime/blob/e1c671760e23de03ee4be74eeb26831813488100/src/libraries/Microsoft.Extensions.Logging.Console/src/AnsiParser.cs

namespace Microsoft.DevProxy.Logging;

internal static class AnsiParser
{
  internal const string DefaultForegroundColor = "\x1B[39m\x1B[22m"; // reset to default foreground color
  internal const string DefaultBackgroundColor = "\x1B[49m"; // reset to the background color

  internal static string GetForegroundColorEscapeCode(ConsoleColor color)
  {
    return color switch
    {
      ConsoleColor.Black => "\x1B[30m",
      ConsoleColor.DarkRed => "\x1B[31m",
      ConsoleColor.DarkGreen => "\x1B[32m",
      ConsoleColor.DarkYellow => "\x1B[33m",
      ConsoleColor.DarkBlue => "\x1B[34m",
      ConsoleColor.DarkMagenta => "\x1B[35m",
      ConsoleColor.DarkCyan => "\x1B[36m",
      ConsoleColor.Gray => "\x1B[37m",
      ConsoleColor.Red => "\x1B[1m\x1B[31m",
      ConsoleColor.Green => "\x1B[1m\x1B[32m",
      ConsoleColor.Yellow => "\x1B[1m\x1B[33m",
      ConsoleColor.Blue => "\x1B[1m\x1B[34m",
      ConsoleColor.Magenta => "\x1B[1m\x1B[35m",
      ConsoleColor.Cyan => "\x1B[1m\x1B[36m",
      ConsoleColor.White => "\x1B[1m\x1B[37m",
      _ => DefaultForegroundColor // default foreground color
    };
  }

  internal static string GetBackgroundColorEscapeCode(ConsoleColor color)
  {
    return color switch
    {
      ConsoleColor.Black => "\x1B[40m",
      ConsoleColor.DarkRed => "\x1B[41m",
      ConsoleColor.DarkGreen => "\x1B[42m",
      ConsoleColor.DarkYellow => "\x1B[43m",
      ConsoleColor.DarkBlue => "\x1B[44m",
      ConsoleColor.DarkMagenta => "\x1B[45m",
      ConsoleColor.DarkCyan => "\x1B[46m",
      ConsoleColor.Gray => "\x1B[47m",
      _ => DefaultBackgroundColor // Use default background color
    };
  }
}
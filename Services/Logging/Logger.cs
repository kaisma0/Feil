#nullable disable
using System;

namespace Feil.Core;

public static class Logger
{
    public static event Action<string> OnLogMessage;

    private static string FormatMessage(string message, object[] args)
    {
        if (args == null || args.Length == 0)
        {
            return message;
        }

        try
        {
            return string.Format(message, args);
        }
        catch (FormatException)
        {
            return message;
        }
    }

    public static void WriteLine(string message, params object[] args)
    {
        OnLogMessage?.Invoke(FormatMessage(message, args));
    }

    public static void WriteLine(object value)
    {
        OnLogMessage?.Invoke(value?.ToString() ?? string.Empty);
    }

    public static void WriteLine()
    {
        OnLogMessage?.Invoke(string.Empty);
    }

    public static void Write(string message, params object[] args)
    {
        OnLogMessage?.Invoke(FormatMessage(message, args));
    }

    public static void Write(object value)
    {
        OnLogMessage?.Invoke(value?.ToString() ?? string.Empty);
    }
}
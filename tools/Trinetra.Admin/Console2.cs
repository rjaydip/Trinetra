namespace Trinetra.Admin;

/// <summary>Console output helpers. Colour is used sparingly and degrades to plain text.</summary>
internal static class Out
{
    public static void Info(string message) => Console.WriteLine(message);

    public static void Ok(string message) => Write(ConsoleColor.Green, $"  OK    {message}");

    public static void Warn(string message) => Write(ConsoleColor.Yellow, $"  WARN  {message}");

    public static void Fail(string message) => Write(ConsoleColor.Red, $"  FAIL  {message}");

    public static void Heading(string message)
    {
        Console.WriteLine();
        Write(ConsoleColor.Cyan, message);
        Console.WriteLine(new string('-', Math.Max(message.Length, 10)));
    }

    private static void Write(ConsoleColor colour, string message)
    {
        if (Console.IsOutputRedirected)
        {
            // Redirected output is usually a log or a CI transcript; escape codes there are noise.
            Console.WriteLine(message);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Reads a secret without echoing it.
    /// </summary>
    /// <remarks>
    /// Passwords are prompted rather than accepted as a flag. A password on the command line
    /// lands in shell history, in the process list where any local user can read it, and often
    /// in CI logs.
    /// </remarks>
    public static string ReadSecret(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
        {
            // Piped input: read a line so provisioning scripts still work.
            var piped = Console.ReadLine() ?? string.Empty;
            Console.WriteLine();
            return piped;
        }

        var buffer = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}

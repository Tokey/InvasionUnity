using System;
using System.Linq;

namespace JndUfo
{
    /// <summary>
    /// Generates a random 4-character alphanumeric string to uniquely identify a single run
    /// of the application. Used to prevent log files from overwriting each other if a session
    /// crashes and the same session ID is reused.
    /// </summary>
    public static class RunId
    {
        const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        static readonly Random Random = new Random();

        public static string Generate()
        {
            return new string(Enumerable.Repeat(Chars, 4)
                .Select(s => s[Random.Next(s.Length)]).ToArray());
        }
    }
}

using System;

namespace ImageServer
{
    public static class Logger
    {
        private static readonly object _lock = new object();

        public static void Log(string message)
        {
            lock (_lock)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId:D2}] {message}");
            }
        }

        public static void Error(string message)
        {
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [ERROR] {message}");
                Console.ResetColor();
            }
        }
    }
}

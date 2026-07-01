using System;
using System.IO;

namespace ImageServer
{
    class Program
    {
        static void Main(string[] args)
        {
            int port = 5050;
            int maxCacheItems = 5;
            int maxParallelTasks = 4;
            int idleTimeoutSeconds = 60;

            string rootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Slike");

            if (!Directory.Exists(rootPath))
            {
                Directory.CreateDirectory(rootPath);
                Directory.CreateDirectory(Path.Combine(rootPath, "grb"));
            }

            ImageWebServer server = new ImageWebServer(port, rootPath, maxCacheItems, maxParallelTasks);

            Console.WriteLine("=== SISTEMSKO PROGRAMIRANJE: IMAGE SERVER (Task/Async verzija) ===");
            Console.WriteLine($"Port: {port} | Max taskova: {maxParallelTasks} | Max kes: {maxCacheItems}");
            //Console.WriteLine("Primer poziva: http://localhost:5050/srbija.png\n");

            server.Start(TimeSpan.FromSeconds(idleTimeoutSeconds));
        }
    }
}

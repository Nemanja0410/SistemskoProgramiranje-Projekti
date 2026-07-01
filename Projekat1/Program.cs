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
            int workerThreads = 4;
            int idleTimeoutSeconds = 60;

            string rootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Slike");

            if (!Directory.Exists(rootPath))
            {
                Directory.CreateDirectory(rootPath);
                Directory.CreateDirectory(Path.Combine(rootPath, "Podfolder"));
            }

            ImageWebServer server = new ImageWebServer(port, rootPath, maxCacheItems, workerThreads);

            Console.WriteLine("=== SISTEMSKO PROGRAMIRANJE: IMAGE SERVER ===");
            server.Start(TimeSpan.FromSeconds(idleTimeoutSeconds));
        }
    }
}

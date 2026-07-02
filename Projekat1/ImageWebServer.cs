using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace ImageServer
{
    public class ImageWebServer
    {
        private readonly int _port;
        private readonly string _rootPath;
        private readonly BlockingCollection<HttpListenerContext> _queue = new BlockingCollection<HttpListenerContext>();
        private readonly ImageCache _cache;
        private readonly int _threadCount;
        private readonly HttpListener _listener;

        private readonly Dictionary<string, List<HttpListenerContext>> _pendingRequests = new Dictionary<string, List<HttpListenerContext>>();

        public ImageWebServer(int port, string rootPath, int cacheSize, int threadCount)
        {
            _port = port;
            _rootPath = rootPath;
            _cache = new ImageCache(cacheSize);
            _threadCount = threadCount;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
        }

        public void Start(TimeSpan? idleTimeout = null)
        {
            _listener.Start();
            Logger.Log($"Server pokrenut na portu {_port}. Pretraga u: {_rootPath}");

            Thread[] workerThreads = new Thread[_threadCount];
            for (int i = 0; i < _threadCount; i++)
            {
                workerThreads[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"Worker-{i}" };
                workerThreads[i].Start();
            }

            Timer? idleTimer = null;
            if (idleTimeout.HasValue)
            {
                Logger.Log($"Server ce se ugasiti nakon {idleTimeout.Value.TotalSeconds}s neaktivnosti.");
                idleTimer = new Timer(_ =>
                {
                    Logger.Log("Nema novih zahteva - server se gasi zbog neaktivnosti.");
                    Stop();
                }, null, idleTimeout.Value, Timeout.InfiniteTimeSpan);
            }

            while (_listener.IsListening)
            {
                try
                {
                    HttpListenerContext ctx = _listener.GetContext();
                    idleTimer?.Change(idleTimeout!.Value, Timeout.InfiniteTimeSpan);
                    _queue.Add(ctx);
                    Logger.Log($"[Prijem] Zahtev za '{ctx.Request.Url?.AbsolutePath}' dodat u red.");
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Logger.Error($"Greska pri prijemu: {ex.Message}"); }
            }

            idleTimer?.Dispose();
            _queue.CompleteAdding();
            foreach (Thread t in workerThreads)
                t.Join();
            Logger.Log("Server zaustavljen.");
        }

        public void Stop()
        {
            if (_listener.IsListening)
                _listener.Stop();
        }

        private void WorkerLoop()
        {
            foreach (HttpListenerContext ctx in _queue.GetConsumingEnumerable())
            {
                ProcessRequest(ctx);
            }
        }

        private void ProcessRequest(HttpListenerContext ctx)
        {
            string? fileName = ctx.Request.Url?.AbsolutePath.TrimStart('/');
            HttpListenerResponse response = ctx.Response;

            if (string.IsNullOrEmpty(fileName))
            {
                SendResponse(response, "Greska: Navedite ime fajla u URL-u.", 400);
                return;
            }

            string ext = Path.GetExtension(fileName).ToLower();
            string[] allowedExts = { ".jpg", ".jpeg", ".png", ".gif", ".bmp",".svg"};
            if (!allowedExts.Contains(ext))
            {
                Logger.Log($"[BLOCKED] Nedozvoljen tip fajla: '{fileName}'");
                SendResponse(response, $"Tip fajla '{ext}' nije dozvoljen. Dozvoljeni tipovi: jpg, jpeg, png, gif, bmp, svg.", 400);
                return;
            }

            if (_cache.TryGet(fileName, out byte[]? data))
            {
                Logger.Log($"[HIT] Slika '{fileName}' poslata iz kesa.");
                SendImage(response, data!, fileName);
                return;
            }

            lock (_pendingRequests)
            {
                if (_pendingRequests.TryGetValue(fileName, out var waiters))
                {
                    Logger.Log($"[WAIT] Zahtev za '{fileName}' ceka na preuzimanje koje je vec u toku.");
                    waiters.Add(ctx);
                    return;
                }

                _pendingRequests[fileName] = new List<HttpListenerContext>();
            }

            try
            {
                Logger.Log($"[MISS] Fajl '{fileName}' se pribavlja sa diska.");
                data = FindAndLoadImage(fileName);
            }
            catch (Exception ex)
            {
                Logger.Error($"Greska pri preuzimanju '{fileName}': {ex.Message}");
                data = null;
            }

            List<HttpListenerContext> cekajuci;
            lock (_pendingRequests)
            {
                cekajuci = _pendingRequests[fileName];
                _pendingRequests.Remove(fileName);
            }

            if (data != null)
                _cache.Add(fileName, data);

            SendToAll(fileName, data, ctx, cekajuci);
        }

        private void SendToAll(string fileName, byte[]? data, HttpListenerContext owner, List<HttpListenerContext> waiters)
        {
            if (data != null)
                SendImage(owner.Response, data, fileName);
            else
                SendResponse(owner.Response, $"Fajl '{fileName}' nije pronadjen.", 404);

            foreach (var w in waiters)
            {
                if (data != null)
                    SendImage(w.Response, data, fileName);
                else
                    SendResponse(w.Response, $"Fajl '{fileName}' nije pronadjen.", 404);
            }
        }

        private byte[]? FindAndLoadImage(string fileName)
        {
            try
            {
                string[] files = Directory.GetFiles(_rootPath, fileName, SearchOption.AllDirectories);
                if (files.Length > 0)
                    return File.ReadAllBytes(files[0]);
            }
            catch (Exception ex) { Logger.Error($"Greska pri citanju fajla: {ex.Message}"); }
            return null;
        }

        private void SendImage(HttpListenerResponse response, byte[] data, string fileName)
        {
            try
            {
                string ext = Path.GetExtension(fileName).TrimStart('.').ToLower();
                response.ContentType = $"image/{ext}";
                response.ContentLength64 = data.Length;
                response.OutputStream.Write(data, 0, data.Length);
                response.OutputStream.Close();
                Logger.Log($"[Odgovor] Slika '{fileName}' ({data.Length} bytes) poslata klijentu.");
            }
            catch { }
        }

        private void SendResponse(HttpListenerResponse response, string message, int statusCode)
        {
            try
            {
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(message);
                response.StatusCode = statusCode;
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
                Logger.Log($"[Odgovor] Status {statusCode}: {message}");
            }
            catch { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ImageServer
{
    public class ImageWebServer
    {
        private readonly int _port;
        private readonly string _rootPath;
        private readonly ImageCache _cache;
        private readonly int _maxParallelTasks;
        private readonly HttpListener _listener;
        private readonly Channel<HttpListenerContext> _channel;

        private readonly Dictionary<string, TaskCompletionSource<byte[]?>> _pendingRequests = new Dictionary<string, TaskCompletionSource<byte[]?>>();
        private readonly object _lock = new object();

        public ImageWebServer(int port, string rootPath, int cacheSize, int maxParallelTasks)
        {
            _port = port;
            _rootPath = rootPath;
            _cache = new ImageCache(cacheSize);
            _maxParallelTasks = maxParallelTasks;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _channel = Channel.CreateUnbounded<HttpListenerContext>();
        }

        public void Start(TimeSpan? idleTimeout = null)
        {
            _listener.Start();
            Logger.Log($"Server pokrenut. Port: {_port} | Slike: {_rootPath} | Max taskova: {_maxParallelTasks}");

            Task[] workerTasks = new Task[_maxParallelTasks];
            for (int i = 0; i < _maxParallelTasks; i++)
                workerTasks[i] = Task.Run(WorkerLoopAsync);

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
                    _channel.Writer.TryWrite(ctx);
                    Logger.Log($"[Prijem] Zahtev za '{ctx.Request.Url?.AbsolutePath}' dodat u red.");
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Logger.Error($"Greska pri prijemu: {ex.Message}"); }
            }

            idleTimer?.Dispose();
            _channel.Writer.Complete();
            Task.WhenAll(workerTasks).Wait();
            Logger.Log("Server zaustavljen.");
        }

        public void Stop()
        {
            if (_listener.IsListening)
                _listener.Stop();
        }

        private async Task WorkerLoopAsync()
        {
            await foreach (HttpListenerContext ctx in _channel.Reader.ReadAllAsync())
            {
                await ProcessRequestAsync(ctx);
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext ctx)
        {
            string? fileName = ctx.Request.Url?.AbsolutePath.TrimStart('/');
            HttpListenerResponse response = ctx.Response;

            if (string.IsNullOrEmpty(fileName))
            {
                await SendResponseAsync(response, "Greska: Navedite ime fajla u URL-u.", 400);
                return;
            }

            string ext = Path.GetExtension(fileName).ToLower();
            string[] allowedExts = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg" };
            if (!allowedExts.Contains(ext))
            {
                Logger.Log($"[BLOCKED] Nedozvoljen tip fajla: '{fileName}'");
                await SendResponseAsync(response, $"Tip fajla '{ext}' nije dozvoljen. Dozvoljeni tipovi: jpg, jpeg, png, gif, bmp, svg.", 400);
                return;
            }

            Logger.Log($"[Task] Obrada zahteva za: '{fileName}'");

            if (_cache.TryGet(fileName, out byte[]? cached))
            {
                Logger.Log($"[HIT] '{fileName}' - saljem iz kesa.");
                await SendImageAsync(response, cached!, fileName);
                return;
            }

            TaskCompletionSource<byte[]?> tcs;
            lock (_lock)
            {
                if (_pendingRequests.TryGetValue(fileName, out var existing))
                {
                    Logger.Log($"[WAIT] '{fileName}' - cekam na vec aktivan fetch.");
                    Task.Run(async () => await HandleWaiterAsync(existing.Task, ctx, fileName));
                    return;
                }

                tcs = new TaskCompletionSource<byte[]?>();
                _pendingRequests[fileName] = tcs;
            }

            byte[]? data = null;
            try
            {
                Logger.Log($"[MISS] '{fileName}' - citam sa diska.");
                data = await FindAndLoadImageAsync(fileName);
                if (data != null)
                    _cache.Add(fileName, data);
                tcs.SetResult(data);
            }
            catch (Exception ex)
            {
                Logger.Error($"Greska pri preuzimanju '{fileName}': {ex.Message}");
                tcs.SetResult(null);
            }
            finally
            {
                lock (_lock) { _pendingRequests.Remove(fileName); }
            }

            if (data != null)
                await SendImageAsync(response, data, fileName);
            else
                await SendResponseAsync(response, $"Fajl '{fileName}' nije pronadjen.", 404);
        }

        private async Task HandleWaiterAsync(Task<byte[]?> ownerTask, HttpListenerContext ctx, string fileName)
        {
            byte[]? data = await ownerTask;
            if (data != null)
                await SendImageAsync(ctx.Response, data, fileName);
            else
                await SendResponseAsync(ctx.Response, $"Fajl '{fileName}' nije pronadjen.", 404);
        }

        private async Task<byte[]?> FindAndLoadImageAsync(string fileName)
        {
            try
            {
                string[] files = await Task.Run(() =>
                    Directory.GetFiles(_rootPath, fileName, SearchOption.AllDirectories));

                if (files.Length == 0)
                {
                    Logger.Log($"[FindImage] '{fileName}' nije pronadjen.");
                    return null;
                }

                Logger.Log($"[FindImage] Pronadjen: {files[0]}");
                return await File.ReadAllBytesAsync(files[0]);
            }
            catch (Exception ex)
            {
                Logger.Error($"Greska pri citanju fajla: {ex.Message}");
                return null;
            }
        }

        private async Task SendImageAsync(HttpListenerResponse response, byte[] data, string fileName)
        {
            try
            {
                string ext = Path.GetExtension(fileName).TrimStart('.').ToLower();
                string contentType = ext == "jpg" ? "jpeg" : ext;
                response.ContentType = $"image/{contentType}";
                response.ContentLength64 = data.Length;
                await response.OutputStream.WriteAsync(data, 0, data.Length);
                response.OutputStream.Close();
                Logger.Log($"[Odgovor] Slika '{fileName}' ({data.Length} bytes) poslata klijentu.");
            }
            catch (Exception ex) { Logger.Error($"Greska pri slanju slike: {ex.Message}"); }
        }

        private async Task SendResponseAsync(HttpListenerResponse response, string message, int statusCode)
        {
            try
            {
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(message);
                response.StatusCode = statusCode;
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.OutputStream.Close();
                Logger.Log($"[Odgovor] Status {statusCode}: {message}");
            }
            catch (Exception ex) { Logger.Error($"Greska pri slanju odgovora: {ex.Message}"); }
        }
    }
}

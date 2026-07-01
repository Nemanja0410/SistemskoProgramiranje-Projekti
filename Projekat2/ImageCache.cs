using System.Collections.Generic;

namespace ImageServer
{
    public class ImageCache
    {
        private readonly int _maxSize;
        private readonly Dictionary<string, byte[]> _data      = new Dictionary<string, byte[]>();
        private readonly LinkedList<string>          _lruOrder = new LinkedList<string>();
        private readonly object                      _lock     = new object();

        public ImageCache(int maxSize)
        {
            _maxSize = maxSize;
        }

        public bool TryGet(string key, out byte[]? value)
        {
            lock (_lock)
            {
                if (_data.TryGetValue(key, out value))
                {
                    _lruOrder.Remove(key);
                    _lruOrder.AddLast(key);
                    return true;
                }
                return false;
            }
        }

        public void Add(string key, byte[] value)
        {
            lock (_lock)
            {
                if (_data.ContainsKey(key)) return;

                if (_data.Count >= _maxSize)
                {
                    string oldest = _lruOrder.First!.Value;
                    _lruOrder.RemoveFirst();
                    _data.Remove(oldest);
                    Logger.Log($"[Kes] Izbacen '{oldest}' zbog LRU limite.");
                }

                _data[key] = value;
                _lruOrder.AddLast(key);
                Logger.Log($"[Kes] Kesiran '{key}'. Trenutno u kesu: {_data.Count}/{_maxSize}");
            }
        }

        public int Count
        {
            get { lock (_lock) { return _data.Count; } }
        }
    }
}

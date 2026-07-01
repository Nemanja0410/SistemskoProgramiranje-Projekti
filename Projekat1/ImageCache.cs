using System.Collections.Generic;

namespace ImageServer
{
    public class ImageCache
    {
        private readonly int _maxSize;
        private readonly Dictionary<string, byte[]> _data = new Dictionary<string, byte[]>();
        private readonly LinkedList<string> _lruOrder = new LinkedList<string>();
        private readonly object _lock = new object();

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
                    Logger.Log($"[Cache] Izbačen fajl zbog limita veličine: {oldest}");
                }

                _data[key] = value;
                _lruOrder.AddLast(key);
                Logger.Log($"[Cache] Keširan fajl: {key} (Ukupno: {_data.Count})");
            }
        }
    }
}

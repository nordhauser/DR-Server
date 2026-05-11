using System.Collections.Generic;

namespace DungeonRunners.Networking
{
    public class MessageQueue
    {
        private Queue<byte[]> _queue = new Queue<byte[]>();

        public void Enqueue(byte[] data)
        {
            _queue.Enqueue(data);
        }

        public bool IsEmpty()
        {
            return _queue.Count == 0;
        }

        public List<byte[]> DequeueAll()
        {
            var messages = new List<byte[]>(_queue);
            _queue.Clear();
            return messages;
        }

        public int Count => _queue.Count;
        // ✅ ADD THIS METHOD
        public void Clear()
        {
            _queue.Clear();
        }
    }
}
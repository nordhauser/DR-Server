using System.Collections.Generic;

namespace DungeonRunners.Networking
{
    public class MessageQueue
    {
        private Queue<byte[]> _queue = new Queue<byte[]>();

        public void Enqueue(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;
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

        public void Clear()
        {
            _queue.Clear();
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace CommsLIBPlus.Pooling
{
    /// <summary>
    /// A pool of objects that can be reused to manage memory efficiently.
    /// </summary>
    /// <typeparam name="T">The type of object that is pooled.</typeparam>
    public static class Pool<T> where T : IPoolable, new()
    {
        // The queue that holds the items
        private readonly static ConcurrentQueue<T> _queue = null;
        // The maximum size
        public static int PoolSize { get; set; } = 5;

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="poolCount">The count of items in the pool.</param>
        /// <param name="newItemMethod">The method that creates a new item.</param>
        /// <param name="resetItemMethod">The method that resets an item's state. Optional.</param>
        static Pool()
        {
            _queue = new ConcurrentQueue<T>();

            for (int i = 0; i < PoolSize; i++)
            {
                var item = new T();
                item.Reset();
                _queue.Enqueue(item);
            }
        }

        /// <summary>
        /// Pushes an item into the pool for later re-use, and resets it state if a reset method was provided to the constructor.
        /// </summary>
        /// <param name="item">The item.</param>
        public static void Push(T item)
        {
            // Limit queue size
            if (_queue.Count >= PoolSize)
            {
                // Dispose if applicable
                if (item is IDisposable disposable)
                    disposable.Dispose();
                return;
            }

            item.Reset();

            _queue.Enqueue(item);
        }

        /// <summary>
        /// Pops an item out of the pool for use.
        /// </summary>
        /// <returns>An item.</returns>
        public static T Pop()
        {
            if (_queue.TryDequeue(out T item))
                return item;
            else
                return new T();
        }
    }
}

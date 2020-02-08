using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace CommsLIBPlus.Memory
{
    /// <summary>
    /// A thin wrapper around a leased array; when disposed, the array
    /// is returned to the pool; the caller is responsible for not retaining
    /// a reference to the array (via .Memory / .ArraySegment) after using Dispose()
    /// Marc Gravell with Pooling added by Jesus Alvarez
    /// </summary>
    internal sealed class ArrayPoolOwner<T> : IMemoryOwner<T>
    {
        private int _length;
        private T[] _oversized;

        #region Pool
        private static readonly ObjectPool<ArrayPoolOwner<T>> Pool = new ObjectPool<ArrayPoolOwner<T>>(() => new ArrayPoolOwner<T>());
        public static IMemoryOwner<T> Rent(T[] oversized, int length)
        {
            var arrayPoolOwner = Pool.GetObject();
            arrayPoolOwner.Init(oversized, length);

            return arrayPoolOwner;
        }
        #endregion

        internal ArrayPoolOwner(T[] oversized, int length)
        {
            _length = length;
            _oversized = oversized;
        }

        internal ArrayPoolOwner()
        {
        }

        public void Init(T[] oversized, int length)
        {
            _oversized = oversized;
            _length = length;
        }

        public Memory<T> Memory => new Memory<T>(GetArray(), 0, _length);

        private T[] GetArray() =>
            Interlocked.CompareExchange(ref _oversized, null, null)
            ?? throw new ObjectDisposedException(ToString());

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            // Return Array
            var arr = Interlocked.Exchange(ref _oversized, null);
            if (arr != null) ArrayPool<T>.Shared.Return(arr);
            // Return itself
            Pool.PutObject(this);
        }

        //~ArrayPoolOwner() { Interlocked.Increment(ref _leakCount); }
        //private static int _leakCount;
        //internal static int LeakCount() => Thread.VolatileRead(ref _leakCount);
    }
}

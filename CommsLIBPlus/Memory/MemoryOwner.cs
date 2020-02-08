using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace CommsLIBPlus.Memory
{
    /// <summary>
    /// Borrowed from Marc Gravell
    /// </summary>
    public static class MemoryOwner
    {
        //public static int LeakCount<T>() => ArrayPoolOwner<T>.LeakCount();
        public static IMemoryOwner<T> Empty<T>() => SimpleMemoryOwner<T>.Empty;

        public static IMemoryOwner<T> Owned<T>(this Memory<T> memory)
            => new SimpleMemoryOwner<T>(memory);


        private sealed class SimpleMemoryOwner<T> : IMemoryOwner<T>
        {
            public static IMemoryOwner<T> Empty { get; } = new SimpleMemoryOwner<T>(Array.Empty<T>());
            public SimpleMemoryOwner(Memory<T> memory) => Memory = memory;

            public Memory<T> Memory { get; }
            public void Dispose() { }
        }

        /// <summary>
        /// Creates a lease over the provided array; the contents are not copied - the array
        /// provided will be handed to the pool when disposed
        /// </summary>
        public static IMemoryOwner<T> Lease<T>(this T[] source, int length = -1)
        {
            if (source == null) return null; // GIGO
            if (length < 0) length = source.Length;
            else if (length > source.Length) throw new ArgumentOutOfRangeException(nameof(length));
            return length == 0 ? Empty<T>() : ArrayPoolOwner<T>.Rent(source, length);
        }
        /// <summary>
        /// Creates a lease from the provided sequence, copying the data out into a linear vector
        /// </summary>
        public static IMemoryOwner<T> Lease<T>(this ReadOnlySequence<T> source)
        {
            if (source.IsEmpty) return Empty<T>();

            int len = checked((int)source.Length);
            var arr = ArrayPool<T>.Shared.Rent(len);
            source.CopyTo(arr);
            return ArrayPoolOwner<T>.Rent(arr, len);
        }

        /// <summary>
        /// Creates a lease from the provided sequence, copying the data out into a linear vector
        /// </summary>
        public static IMemoryOwner<T> Lease<T>(this ReadOnlyMemory<T> source)
        {
            if (source.IsEmpty) return Empty<T>();

            int len = checked((int)source.Length);
            var arr = ArrayPool<T>.Shared.Rent(len);
            source.CopyTo(arr);
            return ArrayPoolOwner<T>.Rent(arr, len);
        }

    }
    
}

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace CommsLIBPlus.Memory
{
    public class LengthPrefixFrameBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private const int SIZE = 4096;
        private byte[] memory;
        public int Position { get; private set; } = 0;

        public LengthPrefixFrameBufferWriter()
        {
            memory = ArrayPool<byte>.Shared.Rent(SIZE);
        }

        public void Reset()
        {
            Position = 4;
        }

        public void Advance(int count)
        {
            Position += count;
            // Update coded length
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(memory, 0, 4), Position - 4);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            return new Memory<byte>(memory, Position, sizeHint);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            return new Span<byte>(memory, Position, sizeHint);
        }

        public ReadOnlyMemory<byte> GetFrameWithLengthPrefix()
        {
            return new ReadOnlyMemory<byte>(memory, 0, Position);
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    ArrayPool<byte>.Shared.Return(memory);
                }

                memory = null;

                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }
        #endregion

    }
}

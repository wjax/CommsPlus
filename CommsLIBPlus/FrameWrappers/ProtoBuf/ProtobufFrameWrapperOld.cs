using CommsLIBPlus.Base;
using CommsLIBPlus.Communications.FrameWrappers;
using CommsLIBPlus.Memory;
using MessagePack;
using ProtoBuf;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CommsLIBPlus.Communications.FrameWrappers.ProtoBuf
{
    public class ProtoBuffFrameWrapperOld<T> : FrameWrapperBase<T>, IDisposable
    {
        #region logger
        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        #endregion

        // Task related
        private Task readerTask;
        private CancellationTokenSource cancellationTokenSource;
        private CancellationToken cancellationToken;
        private bool started = false;

        // Length prefix related
        private int currentLength = 0;
        private LengthPrefixFrameBufferWriter writer;

        public ProtoBuffFrameWrapperOld() : base(false)
        {
            writer = new LengthPrefixFrameBufferWriter();
        }

        public override void Start()
        {
            cancellationTokenSource = new CancellationTokenSource();
            cancellationToken = cancellationTokenSource.Token;

            readerTask = Task.Run(async () => TaskReaderWorker());
            started = true;
        }

        public override async Task Stop()
        {
            if (!started)
                return;

            cancellationTokenSource.Cancel();

            await readerTask;
        }

        private async ValueTask TaskReaderWorker()
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && PipeDataReader != null)
                {
                    ReadResult result = await PipeDataReader.ReadAsync();
                    ReadOnlySequence<byte> buffer = result.Buffer;

                    // We are looking for Length
                    if (currentLength == 0)
                    {
                        if (buffer.Length >= 4) // 4 bytes
                        {
                            SequencePosition pos = GetFrameLength(buffer, out currentLength);
                            PipeDataReader.AdvanceTo(pos, pos);
                            continue;
                        }
                    }
                    else if (buffer.Length >= currentLength)
                    {
                        // Parse actual mensaje if enough data
                        T message = Serializer.Deserialize<T>(buffer.Slice(0, currentLength));
                        var bytesUsed = buffer.GetPosition(currentLength);
                        PipeDataReader.AdvanceTo(bytesUsed, bytesUsed);
                    }
                    else
                        PipeDataReader.AdvanceTo(buffer.Start, buffer.End);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Error while parsing");
            }
        }

        private SequencePosition GetFrameLength(ReadOnlySequence<byte> buffer, out int length)
        {
            if (buffer.Length < 4)
                throw new ArgumentException();

            var seqReader = new SequenceReader<byte>(buffer);

            if (seqReader.TryReadLittleEndian(out length))
            {
                seqReader.Advance(4);
                return seqReader.Position;
            }
            else
                throw new ArgumentException();
        }

        public override void AddBytes(ReadOnlyMemory<byte> data)
        {
            // Do nothing. Using pipes
        }

        public override ReadOnlyMemory<byte> Data2Bytes(T data)
        {
            writer.Reset();
            Serializer.Serialize(writer, data);

            return writer.GetFrameWithLengthPrefix();
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    writer.Dispose();
                    cancellationTokenSource.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~ProtoBuffFrameWrapper() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }


        #endregion


    }
}

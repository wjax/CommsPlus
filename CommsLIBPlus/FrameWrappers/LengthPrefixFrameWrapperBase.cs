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

namespace CommsLIBPlus.Communications.FrameWrappers
{
    public abstract class LengthPrefixFrameWrapperBase<T> : FrameWrapperBase<T>, IDisposable
    {
        #region logger
        private NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        #endregion

        // Task related
        private Task readerTask;
        private CancellationTokenSource cancellationTokenSource;
        private CancellationToken cancellationToken;
        private bool started = false;

        // Length prefix related
        private int currentLength = 0;
        private LengthPrefixFrameBufferWriter writer;

        // Serialization
        public abstract Action<IBufferWriter<byte>, T> serializer { get; }
        public abstract Func<ReadOnlySequence<byte>, T> deserializer { get; }

        // Pipe for reading
        private Pipe pipe;
        private PipeReader pipeReader;
        private PipeWriter pipeWriter;


        public LengthPrefixFrameWrapperBase(bool _useThreadPool = false) : base(_useThreadPool)
        {
            writer = new LengthPrefixFrameBufferWriter();

            pipe = new Pipe();
            pipeReader = pipe.Reader;
            pipeWriter = pipe.Writer;
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

            pipeReader.Complete();
            pipeWriter.Complete();
            pipe.Reset();

            started = false;
        }

        private async ValueTask TaskReaderWorker()
        {
            while (!cancellationToken.IsCancellationRequested && pipeReader != null)
            {
                ReadResult result = await pipeReader.ReadAsync(cancellationToken);
                if (result.IsCompleted || result.IsCanceled || result.Buffer.Length == 0)
                    continue;

                ReadOnlySequence<byte> buffer = result.Buffer;

                // We are looking for Length
                if (currentLength == 0)
                {
                    if (buffer.Length >= 4) // 4 bytes
                    {
                        SequencePosition pos = GetFrameLength(buffer, out currentLength);
                        pipeReader.AdvanceTo(pos, pos);
                        continue;
                    }
                }
                else if (buffer.Length >= currentLength)
                {
                    // Parse actual mensaje if enough data
                    try { 
                        T message = deserializer(buffer.Slice(0, currentLength));
                        _ = FireEvent(message);
                    } catch (Exception e) { logger.Error(e, "Error while parsing"); }
                    
                    var bytesUsed = buffer.GetPosition(currentLength);
                    pipeReader.AdvanceTo(bytesUsed, bytesUsed);
                    currentLength = 0;
                }
                else
                    pipeReader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        private SequencePosition GetFrameLength(ReadOnlySequence<byte> buffer, out int length)
        {
            if (buffer.Length < 4)
                throw new ArgumentException();

            var seqReader = new SequenceReader<byte>(buffer);

            if (seqReader.TryReadLittleEndian(out length))
                return seqReader.Position;
            else
                throw new ArgumentException();
        }

        public override void AddBytes(ReadOnlyMemory<byte> data)
        {
            try
            {
                _ = pipeWriter.WriteAsync(data, cancellationToken);
            }
            catch (Exception e) 
            {
                logger.Error(e, "Pipe writer error while Adding bytes");
            }
        }

        public override ReadOnlyMemory<byte> Data2Bytes(T data)
        {
            writer.Reset();
            serializer(writer, data);

            return writer.GetFrameWithLengthPrefix();
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected override void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    cancellationTokenSource.Cancel();
                    readerTask.Wait();
                    pipeReader.Complete();
                    pipeWriter.Complete();

                    writer.Dispose();
                    cancellationTokenSource.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.
                logger = null;

                disposedValue = true;
            }

            base.Dispose(disposing);
        }      
        #endregion


    }
}

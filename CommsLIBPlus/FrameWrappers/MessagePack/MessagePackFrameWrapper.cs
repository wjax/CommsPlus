using CommsLIBPlus.Communications.FrameWrappers;
using MessagePack;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace CommsLIBPlus.FrameWrappers.MessagePack
{
    public class MessagePackFrameWrapper<T> : LengthPrefixFrameWrapperBase<T>
    {
        public MessagePackFrameWrapper(bool _useThreadPool) : base(_useThreadPool)
        {

        }

        public override Action<IBufferWriter<byte>, T> serializer => 
            (writer, message) => MessagePackSerializer.Typeless.Serialize(writer, message);

        public override Func<ReadOnlySequence<byte>, T> deserializer =>
            (buffer) => (T)MessagePackSerializer.Typeless.Deserialize(buffer);
    }
}

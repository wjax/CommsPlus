using CommsLIBPlus.Communications.FrameWrappers;
using ProtoBuf;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace CommsLIBPlus.FrameWrappers.ProtoBuf
{
    public class ProtobufFrameWrapper<T> : LengthPrefixFrameWrapperBase<T>
    {
        public ProtobufFrameWrapper(bool _useThreadPool) : base (_useThreadPool)
        {

        }

        public override Func<ReadOnlySequence<byte>, T> deserializer { get => 
                (buffer) => Serializer.Deserialize<T>(buffer); }
        public override Action<IBufferWriter<byte>, T> serializer { get => 
                (writer, data) => Serializer.Serialize(writer, data);}
    }
}

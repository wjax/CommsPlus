using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;

namespace DataModel
{
    [ProtoInclude(100, typeof(ChildMessage1))]
    [ProtoContract, Serializable]
    public abstract class MessageBase
    {
        [ProtoMember(1)]
        public int SeqNumber;

        public MessageBase()
        {

        }
    }
}

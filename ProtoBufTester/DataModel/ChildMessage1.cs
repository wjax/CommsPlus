using DataModel.Payloads;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Text;

namespace DataModel
{
    [ProtoContract, Serializable]
    public class ChildMessage1 : MessageBase
    {
        [ProtoMember(1)]
        public string Name;

        [ProtoMember(2)]
        public DictionaryPayload Payload;

        public ChildMessage1() : base()
        {

        }

    }
}

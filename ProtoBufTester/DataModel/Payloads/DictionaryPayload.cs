using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Text;

namespace DataModel.Payloads
{
    [ProtoContract]
    public class DictionaryPayload
    {
        [ProtoMember (1)]
        public Dictionary<string, string> DataDictionary;

        public DictionaryPayload()
        {
            DataDictionary = new Dictionary<string, string>();
            DataDictionary.Add("Jesus", "Rocio");
        }
    }
}

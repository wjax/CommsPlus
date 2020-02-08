using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommsLIBPlus.Base;
using CommsLIBPlus.Communications;
using CommsLIBPlus.Communications.FrameWrappers;
using CommsLIBPlus.FrameWrappers.ProtoBuf;
using DataModel;
using DataModel.Payloads;

namespace ProtobufClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Hello Client!");

            // Crear un Framewrapper. En este casi mi librería ya te da uno hecho para Protobuff que usa Serializer.SerializeWithLengthPrefix(memoryStreamTX, data, PrefixStyle.Base128);
            FrameWrapperBase<MessageBase> frameWrapper1 = new ProtobufFrameWrapper<MessageBase>(true);
            // Crear un TCPCommunicator al que le pasas el framewrapper que ha de usar
            IPSocketCommunicator<MessageBase> client1 = new IPSocketCommunicator<MessageBase>(frameWrapper1);
            // Creas una Uri con el Peer al que quieres conectar
            ConnUri uri1 = new ConnUri("tcp://127.0.0.1:1111");
            // Inicializas el Communicator con la Uri, si quieres q sea persistente (reconecte), el ID, y alguna cosita más
            client1.Init(uri1, true, "CLIENT1", 10000);

            // Te suscribes a los eventos de conexión del communicator y de frame (objeto) disponible del framewrapper
            frameWrapper1.FrameAvailableEvent += OnMessage1;
            client1.ConnectionStateEvent += OnConnection1;
            client1.DataReadySyncEvent += OnRawSyncData;
            client1.DataReadyAsyncEvent += OnRawAsyncData;

            // inicias todo
            client1.Start();

            while(true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));

                ChildMessage1 msg = new ChildMessage1() { Name = DateTime.Now.ToShortTimeString() };

                //TEST
                Dictionary<string, string> payload = new Dictionary<string, string>();
                payload.Add("Jesus", "Roci");
                msg.Payload = new DictionaryPayload { DataDictionary = payload };

                _ = client1.SendAsync(msg);

                //while (true)
                //    await Task.Delay(TimeSpan.FromSeconds(1));
            }

        }

        private static void OnRawAsyncData(string ip, int port, long time, System.Buffers.IMemoryOwner<byte> data, string ID, uint ipuint)
        {
            using (data)
            {
                Console.WriteLine($"Data Received ASYNC: {data.Memory}");
            }
        }

        private static void OnRawSyncData(string ip, int port, long time, ReadOnlyMemory<byte> data, string ID, uint ipuint)
        {
            Console.WriteLine($"Data Received SYNC: {data}");
        }

        private static void OnConnection1(string ID, ConnUri uri, bool connected)
        {
            Console.WriteLine($"Client connected: {connected.ToString()}");
        }

        private static void OnMessage1(string ID, MessageBase payload)
        {
            Console.WriteLine($"Received Message from {ID}");
        }
    }
}

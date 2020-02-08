using CommsLIBPlus.Comms;
using CommsLIBPlus.FrameWrappers.ProtoBuf;
using DataModel;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProtobufServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Hello Server!");

            TCPSmartServer<ProtobufFrameWrapper<MessageBase>, MessageBase> server = new TCPSmartServer<ProtobufFrameWrapper<MessageBase>, MessageBase>(1111, () => new ProtobufFrameWrapper<MessageBase>(false));

            server.ConnectionStateEvent += OnConnection;
            server.FrameReadyEvent += OnFrame;

            server.Start();

            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));

                ChildMessage1 msg = new ChildMessage1() { Name = DateTime.Now.ToLongTimeString() };
                server.Send2All(msg);

                if (Console.ReadKey().Key == ConsoleKey.Enter)
                    break;
            }

            await server.Stop();
        }

        private static void OnFrame(MessageBase message, string ID)
        {
            Console.WriteLine($"Frame received from {ID}: {(message as ChildMessage1).Name}");
        }

        private static void OnConnection(string SourceID, bool connected)
        {
            Console.WriteLine($"Server | Connection from {SourceID} : {connected}");
        }
    }
}

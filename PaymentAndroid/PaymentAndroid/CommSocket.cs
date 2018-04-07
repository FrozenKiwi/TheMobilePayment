using NLog;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace PaymentAndroid
{
    class CommSocket : IDisposable
    {
        readonly ClientWebSocket client = new ClientWebSocket();
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly BlockingCollection<byte[]> responseQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());

        Task responseTask;

        Logger logger = NLog.LogManager.GetCurrentClassLogger();


        public bool IsConnected => client.State == WebSocketState.Open;

        public CommSocket()
        {
        }


        void IDisposable.Dispose()
        {
            logger.Trace("Disconnecting...");
            cts.Cancel();
            // wait for things to be cleaned up.
            client.Dispose();
            logger.Trace("Disconnected");

        }

        public async Task ConnectToServerAsync()
        {
            logger.Trace("Connecting...");
            await client.ConnectAsync(new Uri("ws://192.222.141.84:1379"), cts.Token);
            logger.Trace("Connected");

            responseTask = await Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    WebSocketReceiveResult result;
                    byte[] buff = new byte[4096];
                    ArraySegment<byte> message = new ArraySegment<byte>(buff);
                    do
                    {
                        result = await client.ReceiveAsync(message, cts.Token);
                        var messageBytes = message.Skip(message.Offset).Take(result.Count).ToArray();
                        responseQueue.Add(messageBytes);
                    } while (!result.EndOfMessage);
                }
            }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        bool WaitConnected(int timeout)
        {
            // Spin-lock waiting until timeout for connection
            int counter = 0;
            while (!IsConnected)
            {
                if ((counter++ * 10) > timeout)
                    break;
                Thread.Sleep(10);
            }
            return IsConnected;
        }

        public async Task<bool> SendAsync(byte[] message)
        {
            if (!WaitConnected(1000))
            {
                logger.Warn("Not Connected after timeout!");
                return false;
            }

            ArraySegment<byte> segment = new ArraySegment<byte>(message);
            await client.SendAsync(segment, WebSocketMessageType.Binary, true, cts.Token);
            return true;
        }

        public async Task<byte[]> SendRecieve(byte[] command)
        {
            logger.Trace("Sending {0}", BitConverter.ToString(command));
            if (await SendAsync(command))
            {
                var response = responseQueue.Take(cts.Token);

                var ERROR = System.Text.Encoding.ASCII.GetBytes("ERROR");
                if (ERROR.SequenceEqual(response))
                {
                    logger.Error("Response SERVER ERROR!");
                }
                else
                    logger.Trace("Response {0}", BitConverter.ToString(command));


                return response;
            }
            return null;
        }
    }
}
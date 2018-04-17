using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

using NLog;
using PCSC;
using PCSC.Iso7816;

namespace PaymentServer
{
    class Program
    {
        private class Transaction : IDisposable
        {
            private IsoReader _reader;
            private IsoReader Reader
            {
                get
                {
                    if (_reader == null)
                    {
                        var contextFactory = ContextFactory.Instance;
                        Context = contextFactory.Establish(SCardScope.System);

                        var readerNames = Context.GetReaders();
                        if (NoReaderFound(readerNames))
                        {
                            Console.WriteLine("You need at least one reader in order to run this example.");
                            Console.ReadKey();
                            return null;
                        }

                        var name = ChooseReader(readerNames);
                        if (name == null)
                        {
                            return null;
                        }

                        _reader = new IsoReader(
                            context: Context,
                            readerName: name,
                            mode: SCardShareMode.Shared,
                            protocol: SCardProtocol.Any,
                            releaseContextOnDispose: false);

                        logger.Info("Transaction Started: on {0} ", _reader.ReaderName);
                    }
                    return _reader;
                }
            }
            private ISCardContext Context;

            Logger logger = NLog.LogManager.GetCurrentClassLogger();

            public Transaction()
            {
            }

            public void Dispose()
            {
                if (_reader != null)
                {
                    logger.Trace("Disposing Reader");
                    _reader?.Dispose();
                }
            }

            public Response SendCommand(CommandApdu apdu, String command)
            {
                logger.Trace("Sending Command {0}: {1}", command, BitConverter.ToString(apdu.ToArray()));

                var response = Reader.Transmit(apdu);

                logger.Trace("SW1 SW2 = {0:X2} {1:X2}", response.SW1, response.SW2);

                if (!response.HasData)
                {
                    logger.Trace("No data. (Card does not understand \"{0}\")", command);
                    return null;
                }
                else
                {
                    var resp = response.GetData();
                    var chars = System.Text.Encoding.UTF8.GetString(resp);

                    string asString = "";
                    foreach (char ch in chars)
                    {
                        // Skip that annoying beep
                        if (ch != 0x07)
                            asString += ch;
                    }
                    logger.Trace("Response: \n  {0}\n  {1}", BitConverter.ToString(resp), asString);
                }
                return response;
            }

            public Response SendCommand(byte[] data, String origin)
            {
                // We could simply pass the data directly through by 
                // using the lower-level API, but it appears that the
                // reader/apdu combo provided by the PCSC library does
                // a bit of additional work on reading to ensure we
                // interface with the card correctly, so we route through it
                bool hasData = data.Length > 5;
                IsoCase commandCase = hasData ? IsoCase.Case4Short : IsoCase.Case2Short;

                CommandApdu apdu = new CommandApdu(commandCase, Reader.ActiveProtocol);
                apdu.CLA = data[0];
                apdu.Instruction = (InstructionCode)data[1];
                apdu.P1 = data[2];
                apdu.P2 = data[3];

                if (hasData)
                {
                    // TODO!!! The skipped byte is the Lc byte.  This field
                    // may actually be longer than 255 though, in which case
                    // we may need multiple bytes
                    byte[] buffer = new byte[data.Length - 5];
                    Buffer.BlockCopy(data, 5, buffer, 0, data.Length - 5);
                    apdu.Data = buffer;
                }

                // For validation, convert back to byte array, and check equality
                var newArray = apdu.ToArray();
                if (!newArray.SequenceEqual(data))
                {
                    logger.Error("Reconstructing APDU Failed! \n  Orig={0}\n  Recon={1}", data, newArray);
                    // TODO: return some sort of error message
                }
                return SendCommand(apdu, origin);
            }

            // Later, we might support multiple cards
            private static string ChooseReader(IList<string> readerNames)
            {
                if (readerNames.Count == 1)
                    return readerNames[0];
                return null;
            }

            private static bool NoReaderFound(ICollection<string> readerNames)
            {
                return readerNames == null || readerNames.Count < 1;
            }
        }

        //----------------------------------------------------------------------------

        private static Logger logger = LogManager.GetLogger("Main");

        private static byte[] GetResponse(byte[] data, Transaction currentTransaction, string ident)
        {

            var response = currentTransaction.SendCommand(data, ident);
            // return a reconstructed buffer from all
            // contained apdu's in this response
            if (response != null)
            {
                byte[] swbytes = { response.SW1, response.SW2 };
                return response.GetData().Concat(swbytes).ToArray();
            }
            return System.Text.Encoding.ASCII.GetBytes("ERROR");
        }

        // Incoming data from the client.  
        //public static string data = null;

        public static void StartListening()
        {
            // Data buffer for incoming data.  
            //byte[] bytes = new Byte[1024];

            // Establish the local endpoint for the socket.  
            // Dns.GetHostName returns the name of the   
            // host running the application.  
            //IPHostEntry ipHostInfo = Dns.GetHostEntry(Dns.GetHostName());

            //var ipAddress = new IPAddress(new byte[] { 192, 222, 141, 84 });
            //IPEndPoint localEndPoint = new IPEndPoint(ipAddress, 1379);

            // Bind the socket to the local endpoint and   
            // listen for incoming connections.  
            try
            {
                // Create a TCP/IP socket.  
                Socket listener = new Socket(IPAddress.Any.AddressFamily,
                    SocketType.Stream, ProtocolType.Tcp);

                IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Any, 13790);
                listener.Bind(localEndPoint);
                listener.Listen(10);

                // Start listening for connections.  
                while (true)
                {
                    logger.Info("Waiting for a connection...");
                    // Program is suspended while waiting for an incoming connection.  
                    Socket handler = listener.Accept();
                    using (Transaction thisTransaction = new Transaction())
                    {
                        logger.Info("Connection accepted from {0}", handler.RemoteEndPoint);
                        // Wait a max of 2 seconds before dropping this connection
                        handler.ReceiveTimeout = 5000;

                        // An incoming connection needs to be processed.  
                        try
                        {
                            while (true)
                            {
                                var bytes = new byte[1024];
                                int bytesRec = handler.Receive(bytes);
                                if (bytesRec == 0)
                                {
                                    logger.Info("Transaction Complete");
                                    break;
                                }

                                var input = bytes.Take(bytesRec).ToArray();
                                var response = GetResponse(input, thisTransaction, "TODO");
                                handler.Send(response);
                            }
                        }
                        catch(SocketException e)
                        {
                            logger.Warn("Transaction Timeout");
                        }
                    }
                    handler.Shutdown(SocketShutdown.Both);
                    handler.Close();
                }

            }
            catch (Exception e)
            {
                logger.Error(e, "Whahappen...");
            }

            Console.WriteLine("\nPress ENTER to continue...");
            Console.Read();
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            StartListening();
            //var allSockets = new List<IWebSocketConnection>();
            //var server = new WebSocketServer("ws://0.0.0.0:1379");
            //// Each socket has a transaction attached
            //var socketTransactions = new ConcurrentDictionary<IWebSocketConnection, Transaction>();

            //server.Start(socket =>
            //{
            //    socket.OnOpen = () =>
            //    {
            //        logger.Trace("Open!");
            //        allSockets.Add(socket);
            //    };
            //    socket.OnClose = () =>
            //    {
            //        logger.Trace("Close!");
            //        allSockets.Remove(socket);

            //        Transaction finishedTransaction;
            //        socketTransactions.TryRemove(socket, out finishedTransaction);
            //        finishedTransaction?.Dispose();

            //    };
            //    socket.OnMessage = message =>
            //    {
            //        logger.Info("Message: {0}", message);
            //    };
            //    socket.OnBinary = data =>
            //    {
            //        logger.Trace("Binary Received");


            //    };
            //});


            //var input = Console.ReadLine();
            //while (input != "exit")
            //{
            //    foreach (var socket in allSockets.ToList())
            //    {
            //        socket.Send(input);
            //    }
            //    input = Console.ReadLine();
            //}
        }
    }
}

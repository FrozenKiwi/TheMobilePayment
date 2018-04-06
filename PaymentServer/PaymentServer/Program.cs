using System;
using System.Collections.Generic;
using System.Linq;

using Fleck;
using PCSC;
using PCSC.Iso7816;

namespace PaymentServer
{
    class Program
    {
        private class Transaction
        {
            private IsoReader reader;
            private ISCardContext context;

            public Transaction()
            {
                var contextFactory = ContextFactory.Instance;
                context = contextFactory.Establish(SCardScope.System);

                var readerNames = context.GetReaders();
                if (NoReaderFound(readerNames))
                {
                    Console.WriteLine("You need at least one reader in order to run this example.");
                    Console.ReadKey();
                    return;
                }

                var name = ChooseReader(readerNames);
                if (name == null)
                {
                    return;
                }

                reader = new IsoReader(
                    context: context,
                    readerName: name,
                    mode: SCardShareMode.Exclusive,
                    protocol: SCardProtocol.Any,
                    releaseContextOnDispose: false);
            }

            public byte[] SendCommand(CommandApdu apdu, String command)
            {
                Console.WriteLine("Sending First {0}: {1}",
                    command, BitConverter.ToString(apdu.ToArray()));

                var response = reader.Transmit(apdu);

                Console.WriteLine("SW1 SW2 = {0:X2} {1:X2}",
                    response.SW1, response.SW2);

                if (!response.HasData)
                {
                    Console.WriteLine("No data. (Card does not understand \"{0}\")", command);
                    return null;
                }
                else
                {
                    var resp = response.GetData();
                    var chars = System.Text.Encoding.UTF8.GetString(resp);

                    Console.WriteLine("Response: {0}", BitConverter.ToString(resp), chars);
                    foreach (char ch in chars)
                    {
                        // Skip that annoying beep
                        if (ch != 0x07)
                            Console.Write(ch);
                    }
                    Console.Write("\n");
                    return resp;
                }
            }

            public byte[] SendCommand(byte[] data, String origin)
            {
                // We could simply pass the data directly through by 
                // using the lower-level API, but it appears that the
                // reader/apdu combo provided by the PCSC library does
                // a bit of additional work on reading to ensure we
                // interface with the card correctly, so we route through it
                CommandApdu apdu = new CommandApdu(IsoCase.Case4Short, reader.ActiveProtocol);
                apdu.CLA = data[0];
                apdu.Instruction = (InstructionCode)data[1];
                apdu.P1 = data[2];
                apdu.P2 = data[3];

                if (data.Length > 4)
                {
                    Buffer.BlockCopy(data, 5, apdu.Data, 0, data.Length - 4);
                }

                // For validation, convert back to byte array, and check equality
                var newArray = apdu.ToArray();
                if (!newArray.SequenceEqual(data))
                {
                    String errormsg = String.Format("Reconstructing APDU Failed! \n  Orig={0}\n  Recon={1}", data, newArray);
                    FleckLog.Error(errormsg);
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

        static void Main(string[] args)
        {
            FleckLog.Level = LogLevel.Debug;
            var allSockets = new List<IWebSocketConnection>();
            var server = new WebSocketServer("ws://0.0.0.0:1379");
            // Each socket has a transaction attached
            var socketTransactions = new Dictionary<IWebSocketConnection, Transaction>();

            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    FleckLog.Debug("Open!");
                    Console.WriteLine("Open!");
                    allSockets.Add(socket);

                    socketTransactions.Add(socket, new Transaction());
                };
                socket.OnClose = () =>
                {
                    FleckLog.Debug("Close!");
                    Console.WriteLine("Close!");
                    allSockets.Remove(socket);

                    socketTransactions.Remove(socket);
                };
                socket.OnMessage = message =>
                {

                    Console.WriteLine(message);
                    
                };
                socket.OnBinary = data =>
                {
                    Transaction currentTransaction;
                    if (socketTransactions.TryGetValue(socket, out currentTransaction))
                    {
                        var resp = currentTransaction.SendCommand(data, socket.ConnectionInfo.Origin);
                        socket.Send(resp);
                    }
                };
            });


            var input = Console.ReadLine();
            while (input != "exit")
            {
                foreach (var socket in allSockets.ToList())
                {
                    socket.Send(input);
                }
                input = Console.ReadLine();
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

using NLog;
using PCSC;
using PCSC.Iso7816;
using BerTlv;

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

            public CommandApdu InitApdu(bool hasData)
            {
                IsoCase commandCase = hasData ? IsoCase.Case4Short : IsoCase.Case2Short;

                return new CommandApdu(commandCase, Reader.ActiveProtocol);
            }

            public Response SendCommand(byte[] data, String origin)
            {
                // We could simply pass the data directly through by 
                // using the lower-level API, but it appears that the
                // reader/apdu combo provided by the PCSC library does
                // a bit of additional work on reading to ensure we
                // interface with the card correctly, so we route through it
                bool hasData = data.Length > 5;
                CommandApdu apdu = InitApdu(hasData);
                apdu.CLA = data[0];
                apdu.Instruction = (InstructionCode)data[1];
                apdu.P1 = data[2];
                apdu.P2 = data[3];

                if (hasData)
                {
                    // TODO!!! The skipped byte is the Lc byte.  This field
                    // may actually be longer than 255 though, in which case
                    // we may need multiple bytes
                    byte dataLength = data[4];
                    apdu.Data = data.Skip(5).Take(dataLength).ToArray();
                }

                // For validation, convert back to byte array, and check equality
                // We do allow for a differing final byte (if it's 0) because
                // the library reconstruction does not add this byte (but
                // everything still seems to work)

                var newArray = apdu.ToArray();
                var dataLen = data.Length;
                if (data.Last() == 0)
                    dataLen = newArray.Length;
                if (!newArray.SequenceEqual(data.Take(dataLen)))
                {
                    logger.Error("Reconstructing APDU Failed! \n  Orig={0}\n  Recon={1}", BitConverter.ToString(data), BitConverter.ToString(newArray));
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

        private static byte[] FindValue(Tlv tlv, IEnumerator<string> tags)
        {
            if (tlv.HexTag == tags.Current)
            {
                if (!tags.MoveNext())
                    return tlv.Value;

                foreach (Tlv child in tlv.Children)
                {
                    var val = FindValue(child, tags);
                    if (val != null)
                        return val;
                }
            }
            return null;
        }

        private static byte[] FindValue(Tlv tlv, IEnumerable<string> tags)
        {
            var enumerator = tags.GetEnumerator();
            if (enumerator.MoveNext())
                return FindValue(tlv, enumerator);
            return null;
        }

        struct RecordAddress
        {
            public int SFI;
            public byte FromRecord;
            public byte ToRecord;
            public int OfflineAddress;
        }

        private static List<RecordAddress> ParseAddresses(byte[] data)
        {
            List<RecordAddress> results = new List<RecordAddress>();
            for (int idx = 0; idx < data.Length; idx += 4)
            {
                var newAddress = new RecordAddress()
                {
                    SFI             = data[idx + 0] >> 3,
                    FromRecord      = data[idx + 1],
                    ToRecord        = data[idx + 2],
                    OfflineAddress  = data[idx + 3],
                };
                results.Add(newAddress);
            }
            return results;
        }

        private static void BuildReadRecordApdu(CommandApdu command, RecordAddress address, byte idx)
        {
            command.CLA = 0;
            command.Instruction = InstructionCode.ReadRecord;
            command.P1 = idx;
            command.P2 = (byte)((address.SFI << 3) + 0x4);
        }
        
        private static void WarmUpCard(Transaction transaction)
        {
            logger.Info("Warming up card");


            // We perform initial, constant interactions in this phase
            // For now - hard-coded queries, ignore responses
            byte[] SEL_FILE = new byte[] { 0x00, 0xA4, 0x04, 0x00, 0x0E, 0x32, 0x50, 0x41, 0x59, 0x2E, 0x53, 0x59, 0x53, 0x2E, 0x44, 0x44, 0x46, 0x30, 0x31, 0x00 };
            var selResponse = GetResponse(SEL_FILE, transaction, "Select Payment");
            var tlvSelResponse = Tlv.ParseTlv(selResponse);


            var paymentApp = tlvSelResponse;
            var SelApp = transaction.InitApdu(true);
            SelApp.Instruction = InstructionCode.SelectFile;
            SelApp.P1 = 0x04; // read the first file (I think?)
            SelApp.Data = FindValue(tlvSelResponse.First(), new string[] { "6F", "A5", "BF0C", "61", "4F" });
            var appResponse = transaction.SendCommand(SelApp, "Select App");

            // Extract the PDOL
            var appTlv = Tlv.ParseTlv(appResponse.GetData());
            var pdolData = FindValue(appTlv.First(), new string[] { "6F", "A5", "9F38" });
            var pdolParsed = PDOL.ParsePDOL(pdolData);

            // Normally this would get sent back to the client to be filled by the terminal
            PDOL.FillWithDummyData(pdolParsed);

            var gpo = transaction.InitApdu(true);
            gpo.CLA = 0x80;
            gpo.Instruction = (InstructionCode)0xA8;
            gpo.Data = PDOL.GeneratePDOL(pdolParsed);
            var gpoResponse = transaction.SendCommand(gpo, "GPO");

            var gpoTlv = Tlv.ParseTlv(gpoResponse.GetData());
            var fileData = FindValue(gpoTlv.First(), new string[] { "77", "94" });

            var fileList = ParseAddresses(fileData);
            byte[] cdol = null;
            foreach(var file in fileList)
            {
                for (byte recordNum = file.FromRecord; recordNum <= file.ToRecord; recordNum++)
                {
                    var rr = transaction.InitApdu(false);
                    BuildReadRecordApdu(rr, file, recordNum);
                    var record = transaction.SendCommand(rr, "ReadRecord");
                    var rrtlv = Tlv.ParseTlv(record.GetData());
                    if (cdol == null)
                        cdol = FindValue(rrtlv.First(), new string[] { "70", "8C" });
                }
            }

            if (cdol != null)
            {
                var cdolParsed = PDOL.ParsePDOL(cdol);

                PDOL.FillWithDummyData(cdolParsed);

                var GenerateCrypto = transaction.InitApdu(true);
                GenerateCrypto.CLA = 0x80;
                GenerateCrypto.Instruction = (InstructionCode)0xAE;
                GenerateCrypto.P1 = 0x80;
                GenerateCrypto.Data = PDOL.GenerateCDOL(cdolParsed);

                var fuckinAye = transaction.SendCommand(GenerateCrypto, "GenerateCrypto");
                var faBytes = fuckinAye.GetData();
            }

            //if (commandApdu.SequenceEqual(SEL_FILE))
            //    return new byte[] { 0x6F, 0x2C, 0x84, 0x0E, 0x32, 0x50, 0x41, 0x59, 0x2E, 0x53, 0x59, 0x53, 0x2E, 0x44, 0x44, 0x46, 0x30, 0x31, 0xA5, 0x1A, 0xBF, 0x0C, 0x17, 0x61, 0x15, 0x4F, 0x07, 0xA0, 0x00, 0x00, 0x02, 0x77, 0x10, 0x10, 0x50, 0x07, 0x49, 0x6E, 0x74, 0x65, 0x72, 0x61, 0x63, 0x87, 0x01, 0x01, 0x90, 0x00 };

            //byte[] SEL_INTERAC = new byte[] { 0x00, 0xA4, 0x04, 0x00, 0x07, 0xA0, 0x00, 0x00, 0x02, 0x77, 0x10, 0x10, 0x00 };
            ////transaction.SendCommand(SEL_INTERAC, transaction, "Select Interac");
            ////var tlvSelResponse = BerTlv.Tlv.ParseTlv(selResponse);

            //byte[] GET_ATC = new byte[] { 0x80, 0xCA, 0x9F, 0x36, 0x00 };
            //var r1 = GetResponse(GET_ATC, transaction, "Get App Transaction Counter");

            //byte[] GET_OATC = new byte[] { 0x80, 0xCA, 0x9F, 0x13, 0x00 };
            //var r2 = GetResponse(GET_OATC, transaction, "Get App Transaction Counter (Online)");

            //byte[] GET_PINC = new byte[] { 0x80, 0xCA, 0x9F, 0x17, 0x00 };
            //var r3 = GetResponse(GET_PINC, transaction, "Get Pin Tries");

            //byte[] GET_LOG = new byte[] { 0x80, 0xCA, 0x9F, 0x4F, 0x00 };
            //var r4 = GetResponse(GET_LOG, transaction, "Get Log");


            ////if (commandApdu.SequenceEqual(SEL_INTERAC))
            ////    return new byte[] { 0x6F, 0x31, 0x84, 0x07, 0xA0, 0x00, 0x00, 0x02, 0x77, 0x10, 0x10, 0xA5, 0x26, 0x50, 0x07, 0x49, 0x6E, 0x74, 0x65, 0x72, 0x61, 0x63, 0x87, 0x01, 0x01, 0x5F, 0x2D, 0x04, 0x65, 0x6E, 0x66, 0x72, 0xBF, 0x0C, 0x10, 0x9F, 0x4D, 0x02, 0x0B, 0x0A, 0x5F, 0x56, 0x03, 0x43, 0x41, 0x4E, 0xDF, 0x62, 0x02, 0x80, 0x80, 0x90, 0x00 };

            //var GPO = new byte[] { 0x80, 0xA8, 0x00, 0x00, 0x02, 0x83, 0x00, 0x00 };
            //GetResponse(GPO, transaction, "Warmup");

            //if (GPO.SequenceEqual(commandApdu))
            //    return new byte[] { 0x77, 0x0A, 0x82, 0x02, 0x18, 0x00, 0x94, 0x04, 0x08, 0x01, 0x02, 0x00, 0x90, 0x00 };
        }

        // Incoming data from the client.  
        //public static string data = null;

        public static void StartListening()
        {
            // Data buffer for incoming data.  
            //byte[] bytes = new Byte[1024];

            {
                Transaction thisTransaction = new Transaction();
                WarmUpCard(thisTransaction);

                return;
            }

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
                            var bytes = new byte[1024];
                            int bytesRec = handler.Receive(bytes);

                            // If our first byte is 
                            if (bytes[0] == 0x00 && bytes[1] == 0xB2)
                                WarmUpCard(thisTransaction);

                            while (true)
                            {

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
                        catch(SocketException )
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

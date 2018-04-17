using NLog;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
//using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Java.Net;

namespace PaymentAndroid
{
    class CommSocket : IDisposable
    {
        //const string ServerAddress = "192.222.141.84";
        const string ServerAddress = "192.222.141.84";
        const int ServerPort = 13790;

        Socket socket;
        Logger logger = NLog.LogManager.GetCurrentClassLogger();


        public bool IsConnected => socket?.IsConnected == true;

        public CommSocket()
        {
        }


        void IDisposable.Dispose()
        {
            logger.Trace("Disconnecting...");
            // Release the socket.  
            if (IsConnected)
            {
                socket.Close();
            }
            logger.Trace("Disconnected");

        }

        public bool ConnectToServer()
        {
            logger.Trace("Connecting...");
            // Connect the socket to the remote endpoint. Catch any errors.  
            try
            {
                socket = new Socket(ServerAddress, ServerPort);
                logger.Info("Socket connected to {0}", ServerAddress);
                return true;
            }
            catch (Java.IO.IOException e)
            {
                logger.Error("Couldn't Connect- (IO) {0}: {1}", e.ToString(), e.Message);
                logger.Trace(e.StackTrace);
            }
            catch(Exception e)
            {
                logger.Error(e, "Couldn't Connect - {0}: {1}", e.ToString(), e.Message);
                logger.Trace(e.StackTrace);
            }
            return false;
        }


        public byte[] SendRecieve(byte[] command)
        {
            logger.Info("Sending");
            if (IsConnected)
            {
                socket.OutputStream.Write(command, 0, command.Length);
                logger.Trace("Send complete");

                byte[] response = new byte[1024];
                int bytesRec = socket.InputStream.Read(response, 0, 1024);

                var ERROR = System.Text.Encoding.ASCII.GetBytes("ERROR");
                if (ERROR.SequenceEqual(response))
                {
                    logger.Error("Response SERVER ERROR!");
                }
                return response.Take(bytesRec).ToArray();
            }
            return null;
        }
    }
}
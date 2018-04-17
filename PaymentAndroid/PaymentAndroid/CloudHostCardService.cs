using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Nfc.CardEmulators;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using NLog;

namespace PaymentAndroid
{
    [Service (Exported= true, 
        Name="com.PaymentAndroid.PaymentAndroid.CloudHostApduService",
        Permission="android.permission.BIND_NFC_SERVICE")]
    [IntentFilter(new[] { "android.nfc.cardemulation.action.HOST_APDU_SERVICE" })]
    [MetaData("android.nfc.cardemulation.host_apdu_service", Resource= "@xml/apduservice" )]
    class CloudHostCardService : HostApduService
    {
        private CommSocket socket;
        private System.Diagnostics.Stopwatch stopwatch;
        private Logger logger = LogManager.GetCurrentClassLogger();

        private bool IsTransacting = false;

        public override void OnDeactivated(DeactivationReason reason)
        {
            logger.Info("Deactivated: {0} after {1}ms", reason.ToString(), stopwatch.ElapsedMilliseconds);

            Android.Media.Stream amStream = Android.Media.Stream.Music;
            int iTonGeneratorVolume = 100;

            var toneG = new Android.Media.ToneGenerator(amStream, iTonGeneratorVolume);
            toneG.StartTone(Android.Media.Tone.PropBeep);

            (socket as IDisposable).Dispose();

            IsTransacting = false;
        }

        // When our service is started up, we ensure we have a communications channel open
        public override void OnCreate()
        {
            logger.Info("OnStart");
            // Ensure we initialize as early as possible 
            if (socket == null)
            {
                InitComms();
            }
            base.OnCreate();
        }


        public override byte[] ProcessCommandApdu(byte[] commandApdu, Bundle extras)
        {
            // Double check (remove once we have startup process sussed)
            if (socket == null)
            {
                logger.Warn(" -- DoubleInit on Service --");
                var vibrator = (Vibrator)GetSystemService(VibratorService);
                InitComms();
            }

            if (!IsTransacting)
            {
                var vibrator = (Vibrator)GetSystemService(VibratorService);
                NotifyTransacting(vibrator);
            }

            logger.Info("Recieved APDU: {0}", BitConverter.ToString(commandApdu));
            var response = SendApdu(commandApdu);
            logger.Info("Response: {0}", BitConverter.ToString(response));

            return response;
        }

        private byte[] SendApdu(byte[] commandApdu)
        {
            byte[] response = GetCachedResponse(commandApdu);
            if (response == null)
            {
                try
                {
                    response = socket.SendRecieve(commandApdu);
                }
                catch (Exception e)
                {
                    logger.Error("ProcessCommandApdu faulted: {0}", e.Message);
                    logger.Trace(e.StackTrace);
                }

            }
            return response;
        }

        internal void NotifyTransacting(Vibrator vibrator)
        {
            logger.Info("Init new Transaction");
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                vibrator.Vibrate(VibrationEffect.CreateOneShot(150, 100));
            }
            else
            {
                vibrator.Vibrate(150);
            }
            IsTransacting = true;
        }

        // Initialize communications, 
        internal void InitComms()
        {
            if (socket == null)
            {


                // We wish to time how long this whole operation takes.
                stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Allow networking on the main thread.
                StrictMode.ThreadPolicy policy = new StrictMode.ThreadPolicy.Builder().PermitAll().Build();
                StrictMode.SetThreadPolicy(policy);

                socket = new CommSocket();
                socket.ConnectToServer();
            }
        }

        private byte[] GetCachedResponse(byte[] commandApdu)
        {
            // The Command apdu may (or may not) contain
            // a trailing 0.  Our comparisons always contain it, but
            // so we limit their length to be the same as Command
            var commandLength = commandApdu.Length;

            // For now - hard-coded responses
            byte[] SEL_FILE = new byte[] { 0x00, 0xA4, 0x04, 0x00, 0x0E, 0x32, 0x50, 0x41, 0x59, 0x2E, 0x53, 0x59, 0x53, 0x2E, 0x44, 0x44, 0x46, 0x30, 0x31, 0x00 };
            
            if (commandApdu.SequenceEqual(SEL_FILE.Take(commandLength)))
                return new byte[] { 0x6F, 0x2C, 0x84, 0x0E, 0x32, 0x50, 0x41, 0x59, 0x2E, 0x53, 0x59, 0x53, 0x2E, 0x44, 0x44, 0x46, 0x30, 0x31, 0xA5, 0x1A, 0xBF, 0x0C, 0x17, 0x61, 0x15, 0x4F, 0x07, 0xA0, 0x00, 0x00, 0x02, 0x77, 0x10, 0x10, 0x50, 0x07, 0x49, 0x6E, 0x74, 0x65, 0x72, 0x61, 0x63, 0x87, 0x01, 0x01, 0x90, 0x00 };

            byte[] SEL_INTERAC = new byte[] { 0x00, 0xA4, 0x04, 0x00, 0x07, 0xA0, 0x00, 0x00, 0x02, 0x77, 0x10, 0x10, 0x00 };
            if (commandApdu.SequenceEqual(SEL_INTERAC.Take(commandLength)))
                return new byte[] { 0x6F, 0x31, 0x84, 0x07, 0xA0, 0x00, 0x00, 0x02, 0x77, 0x10, 0x10, 0xA5, 0x26, 0x50, 0x07, 0x49, 0x6E, 0x74, 0x65, 0x72, 0x61, 0x63, 0x87, 0x01, 0x01, 0x5F, 0x2D, 0x04, 0x65, 0x6E, 0x66, 0x72, 0xBF, 0x0C, 0x10, 0x9F, 0x4D, 0x02, 0x0B, 0x0A, 0x5F, 0x56, 0x03, 0x43, 0x41, 0x4E, 0xDF, 0x62, 0x02, 0x80, 0x80, 0x90, 0x00 };

            var GPO = new byte[] { 0x80, 0xA8, 0x00, 0x00, 0x02, 0x83, 0x00, 0x00 };
            if (commandApdu.SequenceEqual(GPO.Take(commandLength)))
                return new byte[] { 0x77, 0x0A, 0x82, 0x02, 0x18, 0x00, 0x94, 0x04, 0x08, 0x01, 0x02, 0x00, 0x90, 0x00 };

            return null;
        }

        public override void OnDestroy()
        {
            logger.Trace("OnDestroy");
        }

        // Converts a string generated by BitConverter.ToString(byte[]) back to a byte array
        //public static byte[] FromString(string str)
        //{
        //    string[] hexValuesSplit = str.Split('-');
        //    byte[] results = new byte[hexValuesSplit.Length];
        //    for (var i = 0; i < hexValuesSplit.Length; i++)
        //    {
        //        int hexbase = 16;
        //        int value = Int32.Parse(hexValuesSplit[i], hexbase);
        //        if (value > byte.MaxValue)
        //            return null;
        //        //throw new FormatException("Invalid hex value found in ")
        //        results[i] = (byte)value;
        //    }
        //    return results;
        //}
    }
}
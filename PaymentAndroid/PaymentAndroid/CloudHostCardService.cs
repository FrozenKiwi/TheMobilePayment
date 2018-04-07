using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

        Logger logger = NLog.LogManager.GetCurrentClassLogger();


        public override void OnDeactivated(Android.Nfc.CardEmulators.DeactivationReason reason)
        {
            logger.Trace("On Deactivated: {0}", reason.ToString());

            (socket as IDisposable).Dispose();
        }
        public override byte[] ProcessCommandApdu(byte[] commandApdu, Bundle extras)
        {
            logger.Trace("Received apdu: ", BitConverter.ToString(commandApdu));
            if (socket == null)
            {
                socket = new CommSocket();
                socket.ConnectToServerAsync().GetAwaiter().GetResult();
            }
            var result = socket.SendRecieve(commandApdu).GetAwaiter().GetResult();
            return result;
    }

        public override void OnCreate()
        {
        }

        public override void OnDestroy()
        {
            throw new NotImplementedException();
        }
    }
}
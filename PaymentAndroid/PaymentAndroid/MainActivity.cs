using Android.App;
using Android.Widget;
using Android.OS;
using System;
using PCSC.Iso7816;
using NLog;
using NLog.Targets;
using Android.Content;
using Android.Nfc;
using Android.Nfc.CardEmulators;
using System.Threading.Tasks;

namespace PaymentAndroid
{
    [Activity(Label = "PaymentAndroid", MainLauncher = true)]
    public class MainActivity : Activity
    {
        Logger logger = NLog.LogManager.GetCurrentClassLogger();

        [Target("ViewTarget")]
        public sealed class MyFirstTarget : NLog.Targets.TargetWithLayout
        {
            private readonly TextView console;
            private readonly Activity activity;
            public MyFirstTarget(TextView view, Activity a)
            {
                console = view;
                activity = a;
                console.MovementMethod = new Android.Text.Method.ScrollingMovementMethod();
            }

            protected override void Write(LogEventInfo logEvent)
            {
                string logMessage = this.Layout.Render(logEvent);
                activity.RunOnUiThread(() =>
                {
                    console.Text = console.Text + ("\n" + logMessage);
                });
            }
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.Main);

            Button button = FindViewById<Button>(Resource.Id.btnTest);

            var externalFolder = GetExternalFilesDir(null);

            var config = new NLog.Config.LoggingConfiguration();

            var logfile = new NLog.Targets.FileTarget() { FileName = externalFolder + "/afile.log", Name = "logfile" };
            config.LoggingRules.Add(new NLog.Config.LoggingRule("*", LogLevel.Info, logfile));

            var consoleView = FindViewById<TextView>(Resource.Id.resultsView);
            var consoleTarget = new MyFirstTarget(consoleView, this);
            consoleTarget.Layout = NLog.Layouts.Layout.FromString("${longdate} | ${message}");
            config.LoggingRules.Add(new NLog.Config.LoggingRule("*", LogLevel.Info, consoleTarget));

            LogManager.Configuration = config;

            logger.Trace("Logger Started");

            button.Click += delegate
            {
                TestTransaction();
            };

            CheckThisIsDefault();
        }

        private CardEmulation GetEmulation()
        {
            var nfcAdapter = NfcAdapter.GetDefaultAdapter(ApplicationContext);
            return CardEmulation.GetInstance(nfcAdapter);
        }
        private ComponentName ServiceName => new ComponentName(this, Java.Lang.Class.FromType(typeof(CloudHostCardService)).Name);
            
        protected override void OnResume()
        {
            var wasSet = GetEmulation().SetPreferredService(this, ServiceName);
            logger.Trace("On Resume: Set Default payment service {0}", wasSet);
            base.OnResume();
        }

        protected override void OnPause()
        {
            GetEmulation().UnsetPreferredService(this);
            base.OnPause();
        }

        private void CheckThisIsDefault()
        {
            // set default payment app
            var emulation = GetEmulation();

            var selMode = emulation.GetSelectionModeForCategory(CardEmulation.CategoryPayment);
            var aids = emulation.GetAidsForService(ServiceName, CardEmulation.CategoryPayment);
            var allowsFG = emulation.CategoryAllowsForegroundPreference(CardEmulation.CategoryPayment);

            if (!emulation.IsDefaultServiceForCategory(ServiceName, CardEmulation.CategoryPayment))
            {
                Intent intent = new Intent();
                intent.SetAction(CardEmulation.ActionChangeDefault);
                intent.PutExtra(CardEmulation.ExtraServiceComponent, ServiceName);
                intent.PutExtra(CardEmulation.ExtraCategory, CardEmulation.CategoryPayment);
                StartActivity(intent);
            }
        }

        private void TestTransaction()
        {
            logger.Info("---- Running Test Transaction ----");
            try
            {
                CloudHostCardService service = new CloudHostCardService();
                service.InitComms();

                // Explicitly init comms
                var vibrator = (Vibrator)GetSystemService(VibratorService);
                service.NotifyTransacting(vibrator);

                //using (CloudHostCardService service = new CloudHostCardService())
                {
                    logger.Trace("Sending SEL_FILE");
                    byte[] SEL_FILE = { 0x32, 0x50, 0x41, 0x59, 0x2E, 0x53, 0x59, 0x53, 0x2E, 0x44, 0x44, 0x46, 0x30, 0x31 };
                    var command = new CommandApdu(IsoCase.Case4Short, PCSC.SCardProtocol.T0)
                    {
                        CLA = 0x00, // Class
                        Instruction = InstructionCode.SelectFile,
                        P1 = 0x04, // Parameter 1
                        P2 = 0x00, // Parameter 2
                        Data = SEL_FILE // Select PPSE (2PAY.SYS.DDF01)
                    };
                    var data_sel_file = service.ProcessCommandApdu(command.ToArray(), null);

                    logger.Trace("Sending SEL_INTERAC");
                    byte[] SEL_INTERAC = { 0xA0, 0x00, 0x00, 0x02, 0x77, 0x10, 0x10 }; // ASCII for Interac
                    var data_sel_interac = service.ProcessCommandApdu(new CommandApdu(IsoCase.Case4Short, PCSC.SCardProtocol.T0)
                    {
                        CLA = 0x00, // Class
                        Instruction = InstructionCode.SelectFile,
                        P1 = 0x04, // Parameter 1
                        P2 = 0x00, // Parameter 2
                        Data = SEL_INTERAC // Select Interac file
                    }.ToArray(), null);

                    logger.Trace("Sending GPO");
                    //byte[] GPO = { 0x83, 0x13, 0xC0, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x01, 0x24, 0x01, 0x24, 0x82, 0x3D, 0xDE, 0x7A, 0x01 };
                    byte[] GPO = { 0x83, 0x00 };
                    var data_gpo = service.ProcessCommandApdu(new CommandApdu(IsoCase.Case4Short, PCSC.SCardProtocol.T0)
                    {
                        CLA = 0x80, // Class
                        Instruction = (InstructionCode)168,
                        P1 = 0x00, // Parameter 1
                        P2 = 0x00, // Parameter 2
                        Data = GPO // Get Processing Options
                    }.ToArray(), null);

                    logger.Trace("Sending RR1");
                    var data_rr1 = service.ProcessCommandApdu(new CommandApdu(IsoCase.Case2Short, PCSC.SCardProtocol.T0)
                    {
                        CLA = 0x00, // Class
                        Instruction = InstructionCode.ReadRecord,
                        P1 = 0x01, // Parameter 1
                        P2 = 0x0C, // Parameter 2
                    }.ToArray(), null);

                    logger.Trace("Sending RR2");
                    var data_rr2 = service.ProcessCommandApdu(new CommandApdu(IsoCase.Case2Short, PCSC.SCardProtocol.T0)
                    {
                        CLA = 0x00, // Class
                        Instruction = InstructionCode.ReadRecord,
                        P1 = 0x01, // Parameter 1
                        P2 = 0x14, // Parameter 2
                    }.ToArray(), null);

                    service.OnDeactivated(DeactivationReason.LinkLoss);
                    logger.Trace("Done");
                }
            }
            catch(Exception e)
            {
                logger.Error("Exception thrown: {0}", e.Message);
            }
        }
    }
}


using s3molib;
using System.Diagnostics;

namespace s3mo
{
    internal static class Program
    {
        public const string Version = "1.2.0";

        private static StreamWriter? _log;
        private static readonly object _logLock = new object();

        private static void WriteLog(string s)
        {
            lock (_logLock)
            {
                try { _log?.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s); }
                catch { /* logging must never take the app down */ }
            }
        }

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // The log is flushed after every line (it used to be lost when the app crashed) and written from any thread.
            try
            {
                _log = new StreamWriter(Path.Combine(baseDir, "s3mo.log"), false) { AutoFlush = true };
            }
            catch { _log = null; }
            WriteLog($"Sims 3 Mod Organizer {Version} starting");

            Logger.InfoLoggedEvent += WriteLog;
            Logger.DebugLoggedEvent += WriteLog;
            Logger.WarningLoggedEvent += s => WriteLog("WARNING: " + s);
            Logger.ErrorLoggedEvent += s => WriteLog("ERROR: " + s);

            Application.ThreadException += (s, e) => ReportCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { if (e.ExceptionObject is Exception ex) WriteLog("FATAL: " + ex); };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            string iniPath = Path.Combine(baseDir, "s3mo.ini");
            Settings.ReadSettings();

            // Per-monitor DPI awareness: crisp at any scale, and windows adapt when moved between monitors.
            // If something still looks wrong on a particular setup, "DpiMode=SystemAware" (or "DpiUnaware") in s3mo.ini
            // switches back without needing the compatibility settings on the exe.
            HighDpiMode dpiMode = HighDpiMode.PerMonitorV2;
            if (Settings.TryGetSettings("DpiMode", out string mode) && Enum.TryParse(mode, true, out HighDpiMode parsed))
                dpiMode = parsed;
            Application.SetHighDpiMode(dpiMode);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!File.Exists(iniPath))
            {
                Application.Run(new StartupForm());
                Settings.ReadSettings();
            }

            Application.Run(new MainForm());

            Settings.WriteSettings();
            WriteLog("Closed");

            lock (_logLock)
            {
                _log?.Dispose();
                _log = null;
            }
        }

        private static void ReportCrash(Exception ex)
        {
            WriteLog("UNHANDLED: " + ex);
            MessageBox.Show($"Something went wrong:{Environment.NewLine}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}Details were written to s3mo.log.",
                "Sims 3 Mod Organizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

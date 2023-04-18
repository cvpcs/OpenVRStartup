using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Valve.VR;

namespace OpenVRStartup
{
    class Program
    {
        static readonly ILogger<Program> logger = LoggerFactory.Create(builder =>
            builder.AddSerilog(new LoggerConfiguration()
                .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} {Message}{NewLine}{Exception}")
                .WriteTo.File(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} {Message}{NewLine}{Exception}", path: $"{Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location)}.log")
                .CreateLogger())).CreateLogger<Program>();

        static readonly string PATH_STARTFOLDER = "./start/";
        static readonly string PATH_STOPFOLDER = "./stop/";
        static readonly string FILE_PATTERN = "*.cmd";

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        public const int SW_SHOWMINIMIZED = 2;
        private volatile static bool _isReady = false;

        static void Main(string[] _)
        {
            // Window setup
            Console.Title = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>().Title;

            // Starting worker
            var t = new Thread(Worker);
            logger.LogInformation($"Application starting ({Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion})");
            if (!t.IsAlive) t.Start();
            else logger.LogError("Error: Could not start worker thread");

            _isReady = true;
            Minimize();

            Console.ReadLine();
            t.Abort();

            OpenVR.Shutdown();
        }

        private static void Minimize() {
            IntPtr winHandle = Process.GetCurrentProcess().MainWindowHandle;
            ShowWindow(winHandle, SW_SHOWMINIMIZED);
        }

        private static bool _isConnected = false;

        private static void Worker()
        {
            var shouldRun = true;

            Thread.CurrentThread.IsBackground = true;
            while (shouldRun)
            {
                if (!_isConnected)
                {
                    Thread.Sleep(1000);
                    _isConnected = InitVR();
                }
                else if(_isReady)
                {
                    RunScripts(PATH_STARTFOLDER);
                    if(WeHaveScripts(PATH_STOPFOLDER)) WaitForQuit();
                    OpenVR.Shutdown();
                    RunScripts(PATH_STOPFOLDER);
                    shouldRun = false;
                }
                if (!shouldRun)
                {
                    logger.LogInformation("Application exiting");
                    Environment.Exit(0);
                }
            }
        }
        
        // Initializing connection to OpenVR
        private static bool InitVR()
        {
            var error = EVRInitError.None;
            OpenVR.Init(ref error, EVRApplicationType.VRApplication_Overlay);
            if (error != EVRInitError.None)
            {
                logger.LogError($"OpenVR init failed: {Enum.GetName(typeof(EVRInitError), error)}");
                return false;
            }
            else
            {
                logger.LogInformation("OpenVR init success");

                // Add app manifest and set auto-launch
                var appKey = "boll7708.openvrstartup";
                if (!OpenVR.Applications.IsApplicationInstalled(appKey))
                {
                    var manifestError = OpenVR.Applications.AddApplicationManifest(Path.GetFullPath("./app.vrmanifest"), false);
                    if (manifestError == EVRApplicationError.None) logger.LogInformation("Successfully installed app manifest");
                    else logger.LogError($"Failed to add app manifest: {Enum.GetName(typeof(EVRApplicationError), manifestError)}");
                    
                    var autolaunchError = OpenVR.Applications.SetApplicationAutoLaunch(appKey, true);
                    if (autolaunchError == EVRApplicationError.None) logger.LogInformation("Successfully set app to auto launch");
                    else logger.LogError($"Failed to turn on auto launch: {Enum.GetName(typeof(EVRApplicationError), autolaunchError)}");
                }
                return true;
            }
        }

        // Scripts
        private static void RunScripts(string folder) {
            try
            {
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                var files = Directory.GetFiles(folder, FILE_PATTERN);
                logger.LogInformation($"Found: {files.Length} script(s) in {folder}");
                foreach (var file in files)
                {
                    logger.LogInformation($"Executing: {file}");
                    var path = Path.Combine(Environment.CurrentDirectory, file);
                    Process p = new Process();
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    p.StartInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                    p.StartInfo.Arguments = $"/C \"{path}\"";
                    p.Start();
                }
                if (files.Length == 0) logger.LogWarning($"Did not find any {FILE_PATTERN} files to execute in {folder}");
            }
            catch (Exception e)
            {
                logger.LogError($"Could not load scripts from {folder}: {e.Message}");
            }
        }

        private static void WaitForQuit()
        {
            logger.LogInformation("This window remains to wait for the shutdown of SteamVR to run additional scripts on exit.");
            var shouldRun = true;
            while(shouldRun)
            {
                var vrEvents = new List<VREvent_t>();
                var vrEvent = new VREvent_t();
                uint eventSize = (uint)Marshal.SizeOf(vrEvent);
                try
                {
                    while (OpenVR.System.PollNextEvent(ref vrEvent, eventSize))
                    {
                        vrEvents.Add(vrEvent);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError($"Could not get new events: {e.Message}");
                }

                foreach (var e in vrEvents)
                {
                    if ((EVREventType)e.eventType == EVREventType.VREvent_Quit)
                    {
                        OpenVR.System.AcknowledgeQuit_Exiting();
                        shouldRun = false;
                    }
                }
                Thread.Sleep(1000);
            }
        }

        private static bool WeHaveScripts(string folder)
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Directory.GetFiles(folder, FILE_PATTERN).Length > 0;
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using DamaCapture.Services;
using DamaCapture.Tests;

namespace DamaCapture;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A short-lived helper copy of the app that runs OCR and face detection, then exits.
        if (args.Length > 0 && args[0] == AnalysisHost.Argument) return AnalysisHost.Serve(Console.OpenStandardInput(), Console.OpenStandardOutput());
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        if (args.Contains("--self-test"))
        {
            var report = Path.Combine(AppContext.BaseDirectory, "self-test-result.json");
            try
            {
                var results = ImageEngineTests.Run().Concat(SessionDocumentTests.Run()).Concat(ServiceTests.Run()).Concat(HistoryWriterTests.Run()).Concat(CaptureTests.Run()).Concat(MotionTests.Run()).Concat(ClipboardMonitorTests.Run()).Concat(ClipboardHistoryTests.Run()).Concat(ClipboardFollowerTests.Run()).Concat(TextRecognitionTests.Run()).Concat(SensitiveDetectionTests.Run()).Concat(CodeReaderTests.Run()).Concat(HistoryWindowTests.Run()).ToArray();
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
                return 1;
            }
        }
        var demo = args.Contains("--demo");
        var instanceName = demo ? "Local\\DamaCapture.Preview." + typeof(Program).Assembly.GetName().Version : "Local\\DamaCapture.Desktop";
        using var singleInstance = new Mutex(true, instanceName, out var firstInstance);
        if (!firstInstance)
        {
            // Windows starting a second copy at sign-in has nothing to show; the first one is already in the tray.
            if (args.Contains(StartupRegistration.TrayArgument)) return 0;
            // The app lives in the tray, so opening it again means "show me the window", not an error.
            try { using var signal = EventWaitHandle.OpenExisting(instanceName + ".Show"); signal.Set(); }
            catch (Exception) { MessageBox.Show("담아가 이미 실행 중입니다. 작업 표시줄 또는 트레이에서 담아를 열어주세요.", "담아"); }
            return 0;
        }
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show");
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        Ui.Install(app);
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show("작업을 완료하지 못했습니다.\n" + e.Exception.Message, "담아", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        var window = new MainWindow(demo, clipboardDemo: demo);
        var waiting = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => window.Dispatcher.BeginInvoke(window.ShowFromOutside), null, Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            // Started by Windows at sign-in: stay in the tray until a hotkey or the tray icon is used.
            if (!demo && args.Contains(StartupRegistration.TrayArgument)) { app.MainWindow = window; window.StartInTray(); return app.Run(); }
            return app.Run(window);
        }
        finally { waiting.Unregister(null); }
    }
}

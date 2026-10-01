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
        using var singleInstance = new Mutex(true, args.Contains("--demo") ? "Local\\DamaCapture.Preview." + typeof(Program).Assembly.GetName().Version : "Local\\DamaCapture.Desktop", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("담아가 이미 실행 중입니다. 작업 표시줄 또는 트레이에서 담아를 열어주세요.", "담아");
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        Ui.Install(app);
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show("작업을 완료하지 못했습니다.\n" + e.Exception.Message, "담아", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        return app.Run(new MainWindow(args.Contains("--demo"), clipboardDemo: args.Contains("--demo")));
    }
}

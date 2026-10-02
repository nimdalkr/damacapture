using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DamaCapture.Capture;
using DamaCapture.Imaging;
using DamaCapture.Services;

namespace DamaCapture.Tests;

/// <summary>Exercises the real close/flush lifecycle with the app's isolated, synthetic demo capture.</summary>
public static class HistoryWindowTests
{
    public static List<string> Run()
    {
        var ownsApplication = Application.Current == null;
        var app = Application.Current ?? new Application();
        var previousShutdownMode = app.ShutdownMode;
        var previousMotion = Motion.ReduceMotion;
        var previousMainWindow = app.MainWindow;
        var resourceCount = app.Resources.MergedDictionaries.Count;
        var exceptions = new List<Exception>();
        MainWindow? window = null;
        DispatcherFrame? activeFrame = null;
        bool loaded = false, closed = false;
        var closingEvents = 0;
        var budget = Stopwatch.StartNew();
        var maximum = TimeSpan.FromSeconds(20);

        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            exceptions.Add(args.Exception);
            args.Handled = true;
            if (activeFrame != null) activeFrame.Continue = false;
        }

        void PumpUntil(Func<bool> condition)
        {
            if (condition() || exceptions.Count > 0 || budget.Elapsed >= maximum) return;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            EventHandler tick = (_, _) =>
            {
                if (condition() || exceptions.Count > 0 || budget.Elapsed >= maximum) frame.Continue = false;
            };
            timer.Tick += tick;
            activeFrame = frame;
            try { timer.Start(); Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); timer.Tick -= tick; activeFrame = null; }
        }

        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += OnUnhandled;
        try
        {
            Ui.Install(app);
            // Demo creates its own GUID temp directory, synthetic image, and no tray or global hotkeys.
            window = new MainWindow(demo: true) { ShowInTaskbar = false };
            window.Loaded += (_, _) => loaded = true;
            window.Closing += (_, _) => closingEvents++;
            window.Closed += (_, _) => closed = true;
            window.Show();
            PumpUntil(() => loaded);
            Assert(exceptions.Count == 0, Describe(exceptions));
            Assert(loaded && window.IsVisible, "The demo editor did not finish loading within five seconds.");

            T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            void Invoke(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
            var original = Field<ImageDocument>("document");
            var history = Field<HistoryStore>("history");
            var first = history.Entries[0];
            var mask = new EditOperation { Kind = EditKind.Mosaic, Bounds = new Rect(30, 30, 110, 80), Strength = 16 };
            original.Add(mask); Invoke("RefreshEditor");
            PumpUntil(() => Field<TextBlock>("preservationLabel").Text == "기록됨");
            Assert(Field<TextBlock>("preservationLabel").Text == "기록됨", "Latest edit never reached the visible preserved state.");
            Assert(!window.Title.Contains('*') && !window.Title.Contains("저장 전"), "Auto-preserved capture still advertises an unsaved document.");
            // Moving to another capture must keep editable redactions and undo history.
            Invoke("SetImage", ImageFactory.Sample());
            Invoke("RecordCaptureHistory", new CaptureContext(DateTimeOffset.Now, "두 번째 예시 창", "예시 앱", "Region"));
            Invoke("ShowPage", "editor", true);
            Invoke("OpenHistory", first);
            Assert(ReferenceEquals(Field<ImageDocument>("document"), original) && original.CanUndo && original.Operations.Any(o => o.Id == mask.Id), "History navigation lost the editing session.");
            original.Undo(); Invoke("RefreshEditor");
            Assert(original.CanRedo && original.Operations.Count == 0, "Undo was not retained after returning from history.");
            original.Redo(); Invoke("RefreshEditor");
            Assert(original.Operations.Any(o => o.Id == mask.Id), "Redo was not retained after returning from history.");
            Invoke("SetHistoryExpanded", true);
            Assert(Field<StackPanel>("recentFiles").Children.Count > 0, "Expanded history did not render entries.");
            Invoke("SetHistoryPeriod", "day", DateTime.Today.AddDays(-30));
            Assert(Field<TextBlock>("historyCount").Text.StartsWith("0개"), "Date filter did not exclude captures from other dates.");
            Invoke("SetHistoryPeriod", "all", null);
            Assert(Field<TextBlock>("historyCount").Text.StartsWith("2개"), "Clearing the date filter failed to restore both captures.");

            // Synthetic clipboard records exercise the integrated UI and storage without
            // reading or writing the user's actual clipboard or attaching a listener.
            var settings = Field<AppSettings>("settings");
            Assert(!settings.ArchiveClipboard && Field<object?>("clipboardMonitor") == null,
                "Clipboard collection must be opt-in and detached in the demo window.");
            settings.ArchiveClipboard = true;
            try
            {
                Invoke("ApplyClipboardSetting");
                Assert(Field<object?>("clipboardMonitor") == null, "A demo window attached to the real clipboard after opt-in.");
            }
            finally { settings.ArchiveClipboard = false; }

            var clipboardHistory = Field<HistoryStore>("clipboardHistory");
            var clipboardWriter = Field<HistorySnapshotWriter>("clipboardWriter");
            var token = "clipboard-window-test-" + Guid.NewGuid().ToString("N");
            var textEntry = clipboardHistory.RecordClipboard(HistoryKind.ClipboardText, DateTimeOffset.Now,
                "복사 텍스트 예시 창", "텍스트 예시 앱", "첫 줄\n" + token + "\n마지막 줄 😀");
            var copiedPixels = ImageFactory.Sample();
            var imageEntry = clipboardHistory.RecordClipboard(HistoryKind.ClipboardImage, DateTimeOffset.Now,
                "복사 이미지 예시 창", "이미지 예시 앱", width: copiedPixels.PixelWidth, height: copiedPixels.PixelHeight);
            clipboardWriter.Enqueue(imageEntry.Id, copiedPixels);
            PumpUntil(() => clipboardWriter.PendingOrFailed(imageEntry.Id) == null && File.Exists(clipboardHistory.Find(imageEntry.Id)!.ImagePath));
            Assert(!clipboardWriter.HasFailures && File.Exists(clipboardHistory.Find(imageEntry.Id)!.ImagePath),
                "The synthetic clipboard image was not archived.");
            Invoke("SetHistoryKind", "all");
            Assert(Field<TextBlock>("historyCount").Text.StartsWith("4개"), "Combined history must contain both captures and both clipboard entries.");
            Invoke("SetHistoryKind", "text");
            Assert(Field<TextBlock>("historyCount").Text.StartsWith("1개"), "Copied-text filtering included other kinds or lost the text entry.");
            // Typing is debounced; apply the pending search the way the timer would.
            Field<TextBox>("historySearch").Text = token; Invoke("RefreshHistory");
            Assert(Field<TextBlock>("historyCount").Text.StartsWith("1개"), "Searching clipboard text did not find its unique body content.");
            Field<TextBox>("historySearch").Text = ""; Invoke("RefreshHistory");
            Invoke("SetHistoryKind", "image");
            Assert(Field<TextBlock>("historyCount").Text.StartsWith("1개"), "Copied-image filtering included captures or text.");
            Invoke("SetHistoryKind", "files");
            Assert(Field<TextBlock>("historyCount").Text.StartsWith("0개"), "File-list filtering included entries of another kind.");
            Invoke("SetHistoryKind", "capture");
            Assert(Field<TextBlock>("historyCount").Text.StartsWith("2개"), "Capture filtering included clipboard entries.");
            Invoke("SetHistoryKind", "all");

            var copiedFromPreview = false;
            var textPreview = new ClipboardPreviewWindow(window, textEntry, () => copiedFromPreview = true);
            try
            {
                var previewText = Descendants<TextBox>(textPreview).Single();
                Assert(textPreview.Title == "복사한 텍스트" && previewText.IsReadOnly && previewText.Text == textEntry.Text,
                    "Text preview changed archived content or allowed editing.");
                var labels = Descendants<TextBlock>(textPreview).Select(block => block.Text).ToArray();
                Assert(labels.Any(label => label.Contains(textEntry.CapturedAt!.Value.LocalDateTime.ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture)) && label.Contains(textEntry.ApplicationName))
                    && labels.Contains(textEntry.WindowTitle), "Preview omitted exact time, application, or source window metadata.");
            }
            finally { textPreview.Close(); }
            var missingPath = Path.Combine(Path.GetTempPath(), "DamaCapture-missing-" + Guid.NewGuid().ToString("N"), "원본 파일.txt");
            var filePreview = new ClipboardPreviewWindow(window, textEntry with { Kind = HistoryKind.ClipboardFiles, Text = "", FilePaths = [missingPath] }, () => copiedFromPreview = true);
            try
            {
                var previewFiles = Descendants<TextBox>(filePreview).Single();
                Assert(filePreview.Title == "복사한 파일 목록" && previewFiles.IsReadOnly && previewFiles.Text.Contains(missingPath)
                    && previewFiles.Text.Contains("원본 없음"), "File preview lost the original path or did not identify a missing source.");
                Assert(!copiedFromPreview, "Constructing a preview automatically invoked the copy action.");
            }
            finally { filePreview.Close(); }

            Invoke("OpenHistory", imageEntry);
            var clipboardDocument = Field<ImageDocument>("document");
            Assert(Field<Guid?>("currentHistoryId") == imageEntry.Id && Pixels(clipboardDocument.Render()).SequenceEqual(Pixels(copiedPixels)),
                "Opening clipboard image history did not restore its archived pixels.");
            var keepCaptures = settings.KeepHistory;
            settings.KeepHistory = false;
            try
            {
                var beforeEdit = Pixels(clipboardDocument.Render());
                clipboardDocument.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(20, 20, 120, 90) });
                Invoke("Undo"); Invoke("Redo");
                var expectedPixels = Pixels(clipboardDocument.Render());
                Assert(!expectedPixels.SequenceEqual(beforeEdit), "The clipboard image edit did not change fixture pixels.");
                PumpUntil(() => Field<TextBlock>("preservationLabel").Text == "기록됨");
                Assert(Field<TextBlock>("preservationLabel").Text == "기록됨" && !clipboardWriter.HasFailures,
                    "Clipboard image edits stopped being archived when capture history was disabled.");
                Assert(Pixels(ImageFiles.Load(clipboardHistory.Find(imageEntry.Id)!.ImagePath)).SequenceEqual(expectedPixels)
                    && history.Find(imageEntry.Id) == null, "Clipboard edits were stored incorrectly or leaked into capture history.");
            }
            finally { settings.KeepHistory = keepCaptures; }
            Invoke("OpenHistory", first);
            Invoke("SetHistoryKind", "all");
            Assert(ReferenceEquals(Field<ImageDocument>("document"), original)
                && Field<TextBlock>("historyCount").Text.StartsWith("4개"), "Clipboard navigation failed to restore the original editing session and combined history.");

            // Hiding another record during an edit must flush the latest current pixels,
            // and must retain the hidden record's managed PNG.
            PumpUntil(() => history.Entries.All(e => System.IO.File.Exists(e.ImagePath)));
            var second = history.Entries.First(e => e.Id != first.Id);
            original.Add(new EditOperation { Kind = EditKind.Rectangle, Bounds = new Rect(5, 5, 30, 30) });
            Invoke("RefreshEditor");
            var hideStarted = false;
            window.Dispatcher.BeginInvoke(() => { Invoke("HideHistoryEntry", second); hideStarted = true; });
            PumpUntil(() => hideStarted && window.IsEnabled);
            Assert(history.Find(second.Id) == null && System.IO.File.Exists(history.Find(first.Id)!.ImagePath), "Hiding a history entry lost the active capture.");
            Assert(System.IO.File.Exists(second.ImagePath), "Hiding removed the managed PNG.");
            Assert(Field<TextBlock>("preservationLabel").Text == "기록됨", "Hiding interrupted the active capture's pending preservation.");

            // Selecting by filter covers exactly the records shown, and deleting them removes only their managed files.
            var copiedImage = clipboardHistory.Entries.First(e => e.Kind == HistoryKind.ClipboardImage);
            PumpUntil(() => System.IO.File.Exists(copiedImage.ImagePath));
            Invoke("SetHistorySelecting", true);
            Invoke("SetHistoryKind", "image");
            Invoke("ToggleSelectAllHistory");
            var selection = Field<HashSet<Guid>>("historySelection");
            Assert(selection.Count == 1 && selection.Contains(copiedImage.Id), "Selecting all did not cover exactly the filtered records.");
            Invoke("SetHistoryKind", "text");
            Assert(selection.Count == 0, "Changing the filter kept records that are no longer shown selected.");
            Invoke("SetHistoryKind", "image");
            Invoke("ToggleSelectAllHistory");
            // Started from the dispatcher, as a click would, so its continuation returns to the UI thread.
            System.Threading.Tasks.Task<int>? deleting = null;
            window.Dispatcher.BeginInvoke(() => deleting = (System.Threading.Tasks.Task<int>)typeof(MainWindow).GetMethod("DeleteHistoryEntriesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [selection.ToList()])!);
            PumpUntil(() => deleting is { IsCompleted: true } && window.IsEnabled);
            Assert(deleting is { IsCompleted: true } && deleting.Result == 1 && clipboardHistory.Find(copiedImage.Id) == null && !System.IO.File.Exists(copiedImage.ImagePath)
                && history.Find(first.Id) != null && System.IO.File.Exists(history.Find(first.Id)!.ImagePath), "Deleting the selection removed the wrong records or left the managed image.");
            Assert(Field<TextBlock>("historyCount").Text.StartsWith("0개") && selection.Count == 0, "Deleted records stayed listed or selected.");
            Invoke("SetHistorySelecting", false);
            Invoke("SetHistoryKind", "all");

            // Text is typed in place, can be opened again and changed, and emptied text is removed.
            var textDocument = Field<ImageDocument>("document");
            var operationsBefore = textDocument.Operations.Count;
            Invoke("BeginTextEdit", new Point(120, 140), null);
            Field<TextBox>("textEditor").Text = "확인 필요\n두 번째 줄";
            Invoke("CommitTextEdit");
            var placed = textDocument.Operations.Last();
            Assert(textDocument.Operations.Count == operationsBefore + 1 && placed.Kind == EditKind.Text && placed.Text == "확인 필요\n두 번째 줄"
                && placed.Bounds.TopLeft == new Point(120, 140) && placed.Bounds.Height > 40, "Typed text was not placed where it was started, with both lines.");
            Invoke("BeginTextEdit", new Point(0, 0), placed);
            Assert(textDocument.Hidden == placed.Id && Field<TextBox>("textEditor").Text == placed.Text, "Opening placed text must take it out of the image and load it into the editor.");
            Field<TextBox>("textEditor").Text = "수정됨";
            Invoke("CommitTextEdit");
            var changed = textDocument.Operations.Single(operation => operation.Id == placed.Id);
            Assert(textDocument.Hidden == null && changed.Text == "수정됨" && changed.Bounds.TopLeft == placed.Bounds.TopLeft && changed.Bounds.Height < placed.Bounds.Height
                && textDocument.Operations.Count == operationsBefore + 1, "Edited text was not updated in place.");
            // A change of weight or font alone, with the same text and box, must not be mistaken for no change.
            textDocument.Update(changed with { Bold = !changed.Bold, Font = "Consolas" });
            var restyled = textDocument.Operations.Single(operation => operation.Id == placed.Id);
            Assert(restyled.Bold != changed.Bold && restyled.Font == "Consolas", "A weight-only or font-only change to placed text was dropped.");
            textDocument.Update(changed);
            Invoke("BeginTextEdit", new Point(0, 0), changed);
            Field<TextBox>("textEditor").Text = "버릴 내용";
            Invoke("CancelTextEdit");
            Assert(textDocument.Hidden == null && textDocument.Operations.Single(operation => operation.Id == placed.Id).Text == "수정됨", "Cancelling must leave placed text as it was.");
            Invoke("BeginTextEdit", new Point(0, 0), changed);
            Field<TextBox>("textEditor").Text = "  ";
            Invoke("CommitTextEdit");
            Assert(textDocument.Operations.All(operation => operation.Id != placed.Id) && textDocument.Operations.Count == operationsBefore, "Emptied text must be removed.");

            // "폴더에 바로 저장": the file is written at once, follows edits, and is not recreated once the user removes it.
            var saveFolder = Path.Combine(Path.GetTempPath(), "DamaCapture-autosave-" + Guid.NewGuid().ToString("N"));
            var previousFolder = settings.SaveFolder; settings.SaveFolder = saveFolder;
            try
            {
                bool Idle() => (bool)typeof(MainWindow).GetProperty("AutoSaveIdle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var savedName = (string?)typeof(MainWindow).GetMethod("AutoSaveCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Assert(savedName != null, "The capture could not be saved to the folder.");
                var savedPath = Path.Combine(saveFolder, savedName!);
                PumpUntil(() => Idle() && history.Find(first.Id)?.FollowedPath == savedPath);
                Assert(File.Exists(savedPath) && history.Find(first.Id)?.FollowedPath == savedPath && Pixels(ImageFiles.Load(savedPath)).SequenceEqual(Pixels(textDocument.Render())),
                    "The capture was not written to the save folder as it looked.");
                textDocument.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(200, 200, 90, 60) });
                Invoke("RefreshEditor"); Invoke("FlushAutoSave");
                PumpUntil(Idle);
                Assert(Pixels(ImageFiles.Load(savedPath)).SequenceEqual(Pixels(textDocument.Render())), "The saved file did not follow the edit, so the folder still holds the image from before it.");
                File.Delete(savedPath);
                textDocument.Add(new EditOperation { Kind = EditKind.Solid, Bounds = new Rect(10, 300, 40, 40) });
                Invoke("RefreshEditor"); Invoke("FlushAutoSave");
                PumpUntil(Idle);
                Assert(!File.Exists(savedPath), "A file the user removed was recreated.");
                // JPG is flattened onto white off the UI thread; it must still be written and readable.
                settings.SaveFormat = "jpg";
                var jpgName = (string?)typeof(MainWindow).GetMethod("AutoSaveCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                PumpUntil(Idle);
                var jpgPath = Path.Combine(saveFolder, jpgName ?? "missing.jpg");
                Assert(jpgName != null && jpgName.EndsWith(".jpg", StringComparison.Ordinal) && File.Exists(jpgPath) && ImageFiles.Load(jpgPath).PixelWidth == textDocument.Width,
                    "Saving straight to the folder as JPG failed.");
            }
            finally { settings.SaveFolder = previousFolder; settings.SaveFormat = "png"; try { Directory.Delete(saveFolder, true); } catch (IOException) { } }

            // Automatic cleanup deletes only records past the chosen age, through the same path, and never the one being edited.
            var stale = history.RecordCapture(DateTimeOffset.Now.AddDays(-40), "오래된 창", "예시 앱", "Region", 8, 8);
            var staler = history.RecordCapture(DateTimeOffset.Now.AddDays(-90), "더 오래된 창", "예시 앱", "Region", 8, 8);
            var retentionDays = settings.HistoryRetentionDays; var retentionCount = settings.HistoryRetentionCount;
            try
            {
                settings.HistoryRetentionDays = 30;
                System.Threading.Tasks.Task<int>? trimming = null;
                window.Dispatcher.BeginInvoke(() => trimming = (System.Threading.Tasks.Task<int>)typeof(MainWindow).GetMethod("TrimHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false])!);
                PumpUntil(() => trimming is { IsCompleted: true } && window.IsEnabled);
                Assert(trimming is { IsCompleted: true } && trimming.Result == 2 && history.Find(stale.Id) == null && history.Find(staler.Id) == null
                    && history.Find(first.Id) != null && clipboardHistory.Find(textEntry.Id) != null, "Age-based cleanup removed the wrong records.");
                // A count of one keeps the newest record; the one being edited is older but protected.
                settings.HistoryRetentionDays = 0; settings.HistoryRetentionCount = 1;
                trimming = null;
                window.Dispatcher.BeginInvoke(() => trimming = (System.Threading.Tasks.Task<int>)typeof(MainWindow).GetMethod("TrimHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false])!);
                PumpUntil(() => trimming is { IsCompleted: true } && window.IsEnabled);
                Assert(trimming is { IsCompleted: true } && trimming.Result == 0 && history.Find(first.Id) != null && Field<Guid?>("currentHistoryId") == first.Id,
                    "Count-based cleanup deleted the record being edited.");
            }
            finally { settings.HistoryRetentionDays = retentionDays; settings.HistoryRetentionCount = retentionCount; }

            // The first close is canceled while history flushes. Exit must be dispatched only
            // after this Closing event has returned; calling Show during Closing used to throw.
            window.Close();
            PumpUntil(() => closed);
            Assert(exceptions.Count == 0, Describe(exceptions));
            Assert(closed && !window.IsVisible, "Closing the editor did not finish flushing history and close the window within five seconds.");
            Assert(closingEvents >= 2, "The history flush did not complete the deferred second close.");

            // Drain callbacks already queued by the writer before deciding the lifecycle passed.
            var drained = false;
            app.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => drained = true));
            PumpUntil(() => drained);
            Assert(exceptions.Count == 0, Describe(exceptions));
            Assert(drained, "The closed editor still had unfinished dispatcher work after five seconds.");
            return ["편집 흐름: 자동 보관 상태·편집 복원·캡처/복사 기록 필터와 본문 검색·읽기 전용 미리보기·캡처 정책과 독립된 복사 이미지 편집 저장·숨기기 후 보관 유지 통과",
                "기록 선택 삭제: 필터 기준 모두 선택·필터 변경 시 선택 정리·선택한 기록과 관리 이미지만 삭제 통과",
                "기록 자동 정리: 기간 지난 기록만 삭제, 개수 상한에서 편집 중 기록 보호 통과",
                "텍스트 바로 입력: 클릭한 자리에 입력·여러 줄, 놓은 글자 다시 편집, 취소 시 원래대로, 비우면 삭제 통과",
                "폴더에 바로 저장: 캡처 즉시 저장, 편집을 따라 같은 파일 갱신, 사용자가 지운 파일은 다시 만들지 않음 통과", "창 종료: 실제 클립보드 접근 없는 격리된 예시 기록·두 이미지 저장 큐 처리 후 WPF 창 종료, 이중 Closing 완료·Dispatcher 예외 없음 통과"];
        }
        finally
        {
            // This suite runs last. Shutting down only the Application we created also cleans
            // up the test window if the regression leaves its first close canceled.
            try
            {
                if (ownsApplication) app.Shutdown();
                else
                {
                    if (window != null && !closed)
                    {
                        window.Closing += (_, args) => args.Cancel = false;
                        window.Close();
                    }
                    app.MainWindow = previousMainWindow;
                    app.ShutdownMode = previousShutdownMode;
                    while (app.Resources.MergedDictionaries.Count > resourceCount)
                        app.Resources.MergedDictionaries.RemoveAt(app.Resources.MergedDictionaries.Count - 1);
                }
            }
            finally
            {
                app.DispatcherUnhandledException -= OnUnhandled;
                Motion.ReduceMotion = previousMotion;
            }
        }
    }

    private static string Describe(List<Exception> exceptions) => exceptions.Count == 0 ? "" :
        "The window lifecycle raised a dispatcher exception: " + string.Join(" | ", exceptions.Select(exception => exception.ToString()));

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var normalized = ImageDocument.Normalize(image);
        var pixels = new byte[checked(normalized.PixelWidth * normalized.PixelHeight * 4)];
        normalized.CopyPixels(pixels, normalized.PixelWidth * 4, 0);
        return pixels;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

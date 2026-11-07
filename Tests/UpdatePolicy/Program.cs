using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ShareX;
using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using SkiaSharp;

internal static class Program
{
    private static readonly Assembly AppAssembly = typeof(ApplicationConfig).Assembly;
    private static readonly Type State = AppAssembly.GetType("ShareX.ApplicationState", true)!;
    private static readonly Type ManagerType = AppAssembly.GetType("ShareX.ShareXUpdateManager", true)!;
    private static readonly MethodInfo Apply = AppAssembly.GetType("ShareX.ApplicationSettingsRuntime", true)!
        .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo UpdateTimer = typeof(GitHubUpdateManager)
        .GetField("updateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [STAThread]
    private static int Main()
    {
        AvaloniaBootstrapper.EnsureInitialized();
        int failures = 0;
        Run("legacy enabled update checks stay disabled over three restarts", () =>
        {
            string saved = """{"AutoCheckUpdate":true,"UpdateChannel":"Release"}""";
            for (int session = 0; session < 3; session++)
            {
                ApplicationConfig settings = JsonConvert.DeserializeObject<ApplicationConfig>(saved, new StringEnumConverter())!;
                using GitHubUpdateManager manager = ConfigureSession(settings);
                Apply.Invoke(null, null);
                AssertNoAutomaticUpdates(manager, settings);
                saved = JsonConvert.SerializeObject(new { settings.AutoCheckUpdate, settings.UpdateChannel }, new StringEnumConverter());
            }
        });
        Run("applying imported settings disposes an existing update timer", () =>
        {
            ApplicationConfig settings = new() { AutoCheckUpdate = true };
            using GitHubUpdateManager manager = ConfigureSession(settings);
            manager.AllowAutoUpdate = true;
            manager.ConfigureAutoUpdate();
            Assert(UpdateTimer.GetValue(manager) != null, "Could not establish the pre-existing timer condition.");
            Apply.Invoke(null, null);
            AssertNoAutomaticUpdates(manager, settings);
        });
        Run("new profiles do not schedule automatic official updates", () =>
        {
            ApplicationConfig settings = new();
            using GitHubUpdateManager manager = ConfigureSession(settings);
            Apply.Invoke(null, null);
            AssertNoAutomaticUpdates(manager, settings);
        });
        Run("clipboard text hotkey retains Unicode and never overwrites earlier saves", () => WithClipboard((folder, task) =>
        {
            Assert(ClipboardHelpers.CopyText("مرحبا 👋\r\nfirst entry"), "Could not prepare clipboard text.");
            TaskHelpers.ExecuteJob(task).GetAwaiter().GetResult();
            Assert(ClipboardHelpers.CopyText("second entry"), "Could not prepare clipboard text.");
            TaskHelpers.ExecuteJob(task).GetAwaiter().GetResult();
            string[] files = Directory.GetFiles(folder, "*.txt");
            Assert(files.Length == 2, "The clipboard hotkey did not save both entries.");
            string[] contents = files.Select(File.ReadAllText).ToArray();
            Assert(contents.Contains("مرحبا 👋\r\nfirst entry") && contents.Contains("second entry"), "Saved clipboard text was lost or changed.");
        }));
        Run("clipboard image hotkey retains PNG transparency", () => WithClipboard((folder, task) =>
        {
            using SKBitmap image = new(2, 2, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            image.Erase(SKColors.Transparent);
            image.SetPixel(1, 1, SKColors.Red);
            Assert(ClipboardHelpers.CopyImage(image), "Could not prepare clipboard image.");
            TaskHelpers.ExecuteJob(task).GetAwaiter().GetResult();
            string[] files = Directory.GetFiles(folder, "*.png");
            Assert(files.Length == 1, "The clipboard hotkey did not save a PNG.");
            using SKBitmap saved = SKBitmap.Decode(files[0]);
            Assert(saved.Width == 2 && saved.Height == 2 && saved.GetPixel(0, 0).Alpha == 0 && saved.GetPixel(1, 1) == SKColors.Red,
                "The clipboard PNG lost its dimensions, transparency or color.");
        }));
        Console.WriteLine($"Fork regression tests: {5 - failures} passed, {failures} failed.");
        return failures == 0 ? 0 : 1;

        void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("FAIL " + name + ": " + (error.InnerException ?? error).Message);
            }
        }
    }

    private static GitHubUpdateManager ConfigureSession(ApplicationConfig settings)
    {
        GitHubUpdateManager manager = (GitHubUpdateManager)Activator.CreateInstance(ManagerType)!;
        // Exercise the real scheduling path without network requests or dialogs
        // if the regression causes the immediate timer callback to run.
        manager.AutoUpdateEnabled = false;
        SetState("Settings", settings);
        SetState("DefaultTaskSettings", settings.DefaultTaskSettings);
        SetState("UpdateManager", manager);
        return manager;
    }

    private static void AssertNoAutomaticUpdates(GitHubUpdateManager manager, ApplicationConfig settings)
    {
        Assert(UpdateTimer.GetValue(manager) == null, "An automatic update timer was scheduled; restarting can reopen the update window.");
        Assert(!manager.AllowAutoUpdate, "Automatic official updates remained enabled.");
        Assert(!settings.AutoCheckUpdate, "The enabled update preference would be persisted for the next restart.");
    }

    private static void SetState(string name, object value) =>
        State.GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);

    private static void WithClipboard(Action<string, TaskSettings> test)
    {
        string folder = Directory.CreateTempSubdirectory("ShareX-fork-tests-").FullName;
        Avalonia.Controls.Window window = new() { Title = "ShareX clipboard tests", Width = 320, Height = 100, ShowInTaskbar = false };
        System.Windows.Forms.DataObject original = new();
        System.Windows.Forms.IDataObject? clipboard = System.Windows.Forms.Clipboard.GetDataObject();
        if (clipboard != null)
        {
            foreach (string format in clipboard.GetFormats(false))
            {
                object? value = clipboard.GetData(format, false);
                if (value != null) original.SetData(format, false, value);
            }
        }
        try
        {
            window.Show();
            window.Activate();
            ApplicationConfig settings = new() { ClipboardImagePasteFallbackFolder = folder };
            TaskSettings task = new() { Job = HotkeyType.PasteClipboardImageToFolder };
            task.GeneralSettings.PlaySoundAfterAction = false;
            task.GeneralSettings.ShowToastNotificationAfterTaskCompleted = false;
            SetState("Settings", settings);
            SetState("DefaultTaskSettings", task);
            test(folder, task);
        }
        finally
        {
            if (original.GetFormats().Length > 0) System.Windows.Forms.Clipboard.SetDataObject(original, true);
            else ClipboardHelpers.Clear();
            window.Close();
            Directory.Delete(folder, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

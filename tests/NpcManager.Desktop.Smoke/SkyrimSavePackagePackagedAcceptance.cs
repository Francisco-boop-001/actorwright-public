using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static readonly JsonSerializerOptions
        SavePackageAcceptanceJsonOptions = new()
        {
            WriteIndented = true
        };

    private static int RunSkyrimSavePackagePackagedAcceptance(
        string executable,
        string sourceRoot,
        string cancelledOutput,
        string outputRoot,
        string screenshotRoot,
        string reportPath,
        string rawReportPath,
        string negativeReportPath,
        string verifierPath)
    {
        string projectRoot =
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation";
        string runTag = Path.GetFileNameWithoutExtension(reportPath)
            .Replace(
                "sky-gui-023-packaged-acceptance-",
                string.Empty,
                StringComparison.OrdinalIgnoreCase);
        var screenshots = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["invalid"] = Path.Combine(
                screenshotRoot,
                $"{runTag}-SKY-GUI-023-invalid.png"),
            ["cancelled"] = Path.Combine(
                screenshotRoot,
                $"{runTag}-SKY-GUI-023-cancelled.png"),
            ["reviewed"] = Path.Combine(
                screenshotRoot,
                $"{runTag}-SKY-GUI-023-reviewed.png"),
            ["final"] = Path.Combine(
                screenshotRoot,
                $"{runTag}-SKY-GUI-023-final.png")
        };
        EnsureFreshAcceptancePaths(
            executable,
            sourceRoot,
            cancelledOutput,
            outputRoot,
            reportPath,
            rawReportPath,
            negativeReportPath,
            verifierPath,
            screenshots.Values);

        Process? application = null;
        var checks = new Dictionary<string, bool>(
            StringComparer.Ordinal);
        try
        {
            application = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory =
                    Path.GetDirectoryName(executable)!,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException(
                "Packaged desktop process did not start.");
            WaitUntil(
                () =>
                {
                    application.Refresh();
                    return application.MainWindowHandle != IntPtr.Zero;
                },
                "Packaged desktop did not create a main window.",
                TimeSpan.FromSeconds(20));
            _ = application.WaitForInputIdle(10_000);
            IntPtr handle = application.MainWindowHandle;
            AutomationElement root =
                AutomationElement.FromHandle(handle);
            if (!SkyGui023AcceptanceNative.MoveWindow(
                    handle,
                    12,
                    12,
                    1500,
                    920,
                    true))
                throw new InvalidOperationException(
                    "Packaged desktop refused resize.");
            Thread.Sleep(250);
            checks["resize"] = true;
            SelectAcceptanceTab(
                root,
                "Save and promote verified package task");

            SetAcceptanceValue(
                root,
                "Source package root",
                sourceRoot);
            SetAcceptanceValue(
                root,
                "Destination package root",
                sourceRoot);
            InvokeAcceptanceButton(
                root,
                "Review exact source package");
            try
            {
                WaitUntil(
                    () => AcceptanceTextContains(
                        root,
                        "Nothing was written. Correct the package"),
                    "Overlapping roots did not fail visibly.",
                    TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException)
            {
                CaptureAcceptanceWindow(
                    handle,
                    screenshots["invalid"]);
                WriteAcceptanceAutomationSnapshot(root);
                throw;
            }
            CaptureAcceptanceWindow(
                handle,
                screenshots["invalid"]);
            checks["validationFailure"] = true;

            SetAcceptanceValue(
                root,
                "Destination package root",
                cancelledOutput);
            InvokeAcceptanceButton(
                root,
                "Review exact source package");
            try
            {
                InvokeAcceptanceButton(
                    root,
                    "Cancel active package operation");
            }
            catch (InvalidOperationException)
            {
                WaitUntil(
                    () => AcceptanceTextContains(
                        root,
                        "exact supported options are hash-bound"),
                    "Review did not become ready for cancellation fallback.",
                    TimeSpan.FromSeconds(30));
                InvokeAcceptanceButton(
                    root,
                    "Apply verified save package choices");
                InvokeAcceptanceButton(
                    root,
                    "Cancel active package operation");
            }
            WaitUntil(
                () => AcceptanceTextContains(root, "Cancelled"),
                "Packaged cancellation was not retained.",
                TimeSpan.FromSeconds(30));
            if (Directory.Exists(cancelledOutput))
                throw new InvalidOperationException(
                    "Cancellation retained a destination package.");
            CaptureAcceptanceWindow(
                handle,
                screenshots["cancelled"]);
            checks["cancellationRollback"] = true;

            SetAcceptanceValue(
                root,
                "Destination package root",
                outputRoot);
            SelectAcceptanceCombo(
                root,
                "NPC save scope including Selected and All changed",
                "All changed NPCs");
            SelectAcceptanceCombo(
                root,
                "Fresh package or Update existing as fresh-derived output",
                "Fresh-derived update");
            SelectAcceptanceCombo(
                root,
                "Plugin encoding choice",
                "UTF-8");
            SelectAcceptanceCombo(
                root,
                "Loose assets or BSA archive choice",
                "Skyrim BSA v105");
            SelectAcceptanceCombo(
                root,
                "Preserve create or append LVLN choice",
                "Append existing LVLN");
            SetAcceptanceToggle(root, "Write ESM flag", true);
            SetAcceptanceToggle(root, "Write ESL flag", false);
            SetAcceptanceValue(
                root,
                "Leveled NPC list EditorID",
                "Gate23ExistingActors");
            SetAcceptanceToggle(
                root,
                "Suppress duplicate LVLN entries",
                true);

            SendAcceptanceAccessKey(handle, "r");
            WaitUntil(
                () => AcceptanceTextContains(
                    root,
                    "exact supported options are hash-bound"),
                "Alt+R did not bind the production review.",
                TimeSpan.FromSeconds(30));
            if (Directory.Exists(outputRoot))
                throw new InvalidOperationException(
                    "Review wrote the output package before Apply.");
            ScrollAcceptance(root, 35);
            Thread.Sleep(250);
            CaptureAcceptanceWindow(
                handle,
                screenshots["reviewed"]);
            checks["keyboardReviewAndProposalOnly"] = true;

            SendAcceptanceAccessKey(handle, "a");
            WaitUntil(
                () =>
                    File.Exists(Path.Combine(
                        outputRoot,
                        "npcmanager-package.json")) &&
                    AcceptanceTextContains(
                        root,
                        "Destination reopened"),
                "Alt+A did not produce the verified package.",
                TimeSpan.FromSeconds(90));
            ScrollAcceptance(root, 35);
            Thread.Sleep(350);
            CaptureAcceptanceWindow(
                handle,
                screenshots["final"]);
            checks["keyboardApplyAndReadback"] = true;
            checks["runtimeBoundaryVisible"] =
                AcceptanceTextContains(
                    root,
                    "Runtime authority remains false");
            if (!checks["runtimeBoundaryVisible"])
                throw new InvalidOperationException(
                    "Final UI lost the runtime-authority boundary.");

            RunAcceptanceVerifier(
                verifierPath,
                outputRoot,
                sourceRoot,
                rawReportPath,
                false);
            RunAcceptanceVerifier(
                verifierPath,
                outputRoot,
                sourceRoot,
                negativeReportPath,
                true);
            using JsonDocument raw = JsonDocument.Parse(
                File.ReadAllBytes(rawReportPath));
            using JsonDocument negative = JsonDocument.Parse(
                File.ReadAllBytes(negativeReportPath));
            if (raw.RootElement.GetProperty("verdict")
                    .GetString() != "PASS" ||
                raw.RootElement.GetProperty("mismatchCount")
                    .GetInt32() != 0 ||
                negative.RootElement.GetProperty("verdict")
                    .GetString() != "EXPECTED_FAIL" ||
                negative.RootElement.GetProperty("mismatchCount")
                    .GetInt32() < 1)
                throw new InvalidDataException(
                    "Independent verifier verdicts are not acceptable.");
            checks["independentRawAudit"] = true;
            checks["negativeControl"] = true;

            var window = (WindowPattern)root.GetCurrentPattern(
                WindowPattern.Pattern);
            window.Close();
            if (!application.WaitForExit(10_000))
                throw new InvalidOperationException(
                    "Title close did not exit the packaged desktop.");
            checks["titleClose"] = true;

            object[] screenshotEvidence = screenshots
                .Select(item => AcceptanceScreenshotEvidence(
                    projectRoot,
                    item.Key,
                    item.Value))
                .ToArray();
            byte[] reportBytes =
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    artifactKind =
                        "sky-gui-023-packaged-acceptance",
                    surfaceId = "SKY-GUI-023",
                    verdict =
                        "PASS_STATIC_RUNTIME_REQUIRED",
                    runTag,
                    packagedExecutable = Relative(
                        projectRoot,
                        executable),
                    packagedExecutableSha256 =
                        HashAcceptanceFile(executable),
                    connectedSource = Relative(
                        projectRoot,
                        sourceRoot),
                    producedPackage = Relative(
                        projectRoot,
                        outputRoot),
                    checks,
                    independentAudit = Relative(
                        projectRoot,
                        rawReportPath),
                    negativeControl = Relative(
                        projectRoot,
                        negativeReportPath),
                    screenshots = screenshotEvidence,
                    staticAuthority = true,
                    equipmentAuthority = false,
                    runtimeAuthority = false,
                    visualAuthority = false
                },
                SavePackageAcceptanceJsonOptions);
            File.WriteAllBytes(reportPath, reportBytes);
            Console.WriteLine(reportPath);
            return 0;
        }
        finally
        {
            if (application is not null &&
                !application.HasExited)
            {
                try
                {
                    application.CloseMainWindow();
                    if (!application.WaitForExit(3_000))
                        application.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
            }
            application?.Dispose();
        }
    }

    private static void EnsureFreshAcceptancePaths(
        string executable,
        string sourceRoot,
        string cancelledOutput,
        string outputRoot,
        string reportPath,
        string rawReportPath,
        string negativeReportPath,
        string verifierPath,
        IEnumerable<string> screenshots)
    {
        foreach (string required in
                 new[]
                 {
                     executable,
                     Path.Combine(
                         sourceRoot,
                         "npcmanager-package.json"),
                     verifierPath
                 })
            if (!File.Exists(required))
                throw new FileNotFoundException(
                    "Required Gate 023 acceptance input is absent.",
                    required);
        foreach (string fresh in
                 new[]
                 {
                     cancelledOutput,
                     outputRoot,
                     reportPath,
                     rawReportPath,
                     negativeReportPath
                 }.Concat(screenshots))
            if (File.Exists(fresh) ||
                Directory.Exists(fresh))
                throw new IOException(
                    $"Acceptance output already exists: {fresh}");
    }

    private static AutomationElement FindAcceptanceElement(
        AutomationElement root,
        ControlType type,
        string name)
    {
        var condition = new AndCondition(
            new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                type),
            new PropertyCondition(
                AutomationElement.NameProperty,
                name));
        return root.FindFirst(
                   TreeScope.Descendants,
                   condition) ??
               throw new InvalidOperationException(
                   $"Required {type.ProgrammaticName} '{name}' was not found.");
    }

    private static void InvokeAcceptanceButton(
        AutomationElement root,
        string name)
    {
        AutomationElement button = FindAcceptanceElement(
            root,
            ControlType.Button,
            name);
        if (!button.Current.IsEnabled)
            throw new InvalidOperationException(
                $"Required button '{name}' is disabled.");
        ((InvokePattern)button.GetCurrentPattern(
            InvokePattern.Pattern)).Invoke();
    }

    private static void SetAcceptanceValue(
        AutomationElement root,
        string name,
        string value)
    {
        AutomationElement edit = FindAcceptanceElement(
            root,
            ControlType.Edit,
            name);
        var pattern = (ValuePattern)edit.GetCurrentPattern(
            ValuePattern.Pattern);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            pattern.SetValue(value);
            Thread.Sleep(75);
            if (string.Equals(
                    pattern.Current.Value,
                    value,
                    StringComparison.Ordinal))
                return;
        }
        throw new InvalidOperationException(
            $"UI Automation did not retain the exact {name} value. " +
            $"Actual: '{pattern.Current.Value}'.");
    }

    private static void SelectAcceptanceTab(
        AutomationElement root,
        string name)
    {
        AutomationElement tab = FindAcceptanceElement(
            root,
            ControlType.TabItem,
            name);
        ((SelectionItemPattern)tab.GetCurrentPattern(
            SelectionItemPattern.Pattern)).Select();
    }

    private static void SelectAcceptanceCombo(
        AutomationElement root,
        string comboName,
        string itemName)
    {
        AutomationElement combo = FindAcceptanceElement(
            root,
            ControlType.ComboBox,
            comboName);
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(
            ExpandCollapsePattern.Pattern);
        expand.Expand();
        try
        {
            AutomationElement? item = null;
            WaitUntil(
                () =>
                {
                    var condition = new AndCondition(
                        new PropertyCondition(
                            AutomationElement.ControlTypeProperty,
                            ControlType.ListItem),
                        new PropertyCondition(
                            AutomationElement.NameProperty,
                            itemName),
                        new PropertyCondition(
                            AutomationElement.ProcessIdProperty,
                            combo.Current.ProcessId));
                    item = AutomationElement.RootElement.FindFirst(
                        TreeScope.Descendants,
                        condition);
                    return item is not null;
                },
                $"Combo item '{itemName}' did not appear.",
                TimeSpan.FromSeconds(15));
            if (item!.TryGetCurrentPattern(
                    SelectionItemPattern.Pattern,
                    out object? selectionPattern))
            {
                ((SelectionItemPattern)selectionPattern).Select();
            }
            else
            {
                int itemIndex = AcceptanceChoiceIndex(itemName);
                combo.SetFocus();
                System.Windows.Forms.SendKeys.SendWait("{HOME}");
                for (int index = 0; index < itemIndex; index++)
                    System.Windows.Forms.SendKeys.SendWait("{DOWN}");
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            }
        }
        finally
        {
            try { expand.Collapse(); }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static int AcceptanceChoiceIndex(string itemName) =>
        itemName switch
        {
            "All changed NPCs" => 1,
            "Fresh-derived update" => 1,
            "UTF-8" => 1,
            "Skyrim BSA v105" => 2,
            "Append existing LVLN" => 2,
            _ => throw new InvalidOperationException(
                $"Gate 023 has no keyboard index for '{itemName}'.")
        };

    private static void SetAcceptanceToggle(
        AutomationElement root,
        string name,
        bool value)
    {
        AutomationElement check = FindAcceptanceElement(
            root,
            ControlType.CheckBox,
            name);
        var pattern = (TogglePattern)check.GetCurrentPattern(
            TogglePattern.Pattern);
        ToggleState wanted = value
            ? ToggleState.On
            : ToggleState.Off;
        if (pattern.Current.ToggleState != wanted)
            pattern.Toggle();
    }

    private static bool AcceptanceTextContains(
        AutomationElement root,
        string text)
    {
        var condition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Text);
        return root.FindAll(
                TreeScope.Descendants,
                condition)
            .Cast<AutomationElement>()
            .Any(item => item.Current.Name.Contains(
                text,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteAcceptanceAutomationSnapshot(
        AutomationElement root)
    {
        AutomationElementCollection elements = root.FindAll(
            TreeScope.Descendants,
            Condition.TrueCondition);
        foreach (AutomationElement element in elements)
        {
            ControlType type = element.Current.ControlType;
            if (type != ControlType.Text &&
                type != ControlType.Edit &&
                type != ControlType.Button)
                continue;
            string value = string.Empty;
            if (element.TryGetCurrentPattern(
                    ValuePattern.Pattern,
                    out object? valuePattern))
                value = ((ValuePattern)valuePattern).Current.Value;
            Console.WriteLine(
                $"UIA {type.ProgrammaticName}: " +
                $"name='{element.Current.Name}' value='{value}' " +
                $"enabled={element.Current.IsEnabled}");
        }
    }

    private static void ScrollAcceptance(
        AutomationElement root,
        double percent)
    {
        AutomationElementCollection elements = root.FindAll(
            TreeScope.Descendants,
            Condition.TrueCondition);
        foreach (AutomationElement element in elements)
        {
            if (!element.TryGetCurrentPattern(
                    ScrollPattern.Pattern,
                    out object? value))
                continue;
            var scroll = (ScrollPattern)value;
            if (!scroll.Current.VerticallyScrollable) continue;
            scroll.SetScrollPercent(
                ScrollPattern.NoScroll,
                percent);
            return;
        }
        throw new InvalidOperationException(
            "Save/package task exposed no vertical ScrollPattern.");
    }

    private static void SendAcceptanceAccessKey(
        IntPtr handle,
        string key)
    {
        _ = SkyGui023AcceptanceNative.SetForegroundWindow(handle);
        Thread.Sleep(150);
        System.Windows.Forms.SendKeys.SendWait("%" + key);
    }

    private static void CaptureAcceptanceWindow(
        IntPtr handle,
        string path)
    {
        if (!SkyGui023AcceptanceNative.GetWindowRect(
                handle,
                out SkyGui023AcceptanceNative.Rect rect))
            throw new InvalidOperationException(
                "Could not resolve packaged window bounds.");
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        Exception? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var bitmap = new Bitmap(width, height);
            try
            {
                using Graphics graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(
                    rect.Left,
                    rect.Top,
                    0,
                    0,
                    new Size(width, height));
                if (!InformativeAcceptanceBitmap(bitmap))
                    throw new InvalidDataException(
                        "Captured frame lacks informative pixels.");
                bitmap.Save(path, ImageFormat.Png);
                return;
            }
            catch (Exception exception) when (exception is
                ExternalException or InvalidDataException)
            {
                last = exception;
                Thread.Sleep(250);
            }
        }
        throw new InvalidOperationException(
            "Could not capture a nonblank packaged WPF frame.",
            last);
    }

    private static bool InformativeAcceptanceBitmap(Bitmap bitmap)
    {
        var colors = new HashSet<int>();
        for (int y = 40; y < bitmap.Height; y += 10)
            for (int x = 10; x < bitmap.Width; x += 10)
            {
                colors.Add(bitmap.GetPixel(x, y).ToArgb());
                if (colors.Count >= 10) return true;
            }
        return false;
    }

    private static object AcceptanceScreenshotEvidence(
        string projectRoot,
        string name,
        string path)
    {
        using var bitmap = new Bitmap(path);
        var colors = new HashSet<int>();
        for (int y = 20; y < bitmap.Height; y += 12)
            for (int x = 10; x < bitmap.Width; x += 12)
                colors.Add(bitmap.GetPixel(x, y).ToArgb());
        if (colors.Count < 10)
            throw new InvalidDataException(
                $"Accepted screenshot '{name}' is blank.");
        return new
        {
            name,
            path = Relative(projectRoot, path),
            sha256 = HashAcceptanceFile(path),
            byteLength = new FileInfo(path).Length,
            width = bitmap.Width,
            height = bitmap.Height,
            sampledDistinctColors = colors.Count,
            nonblank = true
        };
    }

    private static void RunAcceptanceVerifier(
        string verifier,
        string package,
        string source,
        string report,
        bool negative)
    {
        var arguments = new List<string>
        {
            verifier,
            "--package",
            package,
            "--source-package",
            source,
            "--report",
            report
        };
        if (negative)
        {
            arguments.Add("--plant-wrong-member-hash");
            arguments.Add("--expect-failure");
        }
        var start = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
                                throw new InvalidOperationException(
                                    "Independent Python verifier did not start.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException(
                $"Independent verifier failed ({process.ExitCode}). " +
                $"{output} {error}");
    }

    private static void WaitUntil(
        Func<bool> condition,
        string failure,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (condition()) return;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException(failure);
    }

    private static string HashAcceptanceFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string Relative(
        string root,
        string path) =>
        Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static class SkyGui023AcceptanceNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(
            IntPtr window,
            out Rect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveWindow(
            IntPtr window,
            int x,
            int y,
            int width,
            int height,
            [MarshalAs(UnmanagedType.Bool)] bool repaint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(
            IntPtr window);
    }
}

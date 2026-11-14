using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WakeMesh.Mobile.CloudUi.Tests;

/// <summary>
/// T91: closes the T89 report gaps (state marks, expandable details, weekday chips) with source
/// audits only — these run nothing and build nothing. They pin the wiring that the parent gate
/// then proves with a real build/test run.
/// </summary>
public sealed class T91CloudDesignCompletionTests
{
    private static readonly Lazy<string> Root = new(FindRoot);

    private static string Mobile(string relative) =>
        Path.Combine(Root.Value, "src", "WakeMesh.Mobile", relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "WakeMesh.Mobile")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/WakeMesh.Mobile was not found above the test output.");
    }

    private static XDocument LoadDictionary(string path)
    {
        var namespaces = new System.Xml.XmlNamespaceManager(new System.Xml.NameTable());
        namespaces.AddNamespace("x", "http://schemas.microsoft.com/winfx/2009/xaml");
        var context = new System.Xml.XmlParserContext(null, namespaces, null, System.Xml.XmlSpace.None);
        using var reader = System.Xml.XmlReader.Create(
            new StringReader(File.ReadAllText(path)),
            new System.Xml.XmlReaderSettings(),
            context);
        return XDocument.Load(reader);
    }

    [Fact]
    public void SharedStateIndicatorImplementsAllSevenSpecShapes()
    {
        var source = File.ReadAllText(Mobile("Controls/StateIndicator.cs"));

        foreach (var key in new[] { "ok", "pending", "unknown", "warn", "critical", "notRequired", "inactive" })
        {
            Assert.Contains($"\"{key}\"", source);
        }

        // §4: 12dp mark, 2dp stroke, 2,2 dashed unknown, 45° square for critical, 8×2 bar, — for notRequired.
        Assert.Contains("MarkSize = 12", source);
        Assert.Contains("StrokeThickness = 2", source);
        Assert.Contains("StrokeDashArray = new DoubleCollection { 2, 2 }", source);
        Assert.Contains("Rotation = 45", source);
        Assert.Contains("WidthRequest = 8", source);
        Assert.Contains("HeightRequest = 2", source);
        // T99: notRequired is drawn as a 1×8 rule instead of a "—" glyph so it cannot grow with the
        // user's font size; the branch itself is asserted in T99FinalDesignFixesTests.
        Assert.Contains("HeightRequest = 1", source);
        Assert.DoesNotContain("Text = \"—\"", source);

        // Colours come from the shared tokens, never from a page.
        foreach (var token in new[] { "\"Ok\"", "\"Warn\"", "\"Crit\"", "\"Ink2\"", "\"Ink3\"" })
        {
            Assert.Contains(token, source);
        }

        Assert.DoesNotContain("#", source.Replace("#B7B3AA", string.Empty).Replace("#6CC290", string.Empty)
            .Replace("#E8A93A", string.Empty).Replace("#F2766B", string.Empty).Replace("#8F8B83", string.Empty));
    }

    [Fact]
    public void CloudPageMarksDeviceAndTargetStatesWithTheSharedControl()
    {
        var xaml = File.ReadAllText(Mobile("Views/CloudManagementPage.xaml"));

        Assert.Contains("xmlns:controls=\"clr-namespace:WakeMesh.Mobile.Controls\"", xaml);
        Assert.Contains("StateKey=\"{Binding StateKey}\"", xaml);
        Assert.Contains("StateKey=\"{Binding ReflectionStateKey}\"", xaml);
        Assert.Contains("Text=\"未反映の端末あり\"", xaml);
        Assert.Contains("IsVisible=\"{Binding HasUnappliedTargets}\"", xaml);
        Assert.Contains("StateKeyToShortLabelConverter", xaml);
        // The unapplied warn tag and the reauth banner are the only warn marks the page shows by itself.
        Assert.Contains("StateKey=\"critical\"", xaml);
    }

    [Fact]
    public void CloudPageRowsConfirmBeforeExecutingAnyDestructiveCommand()
    {
        var code = File.ReadAllText(Mobile("Views/CloudManagementPage.xaml.cs"));
        var xaml = File.ReadAllText(Mobile("Views/CloudManagementPage.xaml"));

        // Row tap opens the editor from the row's own binding context (§5 S3).
        Assert.Contains("Tapped=\"OnGroupTapped\"", xaml);
        Assert.Contains("(sender as BindableObject)?.BindingContext as CloudAlarmGroupItemViewModel", code);

        // Every destructive path: confirm first, execute only after the user accepted.
        var confirmations = Regex.Matches(code, @"if \(!confirmed\) return;\s*\r?\n\s*RunCommandSafely").Count;
        Assert.True(confirmations >= 4, $"expected 4 confirmed-then-execute paths, found {confirmations}");

        // No command is executed before its dialog: no Execute( call may precede a ConfirmAsync in a handler.
        foreach (var handler in new[] { "OnGroupDeleteClickedAsync", "OnRetireClickedAsync", "OnRevokeClickedAsync", "OnResetRecoveryClicked" })
        {
            var start = code.IndexOf(handler, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{handler} is missing");
            var block = code[start..Math.Min(code.Length, start + 900)];
            var confirmIndex = block.IndexOf("ConfirmAsync", StringComparison.Ordinal);
            var executeIndex = block.IndexOf("RunCommandSafely", StringComparison.Ordinal);
            Assert.True(confirmIndex >= 0 && executeIndex > confirmIndex, $"{handler} executes before confirming");
        }

        // Failures surface through the existing StatusMessage entry instead of being swallowed.
        Assert.Contains("_viewModel.StatusMessage = $\"操作に失敗しました", code);
    }

    [Fact]
    public void GroupEditorWeekdayChipsFollowDayFlagsWithAdaptiveColumns()
    {
        var xaml = File.ReadAllText(Mobile("Views/CloudAlarmGroupEditPage.xaml"));
        var code = File.ReadAllText(Mobile("Views/CloudAlarmGroupEditPage.xaml.cs"));

        foreach (var day in new[] { "DayMonday", "DayTuesday", "DayWednesday", "DayThursday", "DayFriday", "DaySaturday", "DaySunday" })
        {
            Assert.Contains($"{{Binding {day}, Converter={{StaticResource DaySelectedColorConverter}}}}", xaml);
            Assert.Contains($"{{Binding {day}, Converter={{StaticResource DaySelectedTextColorConverter}}}}", xaml);
            Assert.Contains($"Binding=\"{{Binding {day}}}\" Value=\"True\"", xaml);
        }

        // 48dp minimum target, 72dp chip, bold on top of colour so greyscale still reads (§3, §8-4).
        Assert.Contains("<Setter Property=\"MinimumHeightRequest\" Value=\"72\" />", xaml);
        Assert.Contains("<Setter Property=\"MinimumWidthRequest\" Value=\"48\" />", xaml);
        Assert.Contains("<Setter Property=\"FontAttributes\" Value=\"Bold\" />", xaml);

        // Under 480dp: 4 columns × 2 rows; 480dp and wider: 7 columns × 1 row.
        Assert.Contains("<AdaptiveTrigger MinWindowWidth=\"480\" />", xaml);
        Assert.Contains("ColumnDefinitions=\"*,*,*,*\"", xaml);
        Assert.Contains("ColumnDefinitions=\"*,*,*,*,*,*,*\"", xaml);
        Assert.Contains("RowDefinitions=\"Auto,Auto\"", xaml);

        // The existing selected-day logic stays in code-behind (save reads the same flags).
        foreach (var day in new[] { "DayMonday", "DayTuesday", "DayWednesday", "DayThursday", "DayFriday", "DaySaturday", "DaySunday" })
        {
            Assert.Contains($"_viewModel.{day} = !_viewModel.{day};", code);
        }
    }

    [Fact]
    public void GroupEditorKeepsFixedBarWithSaveCancelAndInlineError()
    {
        var xaml = File.ReadAllText(Mobile("Views/CloudAlarmGroupEditPage.xaml"));

        Assert.Contains("RowDefinitions=\"*,Auto\"", xaml);
        Assert.Contains("Text=\"キャンセル\"", xaml);
        Assert.Contains("Command=\"{Binding CancelCommand}\"", xaml);
        Assert.Contains("Text=\"クラウドへ保存\"", xaml);
        Assert.Contains("Command=\"{Binding SaveCommand}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding ErrorMessage, Converter={StaticResource StringNotEmptyToBoolConverter}}\"", xaml);

        // §5 S4: the fixed "保存完了" title and the ETag in the body are gone; only the VM text is shown.
        Assert.DoesNotContain("サーバー保存完了", xaml);
        Assert.Contains("Text=\"{Binding SaveOutcomeNotice}\"", xaml);
        Assert.Contains("Text=\"{Binding ConflictMessage}\"", xaml);
    }

    [Fact]
    public void EveryCloudPageBindingResolvesToAPublicViewModelMember()
    {
        var pageBindings = new (string Page, string[] ViewModels)[]
        {
            ("Views/CloudManagementPage.xaml", new[]
            {
                "ViewModels/CloudManagementViewModel.cs",
                "ViewModels/CloudAlarmGroupItemViewModel.cs",
                "ViewModels/CloudAlarmTargetItemViewModel.cs",
                "ViewModels/CloudDeviceItemViewModel.cs"
            }),
            ("Views/CloudAlarmGroupEditPage.xaml", new[]
            {
                "ViewModels/CloudAlarmGroupEditViewModel.cs",
                "ViewModels/CloudAlarmTargetItemViewModel.cs",
                "ViewModels/CloudDeviceItemViewModel.cs"
            })
        };

        var failures = new List<string>();
        foreach (var (page, viewModels) in pageBindings)
        {
            var text = File.ReadAllText(Mobile(page));
            var members = string.Join(
                "\n",
                viewModels.Select(vm => File.ReadAllText(Mobile(vm))));

            var names = Regex.Matches(text, @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal);

            foreach (var name in names)
            {
                if (!Regex.IsMatch(members, $@"public\s+[^\r\n]{{0,160}}?\b{name}\b\s*(?:\{{|=>|\()"))
                {
                    failures.Add($"{Path.GetFileName(page)}: {name}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Bindings with no public member:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void CloudPagesParseAndOnlyUseRegisteredResources()
    {
        var app = XDocument.Load(Mobile("App.xaml"));
        // Colors.xaml / Styles.xaml use x:Key and rely on the MAUI XAML compiler's implicit x
        // namespace; a strict XmlReader rejects the prefix. That is a reader limitation, not a
        // product defect, so the dictionary files get the same tolerant loader the existing
        // XamlResourceResolutionTests uses. Pages still parse strictly, so a real malformed page
        // is still a failure.
        var colours = LoadDictionary(Mobile("Resources/Styles/Colors.xaml"));
        var styles = LoadDictionary(Mobile("Resources/Styles/Styles.xaml"));

        var globalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in new[] { app, colours, styles })
        {
            foreach (var element in document.Descendants())
            {
                var key = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Key")?.Value;
                if (key is not null) globalKeys.Add(key);
            }
        }

        var missing = new List<string>();
        foreach (var page in new[] { "Views/CloudManagementPage.xaml", "Views/CloudAlarmGroupEditPage.xaml" })
        {
            var document = XDocument.Load(Mobile(page));
            var keys = new HashSet<string>(globalKeys, StringComparer.Ordinal);
            foreach (var element in document.Descendants())
            {
                var key = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Key")?.Value;
                if (key is not null) keys.Add(key);
            }

            foreach (var reference in Regex.Matches(document.ToString(), @"\{StaticResource\s+([^,}\s]+)")
                         .Cast<Match>()
                         .Select(match => match.Groups[1].Value)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!keys.Contains(reference)) missing.Add($"{Path.GetFileName(page)}: {reference}");
            }
        }

        Assert.True(missing.Count == 0, "Unresolved StaticResource references:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void StateShortLabelsMatchTheThemisVocabulary()
    {
        var source = File.ReadAllText(Mobile("Converters/StateDisplayConverters.cs"));

        foreach (var label in new[] { "反映済み", "反映待ち", "同期状況を確認できません", "端末での解除待ち", "不要", "登録解除済み" })
        {
            Assert.Contains(label, source);
        }

        // The keys stay exactly as the view models produce them.
        foreach (var key in new[] { "\"ok\"", "\"pending\"", "\"unknown\"", "\"warn\"", "\"critical\"", "\"notRequired\"", "\"inactive\"" })
        {
            Assert.Contains(key, source);
        }
    }
}

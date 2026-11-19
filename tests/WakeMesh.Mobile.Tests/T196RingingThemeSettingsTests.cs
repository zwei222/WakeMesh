using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.Tests;

/// <summary>
/// T196: ringing background theme — shared contract (RingingThemes / IRingingThemeStore / palette),
/// the settings ViewModel surface (RingingThemeId + SelectRingingThemeCommand + error property) and the
/// Android store source contract. The approved palette fixture that ships with the tests
/// (Fixtures/ringing-theme-tokens.json) is the single source of truth here; these tests assert only
/// against that checked-in fixture, so a fresh clone runs green without any out-of-repo file.
/// </summary>
public sealed class T196RingingThemeSettingsTests
{
    private const string SaveErrorMessage = "背景を保存できませんでした。もう一度お試しください。";

    private static readonly string Root = FindRoot();

    private static string TokensJson =>
        Path.Combine(Root, "tests/WakeMesh.Mobile.Tests/Fixtures/ringing-theme-tokens.json");

    private static string AndroidStoreSource =>
        File.ReadAllText(Path.Combine(Root, "src/WakeMesh.Mobile/Platforms/Android/SharedPreferencesRingingThemeStore.cs"));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "WakeMesh.Mobile"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("repository root not found");
    }

    // ------------------------------------------------------------------ fake store

    private sealed class FakeRingingThemeStore : IRingingThemeStore
    {
        private string _value;
        private bool _failRead;
        private bool _failWrite;

        public FakeRingingThemeStore(string value = RingingThemes.Sunrise) => _value = value;

        public bool FailRead { get => _failRead; set => _failRead = value; }
        public bool FailWrite { get => _failWrite; set => _failWrite = value; }
        public int GetCount { get; private set; }
        public List<string> Writes { get; } = new();

        public string Get()
        {
            GetCount++;
            if (_failRead) throw new InvalidOperationException("read failed (simulated SharedPreferences failure)");
            return _value;
        }

        public void Set(string id)
        {
            if (_failWrite) throw new InvalidOperationException("commit failed (simulated SharedPreferences.Commit() == false)");
            Writes.Add(id);
            _value = RingingThemes.Normalize(id);
        }
    }

    private static DiagnosticsViewModel NewVm(IRingingThemeStore? store = null) =>
        new(new MockPermissionService(), new JapaneseHolidayService(), ringingThemeStore: store);

    // ------------------------------------------------------------------ shared contract

    [Fact]
    public void RingingThemes_All_IsFourIds_InSpecOrder()
    {
        Assert.Equal(new[] { "sunrise", "sky", "forest", "night" }, RingingThemes.All.ToArray());
        Assert.Equal("sunrise", RingingThemes.Sunrise);
        Assert.Equal("sky", RingingThemes.Sky);
        Assert.Equal("forest", RingingThemes.Forest);
        Assert.Equal("night", RingingThemes.Night);
    }

    [Fact]
    public void RingingThemes_PreferenceKey_IsTheFinalContractValue()
    {
        Assert.Equal("ringing_background_theme", RingingThemes.PreferenceKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sunrise")]
    [InlineData("SUNRISE")]
    [InlineData("Night")]
    [InlineData("dark")]
    [InlineData("ringing_background_theme")]
    public void RingingThemes_Normalize_UnknownNullOrCaseVariant_IsSunrise(string? stored)
    {
        Assert.Equal("sunrise", RingingThemes.Normalize(stored));
    }

    [Theory]
    [InlineData("sunrise", "朝焼け")]
    [InlineData("sky", "青空")]
    [InlineData("forest", "木漏れ日")]
    [InlineData("night", "夜")]
    [InlineData("unknown", "朝焼け")]
    [InlineData(null, "朝焼け")]
    public void RingingThemes_Label_IsTheJapaneseName(string? id, string expected)
    {
        Assert.Equal(expected, RingingThemes.Label(id));
    }

    [Theory]
    [InlineData("sunrise")]
    [InlineData("sky")]
    [InlineData("forest")]
    [InlineData("night")]
    [InlineData("dark")]
    [InlineData(null)]
    public void RingingThemePalette_For_IdMatchesNormalize(string? id)
    {
        Assert.Equal(RingingThemes.Normalize(id), RingingThemePalette.For(id).Id);
    }

    [Fact]
    public void RingingThemePalette_SunriseSkyForest_ShareBrandPrimaryAndDifferInBackgroundAndSecondary()
    {
        Assert.Same(RingingThemePalette.Sunrise.Primary, RingingThemePalette.Sky.Primary);
        Assert.Same(RingingThemePalette.Sunrise.Primary, RingingThemePalette.Forest.Primary);
        Assert.NotSame(RingingThemePalette.Sunrise.Secondary, RingingThemePalette.Sky.Secondary);
        Assert.True(RingingThemePalette.Sunrise.LightSystemBars);
        Assert.True(RingingThemePalette.Sky.LightSystemBars);
        Assert.True(RingingThemePalette.Forest.LightSystemBars);
        Assert.False(RingingThemePalette.Night.LightSystemBars);
        Assert.Equal("#F1EEE7", RingingThemePalette.Night.Ink);
    }

    [Fact]
    public void RingingThemePalette_MatchesDesignTokensJson_ForAllFourThemes()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TokensJson));
        var root = doc.RootElement;
        Assert.Equal(RingingThemes.All.ToArray(),
            root.GetProperty("order").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(RingingThemes.Sunrise, root.GetProperty("default").GetString());

        var themes = root.GetProperty("themes");
        foreach (var id in RingingThemes.All)
        {
            var token = themes.GetProperty(id);
            var palette = RingingThemePalette.For(id);

            Assert.Equal(id, palette.Id);
            Assert.Equal(token.GetProperty("label").GetString(), RingingThemes.Label(id));
            Assert.Equal(token.GetProperty("window").GetString(), palette.Window);
            Assert.Equal(token.GetProperty("lightSystemBars").GetBoolean(), palette.LightSystemBars);

            var baseColors = token.GetProperty("base").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(baseColors[0], palette.BgTop);
            Assert.Equal(baseColors[1], palette.BgMid);
            Assert.Equal(baseColors[2], palette.BgBot);

            Assert.Equal(token.GetProperty("glowA").GetString(), palette.GlowA);
            Assert.Equal((float)token.GetProperty("alphaA").GetDouble(), palette.AlphaA, 4);
            Assert.Equal((float)token.GetProperty("dAlphaA").GetDouble(), palette.DAlphaA, 4);
            Assert.Equal(token.GetProperty("glowB").GetString(), palette.GlowB);
            Assert.Equal((float)token.GetProperty("alphaB").GetDouble(), palette.AlphaB, 4);
            Assert.Equal((float)token.GetProperty("dAlphaB").GetDouble(), palette.DAlphaB, 4);

            Assert.Equal(token.GetProperty("ink").GetString(), palette.Ink);
            Assert.Equal(token.GetProperty("ink2").GetString(), palette.Ink2);
            Assert.Equal(token.GetProperty("ink3").GetString(), palette.Ink3);
            Assert.Equal(token.GetProperty("statusInk").GetString(), palette.StatusInk);
            Assert.Equal(token.GetProperty("card").GetString(), palette.Card);
            Assert.Equal(token.GetProperty("cardStroke").GetString(), palette.CardStroke);
            Assert.Equal(token.GetProperty("allStopBg").GetString(), palette.AllStopBg);
            Assert.Equal(token.GetProperty("allStopStroke").GetString(), palette.AllStopStroke);
            Assert.Equal(token.GetProperty("allStopText").GetString(), palette.AllStopText);

            AssertSlide(token.GetProperty("slide").GetProperty("primary"), palette.Primary);
            AssertSlide(token.GetProperty("slide").GetProperty("secondary"), palette.Secondary);
        }
    }

    private static void AssertSlide(JsonElement token, RingingSlideColors actual)
    {
        Assert.Equal(token.GetProperty("track").GetString(), actual.Track);
        Assert.Equal(token.GetProperty("label").GetString(), actual.Label);
        Assert.Equal(token.GetProperty("fill").GetString(), actual.Fill);
        Assert.Equal(token.GetProperty("labelOnFill").GetString(), actual.LabelOnFill);
        Assert.Equal(token.GetProperty("knob").GetString(), actual.Knob);
        Assert.Equal(token.GetProperty("arrow").GetString(), actual.Arrow);
    }

    [Fact]
    public void InMemoryRingingThemeStore_DefaultsToSunrise_AndNormalizesOnSet()
    {
        var store = new InMemoryRingingThemeStore();
        Assert.Equal("sunrise", store.Get());

        store.Set("night");
        Assert.Equal("night", store.Get());

        store.Set(" Night ");
        Assert.Equal("sunrise", store.Get());
    }

    // ------------------------------------------------------------------ ViewModel: read

    [Fact]
    public void Vm_WithoutStore_FallsBackToSunrise()
    {
        var vm = new DiagnosticsViewModel(new MockPermissionService(), new JapaneseHolidayService());
        Assert.Equal("sunrise", vm.RingingThemeId);
        Assert.Null(vm.RingingThemeErrorMessage);
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("")]
    [InlineData("Night")]
    public void Vm_UnknownStoredValue_ShowsSunrise_WithoutRewritingTheStore(string stored)
    {
        var store = new FakeRingingThemeStore(stored);
        var vm = NewVm(store);

        Assert.Equal("sunrise", vm.RingingThemeId);
        Assert.Empty(store.Writes);
    }

    [Theory]
    [InlineData("sunrise")]
    [InlineData("sky")]
    [InlineData("forest")]
    [InlineData("night")]
    public void Vm_EachValidStoredValue_IsShown(string stored)
    {
        var vm = NewVm(new FakeRingingThemeStore(stored));
        Assert.Equal(stored, vm.RingingThemeId);
    }

    [Fact]
    public void Vm_ReadsTheStoreOnce_AtConstruction_AndNotOnEveryPropertyRead()
    {
        var store = new FakeRingingThemeStore("forest");
        var vm = NewVm(store);

        _ = vm.RingingThemeId;
        _ = vm.RingingThemeId;

        Assert.Equal(1, store.GetCount);
    }

    [Fact]
    public void Vm_StoreGetThrows_StillBuildsTheScreenWithSunrise()
    {
        var store = new FakeRingingThemeStore("night") { FailRead = true };

        var vm = NewVm(store); // must not throw: the ringing/settings screen stays alive

        Assert.Equal("sunrise", vm.RingingThemeId);
        Assert.Equal(1, store.GetCount);
    }

    [Fact]
    public void Vm_DoesNotDependOnAuthInitialization_ToReadOrWriteTheTheme()
    {
        // No IAuthService supplied at all: the theme must still come from the store (ctor ordering).
        var store = new FakeRingingThemeStore("sky");
        var vm = new DiagnosticsViewModel(new MockPermissionService(), new JapaneseHolidayService(),
            deviceInfoProvider: null, alarmCoordinator: null, authService: null, ringingThemeStore: store);

        Assert.Equal("sky", vm.RingingThemeId);
        Assert.Null(vm.RingingThemeErrorMessage);
    }

    // ------------------------------------------------------------------ ViewModel: write

    [Fact]
    public void Vm_SelectCommand_PersistsAllFourThemes_AndShowsThem()
    {
        var store = new FakeRingingThemeStore();
        var vm = NewVm(store);

        foreach (var id in RingingThemes.All)
        {
            vm.SelectRingingThemeCommand.Execute(id);
            Assert.Equal(id, vm.RingingThemeId);
            Assert.Equal(id, store.Get());
        }

        Assert.Equal(new[] { "sky", "forest", "night" }, store.Writes.ToArray());
    }

    [Fact]
    public void Vm_ReconstructedAfterSelection_ReadsBackTheStoredValue()
    {
        var store = new FakeRingingThemeStore();
        NewVm(store).SelectRingingThemeCommand.Execute("night");

        var rebuilt = NewVm(store);

        Assert.Equal("night", rebuilt.RingingThemeId);
    }

    [Fact]
    public void Vm_SelectingTheAlreadySelectedValue_DoesNotWrite()
    {
        var store = new FakeRingingThemeStore("sky");
        var vm = NewVm(store);

        vm.SelectRingingThemeCommand.Execute("sky");

        Assert.Empty(store.Writes);
        Assert.Equal("sky", vm.RingingThemeId);
    }

    [Fact]
    public void Vm_SelectingAValidTheme_RaisesRingingThemeIdExactlyOnce()
    {
        var vm = NewVm(new FakeRingingThemeStore());
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectRingingThemeCommand.Execute("sky");

        Assert.Equal(new[] { nameof(DiagnosticsViewModel.RingingThemeId) }, raised);
    }

    [Fact]
    public void Vm_SelectingAnUnknownValue_NormalizesToSunrise_SoNothingIsWrittenFromSunrise()
    {
        var store = new FakeRingingThemeStore();
        var vm = NewVm(store);

        vm.SelectRingingThemeCommand.Execute("dark");

        Assert.Equal("sunrise", vm.RingingThemeId);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void Vm_StoreWriteThrows_KeepsThePreviousSelection_AndReportsTheError()
    {
        var store = new FakeRingingThemeStore("sunrise") { FailWrite = true };
        var vm = NewVm(store);

        vm.SelectRingingThemeCommand.Execute("night"); // must not throw

        Assert.Equal("sunrise", vm.RingingThemeId);
        Assert.Equal(SaveErrorMessage, vm.RingingThemeErrorMessage);
        Assert.Equal("sunrise", store.Get());
    }

    [Fact]
    public void Vm_WriteFailure_ThenSuccessfulWrite_ClearsTheErrorAndMovesTheSelection()
    {
        var store = new FakeRingingThemeStore() { FailWrite = true };
        var vm = NewVm(store);
        vm.SelectRingingThemeCommand.Execute("forest");
        Assert.Equal(SaveErrorMessage, vm.RingingThemeErrorMessage);

        store.FailWrite = false;
        vm.SelectRingingThemeCommand.Execute("forest");

        Assert.Null(vm.RingingThemeErrorMessage);
        Assert.Equal("forest", vm.RingingThemeId);
        Assert.Equal("forest", store.Get());
    }

    [Fact]
    public void Vm_AfterWriteFailure_ReselectingTheCurrentValue_ClearsTheErrorWithoutWriting()
    {
        var store = new FakeRingingThemeStore("sunrise") { FailWrite = true };
        var vm = NewVm(store);
        vm.SelectRingingThemeCommand.Execute("night");
        Assert.Equal(SaveErrorMessage, vm.RingingThemeErrorMessage);

        vm.SelectRingingThemeCommand.Execute("sunrise");

        Assert.Null(vm.RingingThemeErrorMessage);
        Assert.Empty(store.Writes);
        Assert.Equal("sunrise", vm.RingingThemeId);
    }

    [Fact]
    public void Vm_SelectCommand_IsExposedAsRelayCommand_ForTheTileMarkup()
    {
        var vm = NewVm(new FakeRingingThemeStore());
        Assert.IsType<RelayCommand>(vm.SelectRingingThemeCommand);
        Assert.True(vm.SelectRingingThemeCommand.CanExecute("sky"));
    }

    // ------------------------------------------------------------------ Android store (source audit)

    [Fact]
    public void AndroidStore_UsesDedicatedSharedPreferencesFileAndKey()
    {
        var source = AndroidStoreSource;

        Assert.Contains("wakemesh.ringing", source);
        Assert.Contains("RingingThemes.PreferenceKey", source);
        Assert.Contains("FileCreationMode.Private", source);
        Assert.Contains("namespace WakeMesh.Mobile.Platforms.Android;", source);
        Assert.DoesNotContain("using Microsoft.Maui", source);
        Assert.DoesNotContain("Preferences.Default", source);
        Assert.DoesNotContain("Preferences.Get", source);
    }

    [Fact]
    public void AndroidStore_ReadIsSynchronousAndFallsBackToSunriseOnAnyFailure()
    {
        var source = AndroidStoreSource;

        Assert.Contains("public static string Read(Context context)", source);
        Assert.Contains("RingingThemes.Normalize", source);
        Assert.Contains("catch", source);
        Assert.Contains("RingingThemes.Sunrise", source);
    }

    [Fact]
    public void AndroidStore_SetVerifiesCommitAndThrowsInsteadOfSwallowing()
    {
        var source = AndroidStoreSource;

        Assert.Contains("Commit()", source);
        Assert.DoesNotContain(".Apply()", source);
        Assert.Contains("throw", source);
    }
}

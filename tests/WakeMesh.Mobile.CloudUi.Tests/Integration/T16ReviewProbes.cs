using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Maui.Controls;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Client.Sync;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Models;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.ViewModels;
using Xunit;

namespace WakeMesh.Mobile.CloudUi.Tests.Integration;

public sealed class T16ReviewProbes : IClassFixture<CloudUiTestApiFactory>
{
    private readonly CloudUiTestApiFactory api;
    public T16ReviewProbes(CloudUiTestApiFactory api) => this.api = api;

    private static string NewDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "t16-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static AlarmCoordinator Coordinator(string dir) =>
        new(new JsonFileAlarmStore(Path.Combine(dir, "alarms.json")),
            new AlarmScheduleCalculator(new FakeHolidayService()),
            new FakeAlarmScheduler());

    private static string SourceRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/WakeMesh.Mobile"));

    [Fact]
    public void Verify_UnregisteredPanelVisible_WhenNotRegistered_WithActualMauiBinding()
    {
        var x = XDocument.Load(Path.Combine(SourceRoot, "Views/CloudManagementPage.xaml"));
        var unregisteredNode = x.Descendants().Single(n =>
            n.Name.LocalName == "VerticalStackLayout" && (string?)n.Attribute("IsVisible") == "{Binding NotRegistered}");
        Assert.NotNull(unregisteredNode);

        var registeredNode = x.Descendants().Single(n =>
            n.Name.LocalName == "VerticalStackLayout" && (string?)n.Attribute("IsVisible") == "{Binding IsRegistered}");
        Assert.NotNull(registeredNode);

        // Test with real MAUI Binding and Actual ViewModel Properties
        var unregPanel = new VerticalStackLayout();
        unregPanel.SetBinding(VisualElement.IsVisibleProperty, new Binding("NotRegistered"));

        var regPanel = new VerticalStackLayout();
        regPanel.SetBinding(VisualElement.IsVisibleProperty, new Binding("IsRegistered"));

        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", api.Auth.CreateAccessToken());
        using var service = new CloudManagementService(auth, new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json")), null, Coordinator(dir), customStorageDir: dir);
        var vm = new CloudManagementViewModel(service, auth);

        unregPanel.BindingContext = vm;
        regPanel.BindingContext = vm;

        // Initially not registered
        Assert.True(vm.NotRegistered);
        Assert.False(vm.IsRegistered);
        Assert.True(unregPanel.IsVisible);
        Assert.False(regPanel.IsVisible);
    }

    [Fact]
    public void Verify_NoReflectionGoToAsync_AndTypedNavigationAbstractionUsed()
    {
        var vmSource = File.ReadAllText(Path.Combine(SourceRoot, "ViewModels/CloudManagementViewModel.cs"));
        Assert.DoesNotContain("GetMethod(\"GoToAsync\"", vmSource);
        Assert.DoesNotContain("GetMethod(\"GoToAsync\", new[] { typeof(string) })", vmSource);

        var editSource = File.ReadAllText(Path.Combine(SourceRoot, "ViewModels/CloudAlarmGroupEditViewModel.cs"));
        Assert.DoesNotContain("GetMethod(\"GoToAsync\"", editSource);
        Assert.DoesNotContain("GetMethod(\"GoToAsync\", new[] { typeof(string) })", editSource);

        // Verify ICloudNavigation interface exists and is used
        Assert.NotNull(typeof(ICloudNavigation).GetMethod("GoToAsync", new[] { typeof(string) }));
    }

    [Fact]
    public async Task Verify_LabelOnlySavePreservesExcludedDates_AcrossApiRoundTrip()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", api.Auth.CreateAccessToken());
        var creds = new SecureDeviceCredentialStore(new StubSecureStorageProvider(), () => auth.CurrentBinding);
        var client = TestClientHelper.CreateApiClient(api.CreateClient(), auth, creds);
        using var service = new CloudManagementService(auth, new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json")), creds, Coordinator(dir), () => client, dir);

        var first = await service.RegisterCurrentDeviceAsync("synthetic-first");
        var second = await client.RegisterDeviceAsync("synthetic-second", persistCredential: false);
        Assert.True(first.IsSuccess && second.IsSuccess);

        var edit = new CloudAlarmGroupEditViewModel(service);
        await edit.InitializeAsync();
        edit.Label = "synthetic-group";
        edit.Targets[0].TimeOfDay = new TimeSpan(6, 15, 0);
        edit.SelectedDeviceToAdd = edit.AvailableDevices.Single(x => x.Id == second.Value!.Id);
        edit.AddTargetCommand.Execute(null);
        Assert.True(edit.Targets.Count == 2);
        edit.Targets[1].TimeOfDay = new TimeSpan(7, 45, 0);
        edit.ExcludeHolidays = false;
        await edit.SaveCommand.ExecuteAsync(null);
        Assert.Null(edit.ErrorMessage);

        var saved = (await client.ListAlarmGroupsAsync()).Value!.Single(x => x.Id == edit.GroupId);
        Assert.False(saved.ExcludeHolidays);

        // Write an external exclusion date
        var externalWrite = new AlarmGroupWriteRequest(
            saved.Id,
            saved.Label,
            saved.Enabled,
            saved.Repeat,
            saved.Days,
            saved.Date,
            saved.TimeZone,
            true,
            new[] { "2026-12-25" },
            saved.Targets);
        Assert.True((await client.ReplaceAlarmGroupAsync(saved.Id, externalWrite, saved.Version)).IsSuccess);

        // Reload into edit view model
        await edit.InitializeAsync(saved.Id);
        Assert.True(edit.ExcludeHolidays);
        Assert.Contains("2026-12-25", edit.ExcludedDates);

        // Make label-only change
        edit.Label = "label-only-change";
        await edit.SaveCommand.ExecuteAsync(null);
        Assert.Null(edit.ErrorMessage);

        // Confirm excluded dates were strictly preserved on the server
        var after = (await client.ListAlarmGroupsAsync()).Value!.Single(x => x.Id == edit.GroupId);
        Assert.True(after.ExcludeHolidays);
        Assert.NotEmpty(after.ExcludedDates);
        Assert.Contains("2026-12-25", after.ExcludedDates);

        var desired = await client.GetDesiredAsync(first.Value!.Id);
        Assert.True(desired.IsSuccess);
        Assert.True(desired.Value!.Items.Single(x => x.GroupId == edit.GroupId).ExcludeHolidays);
    }

    [Fact]
    public async Task Verify_SettingsChangeAfterRegisterHeaders_PreservesOldScopeAndCredential()
    {
        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", api.Auth.CreateAccessToken());
        var oldSettings = auth.Settings;
        var oldBinding = auth.CurrentBinding;
        var storage = new StubSecureStorageProvider();
        var creds = new SecureDeviceCredentialStore(storage, () => auth.CurrentBinding);
        var gate = new RegisterGateHandler { InnerHandler = api.Server.CreateHandler() };
        using var http = new HttpClient(gate) { BaseAddress = new Uri("https://api.wakemesh.local") };
        var client = TestClientHelper.CreateApiClient(http, auth, creds);
        var store = new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json"));
        using var service = new CloudManagementService(auth, store, creds, Coordinator(dir), () => client, dir);

        var pending = service.RegisterCurrentDeviceAsync("synthetic-race");
        await gate.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Setting flip mid-flight
        auth.SetSettings(
            new AuthSettings
            {
                ApiBaseUrl = "https://other.invalid",
                ApiClientId = Guid.NewGuid().ToString(),
                AndroidClientId = Guid.NewGuid().ToString()
            },
            "synthetic-account-two");

        gate.Release.TrySetResult();

        // The caller receives OperationCanceledException because the cancellation token was tripped
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);

        // However, the registration and credential are NOT orphaned under the new scope:
        // 1. The store contains the registration safely recorded under the old scope
        var savedReg = await store.LoadRegistrationAsync();
        Assert.NotNull(savedReg);
        Assert.Equal("https://api.wakemesh.local", savedReg.ApiBaseUrl);
        Assert.Equal(oldBinding.HomeAccountId, savedReg.AccountId);

        // 2. The credential is NOT visible under the new scope
        Assert.True(string.IsNullOrEmpty(await creds.GetCredentialAsync(savedReg.ServerDeviceId)));

        // 3. The credential IS preserved under the old immutable scope
        var oldCredStore = new SecureDeviceCredentialStore(storage, () => oldBinding);
        var oldCred = await oldCredStore.GetCredentialAsync(savedReg.ServerDeviceId);
        Assert.False(string.IsNullOrEmpty(oldCred));
    }

    [Fact]
    public async Task Verify_TypedNavigationCommand_InvokesInjectedNavigation()
    {
        string? navigatedRoute = null;
        var nav = new ActionCloudNavigation(route => navigatedRoute = route);

        var dir = NewDir();
        var auth = new MockAuthService("https://api.wakemesh.local", api.Auth.CreateAccessToken());
        var client = TestClientHelper.CreateApiClient(api.CreateClient(), auth);
        using var service = new CloudManagementService(auth, new FileCloudDeviceRegistrationStore(Path.Combine(dir, "reg.json")), null, Coordinator(dir), () => client, dir);

        var vm = new CloudManagementViewModel(service, auth, nav);
        Assert.True(vm.NavigateToCreateGroupCommand.CanExecute(null));

        await vm.NavigateToCreateGroupCommand.ExecuteAsync(null);
        Assert.Equal("CloudAlarmGroupEditPage", navigatedRoute);

        await vm.NavigateToSettingsCommand.ExecuteAsync(null);
        Assert.Equal("SettingsPage", navigatedRoute);

        var editVm = new CloudAlarmGroupEditViewModel(service, nav);
        await editVm.CancelCommand.ExecuteAsync(null);
        Assert.Equal("..", navigatedRoute);
    }

    private sealed class RegisterGateHandler : DelegatingHandler
    {
        public TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/devices")
            {
                response.Content = new GatedContent(response.Content, BodyStarted, Release);
            }
            return response;
        }
    }

    private sealed class GatedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly TaskCompletionSource started, release;

        public GatedContent(HttpContent inner, TaskCompletionSource started, TaskCompletionSource release)
        {
            this.inner = inner;
            this.started = started;
            this.release = release;
            foreach (var h in inner.Headers)
            {
                Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            started.TrySetResult();
            await release.Task;
            await inner.CopyToAsync(stream);
        }
    }
}

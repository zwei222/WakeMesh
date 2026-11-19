using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.CloudUi.Tests.Integration;

/// <summary>
/// T73: the cloud device list is rebuilt from real API data. Every continuation in
/// CloudManagementViewModel runs with ConfigureAwait(false), so without marshalling the
/// ObservableCollection mutations and PropertyChanged notifications fire on a thread-pool
/// thread while MAUI bindings read them from the UI thread — the reported reload crash.
/// These probes drive the real command entry point against the real in-process API with a
/// non-empty device list and record the thread each notification fires on.
/// </summary>
public sealed class T73CloudUiThreadSafetyTests : IClassFixture<CloudUiTestApiFactory>, IDisposable
{
    private readonly CloudUiTestApiFactory _factory;
    private readonly string _testDir;

    public T73CloudUiThreadSafetyTests(CloudUiTestApiFactory factory)
    {
        _factory = factory;
        _testDir = Path.Combine(Path.GetTempPath(), "wakemesh-t73-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LoadDataCommand_RegisteredDevicePresent_MutatesListAndStateOnlyOnDispatcherThread()
    {
        var http = _factory.CreateClient();
        var token = _factory.Auth.CreateAccessToken();
        var mockAuth = new MockAuthService("https://api.wakemesh.local", token);

        // Real producer: the list must contain exactly one device, not an empty collection.
        var setupClient = TestClientHelper.CreateApiClient(http, mockAuth);
        var registered = await setupClient.RegisterDeviceAsync("T73-Thread-Device", persistCredential: false);
        Assert.True(registered.IsSuccess, registered.Message);

        var secureStorage = new StubSecureStorageProvider();
        var credStore = new SecureDeviceCredentialStore(secureStorage, () => mockAuth.CurrentBinding);
        var regStore = new FileCloudDeviceRegistrationStore(Path.Combine(_testDir, "reg.json"));
        var coordinator = new AlarmCoordinator(
            new JsonFileAlarmStore(Path.Combine(_testDir, "alarms.json")),
            new AlarmScheduleCalculator(new FakeHolidayService()),
            new FakeAlarmScheduler());

        var cloudService = new CloudManagementService(
            mockAuth,
            regStore,
            credStore,
            coordinator,
            () => TestClientHelper.CreateApiClient(http, mockAuth, credStore),
            customStorageDir: _testDir);

        using var pump = new UiThreadPump();
        var dispatcher = new RecordingDispatcher(pump);
        var vm = new CloudManagementViewModel(cloudService, mockAuth, dispatcher: dispatcher);

        var violations = new ConcurrentQueue<string>();
        void Record(string member)
        {
            if (!pump.IsUiThread)
            {
                violations.Enqueue($"{member} on managed thread {Environment.CurrentManagedThreadId} (UI thread {pump.ThreadId})");
            }
        }

        vm.PropertyChanged += (_, e) => Record("vm." + (e.PropertyName ?? "<null>"));
        vm.Devices.CollectionChanged += (_, e) =>
        {
            Record($"vm.Devices.{e.Action}");
            if (e.NewItems is null) return;
            foreach (var item in e.NewItems.OfType<CloudDeviceItemViewModel>())
            {
                item.PropertyChanged += (_, pe) => Record("deviceItem." + (pe.PropertyName ?? "<null>"));
            }
        };
        vm.AlarmGroups.CollectionChanged += (_, e) => Record($"vm.AlarmGroups.{e.Action}");

        // The user taps "reload"; the entry point is the real AsyncRelayCommand.
        var work = Task.Run(() => vm.LoadDataCommand.ExecuteAsync(null));
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(45)));
        Assert.Same(work, finished);
        await work;
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        Assert.True(dispatcher.PostCount > 0,
            "CloudManagementViewModel never used IMainThreadDispatcher; UI-bound updates ran on the caller thread.");
        Assert.True(violations.IsEmpty,
            "UI-bound state was mutated off the main thread:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.False(vm.IsDeviceListError, vm.DeviceListErrorMessage);
        Assert.Single(vm.Devices);
        Assert.False(vm.IsBusy, "IsBusy must be reset on the UI thread after a reload");
    }

    [Fact]
    public async Task UiThreadDetector_RecordsOffThreadMutation_AndPostMarshalsToUiThread()
    {
        using var pump = new UiThreadPump();
        var dispatcher = new RecordingDispatcher(pump);
        var violations = new ConcurrentQueue<string>();
        var collection = new ObservableCollection<int>();
        collection.CollectionChanged += (_, e) =>
        {
            if (!pump.IsUiThread) violations.Enqueue("off-thread " + e.Action);
        };

        // Control: an un-marshalled mutation must be caught, otherwise the probe above is vacuous.
        await Task.Run(() => collection.Add(1));
        Assert.True(violations.Count == 1, "The control probe must detect an un-marshalled collection mutation.");

        // Post marshals the same mutation onto the dispatcher thread.
        dispatcher.Post(() => collection.Add(2));
        await pump.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.True(violations.Count == 1, "A posted mutation must run on the dispatcher thread.");
        Assert.Equal(2, collection.Count);
    }

    /// <summary>A single dedicated thread that consumes posted work; ThreadId is the UI thread.</summary>
    private sealed class UiThreadPump : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private int _outstanding;

        public UiThreadPump()
        {
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                SynchronizationContext.SetSynchronizationContext(new PumpSynchronizationContext(_queue));
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    try { action(); }
                    finally { Interlocked.Decrement(ref _outstanding); }
                }
            })
            { IsBackground = true, Name = "t73-ui-thread" };
            _thread.Start();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (ThreadId == 0 && DateTime.UtcNow < deadline) Thread.Sleep(1);
            Assert.True(ThreadId != 0, "UI pump thread did not start");
        }

        public int ThreadId { get; private set; }

        public bool IsUiThread => Environment.CurrentManagedThreadId == ThreadId;

        public void Post(Action action)
        {
            Interlocked.Increment(ref _outstanding);
            _queue.Add(action);
        }

        public async Task DrainAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _outstanding) > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5).ConfigureAwait(false);
            }

            Assert.Equal(0, Volatile.Read(ref _outstanding));
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }

        private sealed class PumpSynchronizationContext : SynchronizationContext
        {
            private readonly BlockingCollection<Action> _queue;

            public PumpSynchronizationContext(BlockingCollection<Action> queue) => _queue = queue;

            public override void Post(SendOrPostCallback d, object? state) => _queue.Add(() => d(state));

            public override void Send(SendOrPostCallback d, object? state) => d(state);
        }
    }

    private sealed class RecordingDispatcher : IMainThreadDispatcher
    {
        private readonly UiThreadPump _pump;

        public RecordingDispatcher(UiThreadPump pump) => _pump = pump;

        public int PostCount { get; private set; }

        public bool IsDispatchRequired => !_pump.IsUiThread;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            PostCount++;
            _pump.Post(action);
        }
    }
}

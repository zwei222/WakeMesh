// T115: the group editor's device picker went empty on a real device.
// CloudAlarmGroupEditViewModel awaited every cloud call with ConfigureAwait(false) and then mutated
// the bound AvailableDevices/Targets collections and properties with no main-thread dispatch, so the
// post-await continuation applied UI state from a thread-pool thread (Android: the Picker's native view
// is touched off the UI thread and the load aborts). A second defect discarded ListDevicesAsync
// failures silently, leaving an empty picker with no message.
// These probes drive the real view-model entry points against a fake that yields like a real network
// call, and record the thread every PropertyChanged/CollectionChanged/CanExecuteChanged fires on.
// They assert the state is applied on the dispatcher thread, never that the dispatcher is bypassed.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.Services;
using WakeMesh.Mobile.Services.CloudManagement;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.CloudUi.Tests;

public sealed class T115GroupEditorThreadingTests
{
    private static readonly Guid DeviceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeviceB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task T115_Initialize_PopulatesPickerAndDefaultTarget_OnlyOnDispatcherThread()
    {
        var service = new T115FakeCloudManagementService
        {
            ListDevicesResult = () => ApiResult<IReadOnlyList<DeviceListItem>>.Ok(new[]
            {
                T115Fixtures.Device(DeviceA, "登録端末A"),
                T115Fixtures.Device(DeviceB, "登録端末B"),
            }),
            Registration = T115Fixtures.Registration(DeviceA, "登録端末A"),
        };

        using var pump = new T115UiThreadPump();
        var dispatcher = new T115RecordingDispatcher(pump);
        var vm = new CloudAlarmGroupEditViewModel(service, dispatcher: dispatcher);
        var violations = new ConcurrentQueue<string>();
        AttachRecorder(vm, pump, violations);

        var work = Task.Run(() => vm.InitializeAsync());
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(45)));
        Assert.Same(work, finished);
        await work;
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        Assert.True(dispatcher.PostCount > 0,
            "InitializeAsync never used IMainThreadDispatcher; UI-bound state was applied on the caller thread.");
        Assert.True(violations.IsEmpty,
            "UI-bound state was applied off the UI thread:" + Environment.NewLine + string.Join(Environment.NewLine, violations));

        Assert.Equal(2, vm.AvailableDevices.Count);
        Assert.Equal(new[] { "登録端末A", "登録端末B" }, vm.AvailableDevices.Select(d => d.DisplayName).ToArray());
        Assert.True(vm.AvailableDevices[0].IsCurrentDevice);
        Assert.False(vm.AvailableDevices[1].IsCurrentDevice);
        Assert.Same(vm.AvailableDevices[0], vm.SelectedDeviceToAdd);

        // Existing behaviour kept: the registered device pre-fills the target list.
        var target = Assert.Single(vm.Targets);
        Assert.Equal(DeviceA, target.DeviceId);
        Assert.Equal("登録端末A", target.DeviceDisplayName);
        Assert.Equal(new TimeSpan(7, 0, 0), target.TimeOfDay);

        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsBusy, "IsBusy must be reset after a successful load.");
    }

    [Fact]
    public async Task T115_Initialize_ListDevicesFails_ShowsMessageInsteadOfSilentEmptyPicker()
    {
        var service = new T115FakeCloudManagementService
        {
            ListDevicesResult = () => ApiResult<IReadOnlyList<DeviceListItem>>.Fail(
                ApiErrorKind.Unauthorized, statusCode: 401, message: "サインインの有効期限が切れています。"),
        };

        using var pump = new T115UiThreadPump();
        var dispatcher = new T115RecordingDispatcher(pump);
        var vm = new CloudAlarmGroupEditViewModel(service, dispatcher: dispatcher);
        var violations = new ConcurrentQueue<string>();
        AttachRecorder(vm, pump, violations);

        var work = Task.Run(() => vm.InitializeAsync());
        await await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(45)));
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        Assert.True(violations.IsEmpty,
            "Failure state was applied off the UI thread:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.Empty(vm.AvailableDevices);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage), "A device-list failure must not be swallowed silently.");
        Assert.Contains("端末一覧を取得できませんでした", vm.ErrorMessage);
        Assert.Contains("サインインの有効期限が切れています。", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task T115_Initialize_EmptyDeviceListOnSuccess_IsDistinctFromFailure()
    {
        var service = new T115FakeCloudManagementService
        {
            ListDevicesResult = () => ApiResult<IReadOnlyList<DeviceListItem>>.Ok(Array.Empty<DeviceListItem>()),
        };

        using var pump = new T115UiThreadPump();
        var dispatcher = new T115RecordingDispatcher(pump);
        var vm = new CloudAlarmGroupEditViewModel(service, dispatcher: dispatcher);
        var violations = new ConcurrentQueue<string>();
        AttachRecorder(vm, pump, violations);

        var work = Task.Run(() => vm.InitializeAsync());
        await await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(45)));
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        Assert.True(violations.IsEmpty,
            "State was applied off the UI thread:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.Empty(vm.AvailableDevices);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task T115_Initialize_ServiceThrows_ErrorMessageSetAndBusyRestored()
    {
        var service = new T115FakeCloudManagementService
        {
            ListDevicesException = new InvalidOperationException("transient transport failure"),
        };

        using var pump = new T115UiThreadPump();
        var dispatcher = new T115RecordingDispatcher(pump);
        var vm = new CloudAlarmGroupEditViewModel(service, dispatcher: dispatcher);
        var violations = new ConcurrentQueue<string>();
        AttachRecorder(vm, pump, violations);

        var work = Task.Run(() => vm.InitializeAsync());
        await await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(45)));
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        Assert.True(violations.IsEmpty,
            "Error state was applied off the UI thread:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
        Assert.Contains("端末一覧", vm.ErrorMessage);
        Assert.False(vm.IsBusy, "IsBusy must return to false when the load throws.");
    }

    [Fact]
    public async Task T115_SaveThenReload_AppliesResultsOnDispatcherThread_AndKeepsWireLogic()
    {
        var service = new T115FakeCloudManagementService
        {
            ListDevicesResult = () => ApiResult<IReadOnlyList<DeviceListItem>>.Ok(new[]
            {
                T115Fixtures.Device(DeviceA, "登録端末A"),
            }),
            Registration = T115Fixtures.Registration(DeviceA, "登録端末A"),
            CreateGroupOutcome = T115Fixtures.Group("group-created", 3),
        };

        using var pump = new T115UiThreadPump();
        var dispatcher = new T115RecordingDispatcher(pump);
        var vm = new CloudAlarmGroupEditViewModel(service, dispatcher: dispatcher);
        var violations = new ConcurrentQueue<string>();
        AttachRecorder(vm, pump, violations);

        await await Task.WhenAny(Task.Run(() => vm.InitializeAsync()), Task.Delay(TimeSpan.FromSeconds(45)));
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        await pump.InvokeAsync(() =>
        {
            vm.Label = "朝のアラーム";
            vm.DaySunday = true; // wire contract 0=Sunday must still travel in the request
        });

        await pump.RunAsync(() => vm.SaveCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(45));
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        Assert.True(violations.IsEmpty,
            "Save result was applied off the UI thread:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsBusy, "IsBusy must be reset after save.");
        Assert.False(vm.IsNew);
        Assert.Equal("group-created", vm.GroupId);
        Assert.Equal(3, vm.IfMatchVersion);
        Assert.False(string.IsNullOrWhiteSpace(vm.SaveOutcomeNotice));

        Assert.NotNull(service.LastCreateRequest);
        Assert.Contains(0, service.LastCreateRequest!.Days!); // Sunday bit preserved through the UI
        Assert.Contains(1, service.LastCreateRequest!.Days!);

        // Reload the freshly saved group from the server.
        service.ListGroupsResult = () => ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(new[]
        {
            T115Fixtures.GroupDto("group-created", 9),
        });

        await pump.RunAsync(() => vm.ReloadFromServerCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(45));
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        Assert.True(violations.IsEmpty,
            "Reload result was applied off the UI thread:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.Equal(9, vm.IfMatchVersion);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task T115_SaveFailure_ErrorMessageSetAndBusyRestored_OnDispatcherThread()
    {
        var service = new T115FakeCloudManagementService
        {
            ListDevicesResult = () => ApiResult<IReadOnlyList<DeviceListItem>>.Ok(new[]
            {
                T115Fixtures.Device(DeviceA, "登録端末A"),
            }),
            Registration = T115Fixtures.Registration(DeviceA, "登録端末A"),
            CreateGroupException = new HttpRequestException("network down"),
        };

        using var pump = new T115UiThreadPump();
        var dispatcher = new T115RecordingDispatcher(pump);
        var vm = new CloudAlarmGroupEditViewModel(service, dispatcher: dispatcher);
        var violations = new ConcurrentQueue<string>();
        AttachRecorder(vm, pump, violations);

        await await Task.WhenAny(Task.Run(() => vm.InitializeAsync()), Task.Delay(TimeSpan.FromSeconds(45)));
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        await pump.InvokeAsync(() => vm.Label = "朝のアラーム");
        await pump.RunAsync(() => vm.SaveCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(45));
        await pump.DrainAsync(TimeSpan.FromSeconds(15));

        Assert.True(violations.IsEmpty,
            "Save failure state was applied off the UI thread:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
        Assert.Contains("保存", vm.ErrorMessage);
        Assert.False(vm.IsBusy, "IsBusy must return to false when save fails.");
    }

    private static void AttachRecorder(CloudAlarmGroupEditViewModel vm, T115UiThreadPump pump, ConcurrentQueue<string> violations)
    {
        void Record(string member)
        {
            if (!pump.IsUiThread)
            {
                violations.Enqueue($"{member} on managed thread {Environment.CurrentManagedThreadId} (UI thread {pump.ThreadId})");
            }
        }

        vm.PropertyChanged += (_, e) => Record("vm." + (e.PropertyName ?? "<null>"));
        vm.AvailableDevices.CollectionChanged += (_, e) => Record("vm.AvailableDevices." + e.Action);
        vm.Targets.CollectionChanged += (_, e) => Record("vm.Targets." + e.Action);
        vm.SaveCommand.CanExecuteChanged += (_, _) => Record("SaveCommand.CanExecuteChanged");
        vm.ReloadFromServerCommand.CanExecuteChanged += (_, _) => Record("ReloadFromServerCommand.CanExecuteChanged");
        vm.CancelCommand.CanExecuteChanged += (_, _) => Record("CancelCommand.CanExecuteChanged");
        vm.AddTargetCommand.CanExecuteChanged += (_, _) => Record("AddTargetCommand.CanExecuteChanged");
    }

    /// <summary>A single dedicated thread that consumes posted work; ThreadId is the UI thread.</summary>
    private sealed class T115UiThreadPump : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private int _outstanding;

        public T115UiThreadPump()
        {
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                SynchronizationContext.SetSynchronizationContext(new PumpSynchronizationContext(_queue));
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    action();
                }
            })
            { IsBackground = true, Name = "t115-ui-thread" };
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
            _queue.Add(() =>
            {
                try { action(); }
                finally { Interlocked.Decrement(ref _outstanding); }
            });
        }

        /// <summary>Runs an action on the UI thread and waits until it has been applied there.</summary>
        public Task InvokeAsync(Action action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _outstanding);
            _queue.Add(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    Interlocked.Decrement(ref _outstanding);
                }
            });
            return tcs.Task;
        }

        /// <summary>
        /// Runs an async entry point on the UI thread (as a real command is invoked) and completes only
        /// after both the command and the UI work it posted have finished.
        /// </summary>
        public Task RunAsync(Func<Task> work)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _outstanding);
            _queue.Add(() => { _ = ExecuteAsync(); });
            return tcs.Task;

            async Task ExecuteAsync()
            {
                try
                {
                    await work().ConfigureAwait(false);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    Interlocked.Decrement(ref _outstanding);
                }
            }
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

    private sealed class T115RecordingDispatcher : IMainThreadDispatcher
    {
        private readonly T115UiThreadPump _pump;

        public T115RecordingDispatcher(T115UiThreadPump pump) => _pump = pump;

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

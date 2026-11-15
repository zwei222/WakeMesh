// T186R: opening the group editor from the list for an existing group could fall through to the
// "new group" defaults when the id did not resolve (missing on the server, list failure, empty id
// coming from the query string). The editor then re-issued an id, showed defaults and could create a
// second group instead of editing the one the user picked.
// These probes pin the guard contract on the real view-model entry points:
//  - edit intent (any non-null id, empty/whitespace included) keeps IsNew=false and the requested id
//  - load failures report a fixed, distinguishable error and never create / never re-issue an id
//  - SaveCommand is disabled (CanExecute) AND its execute body refuses until a load really succeeded
//  - a retry with the same id, or a successful ReloadFromServer for the same id, re-enables saving
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WakeMesh.Client;
using WakeMesh.Client.Models;
using WakeMesh.Mobile.CloudUi.Tests.TestDoubles;
using WakeMesh.Mobile.ViewModels;

namespace WakeMesh.Mobile.CloudUi.Tests;

public sealed class T186REditLoadGuardTests
{
    [Fact]
    public async Task MissingEditGroupKeepsRequestedIdAndBlocksCreateUntilSameIdRetryLoads()
    {
        var service = new T115FakeCloudManagementService();
        var vm = new CloudAlarmGroupEditViewModel(service);
        var readyChanges = new List<bool>();
        vm.SaveCommand.CanExecuteChanged += (_, _) => readyChanges.Add(vm.SaveCommand.CanExecute(null));

        await vm.InitializeAsync("group-missing");

        Assert.False(vm.IsNew);
        Assert.Equal("group-missing", vm.GroupId);
        Assert.False(vm.IsSaveReady);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
        Assert.Contains("見つかりません", vm.ErrorMessage);
        Assert.Empty(vm.Targets);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // The execute body itself must not save either: no create, no replace, no re-issued id.
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(service.LastCreateRequest);
        Assert.Null(service.LastReplaceRequest);
        Assert.Equal("group-missing", vm.GroupId);
        Assert.False(vm.IsNew);
        // Existing draft is not silently replaced by the new-group defaults.
        Assert.NotEqual("朝のアラーム", vm.Label);
        Assert.Equal(1, service.ListGroupsCallCount);

        service.ListGroupsResult = () => ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(
            new[] { T115Fixtures.GroupDto("group-missing", 3) });
        await vm.InitializeAsync("group-missing");

        Assert.False(vm.IsNew);
        Assert.Equal("group-missing", vm.GroupId);
        Assert.Equal(3, vm.IfMatchVersion);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsSaveReady);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Contains(true, readyChanges);                       // readiness change notified the command
        Assert.Null(service.LastCreateRequest);
        Assert.Equal(2, service.ListGroupsCallCount);
    }

    [Fact]
    public async Task GroupListFailureAndExceptionRemainEditFailureAndCannotSave()
    {
        var service = new T115FakeCloudManagementService
        {
            ListGroupsResult = () => ApiResult<IReadOnlyList<AlarmGroupDto>>.Fail(ApiErrorKind.Server, statusCode: 500, message: "boom")
        };
        var vm = new CloudAlarmGroupEditViewModel(service);
        await vm.InitializeAsync("group-abc");
        Assert.False(vm.IsNew);
        Assert.Equal("group-abc", vm.GroupId);
        Assert.False(vm.IsSaveReady);
        Assert.Contains("読み込", vm.ErrorMessage);
        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(service.LastCreateRequest);
        Assert.Null(service.LastReplaceRequest);

        // A successful ReloadFromServer for the same id must restore the save guard.
        service.ListGroupsResult = () => ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(
            new[] { T115Fixtures.GroupDto("group-abc", 5) });
        Assert.True(vm.ReloadFromServerCommand.CanExecute(null));
        await vm.ReloadFromServerCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsSaveReady);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal(5, vm.IfMatchVersion);
        Assert.Null(service.LastCreateRequest);

        // A throwing list call (not just a 500 result) is still an edit failure, never a create.
        var throwing = new T115FakeCloudManagementService
        {
            ListGroupsResult = () => throw new InvalidOperationException("groups endpoint down")
        };
        var exceptionVm = new CloudAlarmGroupEditViewModel(throwing);
        await exceptionVm.InitializeAsync("group-throws");
        Assert.False(exceptionVm.IsNew);
        Assert.Equal("group-throws", exceptionVm.GroupId);
        Assert.False(exceptionVm.IsSaveReady);
        Assert.Contains("読み込", exceptionVm.ErrorMessage);
        Assert.DoesNotContain("groups endpoint down", exceptionVm.ErrorMessage); // no raw exception text
        Assert.False(exceptionVm.SaveCommand.CanExecute(null));
        await exceptionVm.SaveCommand.ExecuteAsync(null);
        Assert.Null(throwing.LastCreateRequest);
        Assert.Null(throwing.LastReplaceRequest);
        Assert.Equal("group-throws", exceptionVm.GroupId);

        // An expired sign-in is reported as such (fixed, recognisable message), still no create.
        var unauthorized = new T115FakeCloudManagementService
        {
            ListGroupsResult = () => ApiResult<IReadOnlyList<AlarmGroupDto>>.Fail(
                ApiErrorKind.Unauthorized, statusCode: 401, message: "unauthorized")
        };
        var authVm = new CloudAlarmGroupEditViewModel(unauthorized);
        await authVm.InitializeAsync("group-auth");
        Assert.False(authVm.IsNew);
        Assert.False(authVm.IsSaveReady);
        Assert.Contains("サインイン", authVm.ErrorMessage);
        Assert.False(authVm.SaveCommand.CanExecute(null));
        await authVm.SaveCommand.ExecuteAsync(null);
        Assert.Null(unauthorized.LastCreateRequest);
        Assert.Null(unauthorized.LastReplaceRequest);
    }

    [Fact]
    public async Task EmptyEditIdIsNotNewButMissingQueryIsNew()
    {
        var service = new T115FakeCloudManagementService();
        var vm = new CloudAlarmGroupEditViewModel(service);
        await vm.InitializeAsync(string.Empty);
        Assert.False(vm.IsNew);
        Assert.Equal(string.Empty, vm.GroupId);
        Assert.False(vm.IsSaveReady);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Contains("読み込", vm.ErrorMessage);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(service.LastCreateRequest);
        Assert.Null(service.LastReplaceRequest);
        Assert.Equal(string.Empty, vm.GroupId);
        Assert.NotEqual("朝のアラーム", vm.Label);            // defaults were not applied over the edit intent
        Assert.Equal(0, service.ListGroupsCallCount);        // invalid id never reaches the list call

        // Whitespace from the query string is the same invalid edit, not a new group.
        var whitespace = new CloudAlarmGroupEditViewModel(service);
        await whitespace.InitializeAsync("   ");
        Assert.False(whitespace.IsNew);
        Assert.Equal("   ", whitespace.GroupId);
        Assert.False(whitespace.IsSaveReady);
        Assert.False(whitespace.SaveCommand.CanExecute(null));
        Assert.Contains("読み込", whitespace.ErrorMessage);
        await whitespace.SaveCommand.ExecuteAsync(null);
        Assert.Null(service.LastCreateRequest);

        // No query value at all keeps the original new-group behaviour.
        var create = new CloudAlarmGroupEditViewModel(service);
        await create.InitializeAsync();
        Assert.True(create.IsNew);
        Assert.StartsWith("group-", create.GroupId);
        Assert.Equal("朝のアラーム", create.Label);
        Assert.True(create.IsSaveReady);
        Assert.True(create.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExistingGroupLoadsItsDataAndDefaultsRemainForTrueNewIntent()
    {
        var deviceId = Guid.NewGuid();
        var dto = new AlarmGroupDto("group-existing", "夜のアラーム", true, 7, null, "weekly",
            new[] { 1, 2, 3, 4, 5 }, null, "Asia/Tokyo", true, new[] { "2026-12-25" },
            new[] { new AlarmTargetDto(null, deviceId, "06:30", "default", 80, true, true) });
        var service = new T115FakeCloudManagementService
        {
            ListGroupsResult = () => ApiResult<IReadOnlyList<AlarmGroupDto>>.Ok(new[] { dto })
        };
        var edit = new CloudAlarmGroupEditViewModel(service);
        await edit.InitializeAsync("group-existing");
        Assert.False(edit.IsNew);
        Assert.Equal("group-existing", edit.GroupId);
        Assert.True(edit.IsSaveReady);
        Assert.Null(edit.ErrorMessage);
        Assert.Equal("夜のアラーム", edit.Label);
        Assert.Equal(7, edit.IfMatchVersion);
        Assert.Single(edit.Targets);
        Assert.Equal(deviceId, edit.Targets.Single().DeviceId);
        Assert.Equal("06:30", edit.Targets.Single().TimeOfDay.ToString(@"hh\:mm"));
        Assert.Equal(new[] { "2026-12-25" }, edit.ExcludedDates);
        Assert.Null(service.LastCreateRequest);              // opening an existing group never creates

        // Re-opening with an id that does not resolve keeps the already loaded draft intact and
        // blocks saving - it does not fall back to the new-group defaults.
        await edit.InitializeAsync("group-not-on-server");
        Assert.False(edit.IsNew);
        Assert.False(edit.IsSaveReady);
        Assert.False(edit.SaveCommand.CanExecute(null));
        Assert.Contains("見つかりません", edit.ErrorMessage);
        Assert.Equal("夜のアラーム", edit.Label);
        Assert.Single(edit.Targets);
        Assert.Equal(deviceId, edit.Targets.Single().DeviceId);
        await edit.SaveCommand.ExecuteAsync(null);
        Assert.Null(service.LastCreateRequest);
        Assert.Null(service.LastReplaceRequest);

        var create = new CloudAlarmGroupEditViewModel(service);
        await create.InitializeAsync();
        Assert.True(create.IsNew);
        Assert.Equal("朝のアラーム", create.Label);
        Assert.True(create.IsSaveReady);
        Assert.True(create.SaveCommand.CanExecute(null));
    }
}

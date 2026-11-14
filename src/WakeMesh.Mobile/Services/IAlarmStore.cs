using System.Collections.Generic;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

public interface IAlarmStore
{
    Task<IReadOnlyList<LocalAlarmModel>> GetAllAsync();
    Task<LocalAlarmModel?> GetByIdAsync(string id);
    Task SaveAsync(LocalAlarmModel alarm);
    Task DeleteAsync(string id);
    Task<LocalAlarmModel?> ToggleEnabledAsync(string id);
    Task<LocalAlarmModel?> UpdateAsync(string id, Action<LocalAlarmModel> mutate);
}

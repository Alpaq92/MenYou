using MenYou.Models;

namespace MenYou.Services;

public sealed class RecentItemsService : IRecentItemsService
{
    private readonly ISettingsService _settings;
    public event Action? Changed;

    public RecentItemsService(ISettingsService settings) => _settings = settings;

    public IReadOnlyList<RecentEntry> Recent => _settings.Current.Recent
        .OrderByDescending(r => r.LastUsedUtc)
        .Take(_settings.Current.MaxRecentItems)
        .ToList();

    public void RecordLaunch(string appId)
    {
        var list = _settings.Current.Recent;
        var idx = list.FindIndex(r => r.AppId == appId);
        if (idx >= 0)
            list[idx] = list[idx] with { LastUsedUtc = DateTime.UtcNow, LaunchCount = list[idx].LaunchCount + 1 };
        else
            list.Add(new RecentEntry(appId, DateTime.UtcNow, 1));
        _settings.Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _settings.Current.Recent.Clear();
        _settings.Save();
        Changed?.Invoke();
    }
}

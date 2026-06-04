namespace MenYou.Models;

public sealed record RecentEntry(string AppId, DateTime LastUsedUtc, int LaunchCount);

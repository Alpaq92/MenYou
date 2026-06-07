namespace MenYou.Models;

public enum SearchResultKind
{
    App,
    File,
    Folder,
    Command,
    /// UWP / packaged app — launch via explorer.exe shell:AppsFolder\<Aumid>;
    /// icon via PIDL.
    PackagedApp,
    /// Control Panel "All Tasks" entry. The TargetPath is a shell-IDL
    /// string that can't be passed to explorer.exe as an argument; launch
    /// goes through ControlPanelEnumerator.LaunchTask which re-navigates
    /// the namespace and invokes the item's default verb.
    ControlPanelTask,
}

public sealed record SearchResult(
    string Title,
    string? Subtitle,
    string? TargetPath,
    string? Arguments,
    string? IconPath,
    int IconIndex,
    SearchResultKind Kind,
    int Score,
    string? Aumid = null,
    // Discovery AppEntry.Id for App / PackagedApp results, so a launch from
    // search can be recorded into the recent-apps list. RecentItemsService
    // keys on this Id and AppDiscoveryService.FindById resolves it back to a
    // tile. Null for File / Folder / Command / ControlPanelTask results,
    // which aren't tracked as recent apps.
    string? AppId = null);

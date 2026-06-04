using System.Runtime.Versioning;
using System.Xml.Linq;

namespace MenYou.Platform.Windows;

/// Enumerates Settings deep-links by parsing the two
/// <c>AllSystemSettings_*.xml</c> files Windows ships in
/// <c>%SystemRoot%\ImmersiveControlPanel\Settings\</c>. Each
/// <c>&lt;SearchableContent&gt;</c> entry contains a Description and
/// HighKeywords reference of the form
/// <c>@{windows?ms-resource://Windows.UI.SettingsAppThreshold/...}</c>;
/// <see cref="ShellLocalization.LoadIndirectString"/> resolves them to
/// the user's localized text. PageIDs are mapped to <c>ms-settings:</c>
/// URIs via a hand-curated table (the same approach Open-Shell uses).
///
/// Entries whose PageID we don't map are dropped — landing on the wrong
/// Settings page is worse than not showing the result at all. The table
/// covers the ~50 most-trafficked pages; adding more is one-line
/// additions in <see cref="PageIdToUri"/>.
[SupportedOSPlatform("windows")]
internal static class SettingsDeepLinkEnumerator
{
    public sealed record Item(string Name, string Keywords, string Uri);

    private static IReadOnlyList<Item>? _cached;
    private static readonly object _gate = new();

    public static IReadOnlyList<Item> Enumerate()
    {
        if (_cached is not null) return _cached;
        lock (_gate)
        {
            _cached ??= LoadFresh();
            return _cached;
        }
    }

    private static List<Item> LoadFresh()
    {
        var list = new List<Item>();
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "ImmersiveControlPanel", "Settings");
        if (!Directory.Exists(dir)) return list;

        foreach (var path in Directory.EnumerateFiles(dir, "AllSystemSettings_*.xml"))
        {
            try
            {
                var doc = XDocument.Load(path);
                foreach (var sc in doc.Descendants("SearchableContent"))
                {
                    var info = sc.Element("SettingInformation");
                    if (info is null) continue;
                    var descRef = info.Element("Description")?.Value;
                    var kwRef   = info.Element("HighKeywords")?.Value;

                    var pageId = sc.Descendants("PageID").FirstOrDefault()?.Value;
                    if (string.IsNullOrWhiteSpace(pageId) || string.IsNullOrWhiteSpace(descRef)) continue;

                    var uri = PageIdToUri(pageId);
                    if (uri is null) continue; // skip unmapped pages

                    var name = ShellLocalization.LoadIndirectString(descRef);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var keywords = string.IsNullOrWhiteSpace(kwRef)
                        ? string.Empty
                        : ShellLocalization.LoadIndirectString(kwRef) ?? string.Empty;

                    list.Add(new Item(name!, keywords, uri));
                }
            }
            catch
            {
                // Malformed XML or transient I/O — skip this file and keep going.
            }
        }

        // Dedup on (Name, Uri) — the two XML files overlap, and multiple
        // entries can resolve to the same description+URI.
        return list
            .GroupBy(i => (i.Name, i.Uri))
            .Select(g => g.First())
            .ToList();
    }

    /// PageID → ms-settings URI. Hand-curated from the on-disk XML survey.
    /// Pages not listed here are skipped (landing on the wrong Settings
    /// page would be worse than nothing). Add an entry to extend coverage.
    private static string? PageIdToUri(string pageId) => pageId switch
    {
        // System
        "SettingsPagePCSystemDisplay"           => "ms-settings:display",
        "SettingsPagePCSystemDisplayRemote"     => "ms-settings:project",
        "SettingsPagePCSystemDisplayAdvanced"   => "ms-settings:display-advanced",
        "SettingsPageAudio"                     => "ms-settings:sound",
        "SettingsPageAppsNotifications"         => "ms-settings:notifications",
        "SettingsPageAppsNotifications-2"       => "ms-settings:notifications",
        "SettingsPagePowerAndBattery"           => "ms-settings:powersleep",
        "SettingsPageBatterySaver"              => "ms-settings:batterysaver",
        "SettingsPageStorageSenseStorageOverview" => "ms-settings:storagesense",
        "SettingsPageMultiTasking"              => "ms-settings:multitasking",
        "SettingsPageRestoreRestore"            => "ms-settings:recovery",
        "SettingsPageActivate"                  => "ms-settings:activation",
        "SettingsPageAbout"                     => "ms-settings:about",
        "SettingsPageAdvancedSystemSettings"    => "ms-settings:about",
        "SettingsPageRestoreDeveloperOptionsInSystemPageRejuv" => "ms-settings:developers",

        // Bluetooth & devices
        "SettingsPagePCSystemDeviceSettings"    => "ms-settings:bluetooth",
        "SettingsPageDevicesTouch"              => "ms-settings:devices",
        "SettingsPageDevicesTouchpad"           => "ms-settings:devices-touchpad",
        "SettingsPageDevicesPen"                => "ms-settings:pen",
        "SettingsPageDevicesKeyboard"           => "ms-settings:keyboard",
        "SettingsPageUsb"                       => "ms-settings:usb",
        "SettingsPagePrinter"                   => "ms-settings:printers",

        // Network
        "SettingsPageNetwork"                   => "ms-settings:network",
        "SettingsPageNetworkWiFi"               => "ms-settings:network-wifi",
        "SettingsPageNetworkEthernet"           => "ms-settings:network-ethernet",
        "SettingsPageNetworkVPN"                => "ms-settings:network-vpn",
        "SettingsPageNetworkProxy"              => "ms-settings:network-proxy",
        "SettingsPageNetworkMobileHotspot"      => "ms-settings:network-mobilehotspot",
        "SettingsPageNetworkAirplaneMode"       => "ms-settings:network-airplanemode",

        // Personalization
        "SettingsPageBackground"                => "ms-settings:personalization-background",
        "SettingsPageColors"                    => "ms-settings:personalization-colors",
        "SettingsPageLockScreen"                => "ms-settings:lockscreen",
        "SettingsPageTaskbar"                   => "ms-settings:taskbar",
        "SettingsPageStart"                     => "ms-settings:personalization-start",
        "SettingsPageFonts"                     => "ms-settings:fonts",
        "SettingsPageThemes"                    => "ms-settings:themes",
        "SettingsPagePersonalizationTextInput"  => "ms-settings:personalization-textinput",

        // Apps
        "SettingsPageAppsSizes"                 => "ms-settings:appsfeatures",
        "SettingsPageAppsDefaults"              => "ms-settings:defaultapps",
        "SettingsPageAppsForWebsites"           => "ms-settings:appsforwebsites",
        "SettingsPageAppsStartup"               => "ms-settings:startupapps",
        "SettingsPageVideo"                     => "ms-settings:videoplayback",
        "SettingsPageMaps"                      => "ms-settings:maps",

        // Accounts
        "SettingsPageAccountsInfo"              => "ms-settings:yourinfo",
        "SettingsPageEmailAndAccounts"          => "ms-settings:emailandaccounts",
        "SettingsPageSignInOptions"             => "ms-settings:signinoptions",
        "SettingsPageResume"                    => "ms-settings:signinoptions",
        "SettingsPageWorkAccess"                => "ms-settings:workplace",
        "SettingsPageOtherUsers"                => "ms-settings:otherusers",
        "SettingsPageAccountsSync"              => "ms-settings:sync",

        // Time & language
        "SettingsPageTimeRegionDateTime"        => "ms-settings:dateandtime",
        "SettingsPageTimeRegionLanguage"        => "ms-settings:regionlanguage",
        "SettingsPageTimeRegionKeyboard"        => "ms-settings:regionlanguage-bpmfime",
        "SettingsPageTimeRegionSpelling"        => "ms-settings:typing",
        "SettingsPageSpeech"                    => "ms-settings:speech",

        // Gaming
        "SettingsPageGameDVR"                   => "ms-settings:gaming-gamedvr",
        "SettingsPageGameBar"                   => "ms-settings:gaming-gamebar",
        "SettingsPageGameMode"                  => "ms-settings:gaming-gamemode",

        // Accessibility
        "SettingsPageEaseOfAccessVisualEffects" => "ms-settings:easeofaccess-visualeffects",
        "SettingsPageEaseOfAccessTextCursor"    => "ms-settings:easeofaccess-cursor",
        "SettingsPageEaseOfAccessMagnifier"     => "ms-settings:easeofaccess-magnifier",
        "SettingsPageEaseOfAccessColorFilter"   => "ms-settings:easeofaccess-colorfilter",
        "SettingsPageEaseOfAccessHighContrast"  => "ms-settings:easeofaccess-highcontrast",
        "SettingsPageEaseOfAccessNarrator"      => "ms-settings:easeofaccess-narrator",
        "SettingsPageEaseOfAccessNarratorNS"    => "ms-settings:easeofaccess-narrator",
        "SettingsPageEaseOfAccessAudio"         => "ms-settings:easeofaccess-audio",
        "SettingsPageEaseOfAccessCaptions"      => "ms-settings:easeofaccess-closedcaptioning",
        "SettingsPageEaseOfAccessSpeech"        => "ms-settings:easeofaccess-speechrecognition",
        "SettingsPageEaseOfAccessKeyboard"      => "ms-settings:easeofaccess-keyboard",
        "SettingsPageEaseOfAccessMouse"         => "ms-settings:easeofaccess-mouse",
        "SettingsPageEaseOfAccessMousePointer"  => "ms-settings:easeofaccess-mousepointer",
        "SettingsPageEaseOfAccessEyeControl"    => "ms-settings:easeofaccess-eyecontrol",

        // Privacy & security
        "SettingsPagePrivacyGeneral"            => "ms-settings:privacy-general",
        "SettingsPagePrivacyLocation"           => "ms-settings:privacy-location",
        "SettingsPagePrivacyWebcam"             => "ms-settings:privacy-webcam",
        "SettingsPagePrivacyMicrophone"         => "ms-settings:privacy-microphone",
        "SettingsPagePrivacySIUFSettings"       => "ms-settings:privacy-feedback",
        "SettingsPagePrivacyPersonalization"    => "ms-settings:privacy-speechtyping",
        "SettingsPagePrivacyTasks"              => "ms-settings:privacy-tasks",
        "SettingsPagePrivacyAccountInfo"        => "ms-settings:privacy-accountinfo",
        "SettingsPagePrivacyContacts"           => "ms-settings:privacy-contacts",
        "SettingsPagePrivacyCalendar"           => "ms-settings:privacy-calendar",
        "SettingsPagePrivacyEmail"              => "ms-settings:privacy-email",
        "SettingsPagePrivacyPhoneCalls"         => "ms-settings:privacy-phonecalls",
        "SettingsPagePrivacyCallHistory"        => "ms-settings:privacy-callhistory",
        "SettingsPagePrivacyNotifications"      => "ms-settings:privacy-notifications",
        "SettingsPagePrivacyMessaging"          => "ms-settings:privacy-messaging",
        "SettingsPagePrivacyRadios"             => "ms-settings:privacy-radios",
        "SettingsPagePrivacyOtherDevices"       => "ms-settings:privacy-customdevices",
        "SettingsPagePrivacyBackgroundApps"     => "ms-settings:privacy-backgroundapps",
        "SettingsPagePrivacyAppDiagnostics"     => "ms-settings:privacy-appdiagnostics",
        "SettingsPagePrivacyDocuments"          => "ms-settings:privacy-documents",
        "SettingsPagePrivacyPictures"           => "ms-settings:privacy-pictures",
        "SettingsPagePrivacyVideos"             => "ms-settings:privacy-videos",
        "SettingsPagePrivacyMusicLibrary"       => "ms-settings:privacy-musiclibrary",
        "SettingsPagePrivacyBroadFileSystemAccess" => "ms-settings:privacy-broadfilesystemaccess",
        "SettingsPagePrivacyActivityHistory"    => "ms-settings:privacy-activityhistory",

        // Search / Cortana
        "SettingsPageRationalizedSearchSettings" => "ms-settings:cortana",
        "SettingsPageSearchPermissionsAndHistory" => "ms-settings:cortana-permissions",
        "SettingsPageSearchIndex"               => "ms-settings:cortana-windowssearch",

        // Update
        "SettingsPageRestoreMusUpdate"          => "ms-settings:windowsupdate",
        "SettingsPageWindowsUpdate"             => "ms-settings:windowsupdate",
        "SettingsPageDeliveryOptimization"      => "ms-settings:delivery-optimization",
        "SettingsPageTroubleshoot"              => "ms-settings:troubleshoot",
        "SettingsPageFindMyDevice"              => "ms-settings:findmydevice",

        _ => null,
    };
}

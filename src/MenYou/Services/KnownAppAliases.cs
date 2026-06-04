namespace MenYou.Services;

/// Per-app multi-language alias bundles for well-known Windows apps and
/// utilities. Solves two cross-locale search misses that fall through the
/// regular DisplayName / on-disk-filename matching:
///
///   * UWP apps like Snipping Tool come from <c>Get-StartApps</c> with
///     only the localized name ("Narzędzie Wycinanie" on a Polish
///     Windows); the English label "Snipping Tool" isn't on disk
///     anywhere we can read. Without an alias map a Polish-locale user
///     can't find it by typing the English name.
///   * Win32 .lnk shortcuts often have one canonical filename but users
///     type abbreviations ("cmd" for Command Prompt, "regedit" for
///     Registry Editor) that don't appear in the displayed Title either.
///     Basename ranking in SearchService handles many of these, but the
///     alias bundle here covers the cases where the exe basename doesn't
///     match the common abbreviation (e.g. Snipping Tool's UWP entry
///     has no exe).
///
/// Each row is a single group. Lookup by ANY name in a group returns the
/// full row, so typing "cmd", "Command Prompt", "Wiersz polecenia", or
/// "Eingabeaufforderung" all surface the same app.
public static class KnownAppAliases
{
    private static readonly string[][] Groups =
    {
        // Built-in Windows utilities
        new[] { "Snipping Tool", "Narzędzie Wycinanie", "Werkzeug für Bildschirmausschnitte",
                "Outil Capture d'écran", "Recortes", "Strumento di cattura", "Notools",
                "Kirpme aracı", "切图工具", "切圖工具", "切り取り＆スケッチ" },
        new[] { "Calculator", "calc", "Kalkulator", "Rechner", "Calculatrice", "Calculadora",
                "Calcolatrice", "Calculadora", "Калькулятор", "電卓", "计算器", "計算機", "계산기" },
        new[] { "Notepad", "notepad", "Notatnik", "Editor", "Bloc-notes",
                "Bloc de notas", "Blocco note", "Bloco de Notas", "Блокнот",
                "メモ帳", "记事本", "記事本", "메모장" },
        new[] { "Paint", "mspaint", "Paint 3D" },
        new[] { "Photos", "Zdjęcia", "Fotos", "Photos", "Fotografias", "Foto",
                "Фотографии", "フォト", "照片", "사진" },
        new[] { "Settings", "Ustawienia", "Einstellungen", "Paramètres",
                "Configuración", "Impostazioni", "Configurações", "Instellingen",
                "Параметры", "設定", "设置", "設定", "설정" },
        new[] { "File Explorer", "explorer", "Eksplorator plików", "Datei-Explorer",
                "Explorateur de fichiers", "Explorador de archivos", "Esplora file",
                "Bestandsverkenner", "Проводник", "エクスプローラー", "文件资源管理器",
                "檔案總管", "파일 탐색기" },
        new[] { "Command Prompt", "cmd", "Wiersz polecenia", "Eingabeaufforderung",
                "Invite de commandes", "Símbolo del sistema", "Prompt dei comandi",
                "Prompt de Comando", "Opdrachtprompt", "Командная строка",
                "コマンド プロンプト", "命令提示符", "命令提示字元", "명령 프롬프트" },
        new[] { "PowerShell", "powershell", "ps", "Windows PowerShell", "pwsh" },
        new[] { "Task Manager", "taskmgr", "Menedżer zadań", "Task-Manager",
                "Gestionnaire des tâches", "Administrador de tareas", "Gestione attività",
                "Gerenciador de Tarefas", "Taakbeheer", "Диспетчер задач",
                "タスク マネージャー", "任务管理器", "工作管理員", "작업 관리자" },
        new[] { "Registry Editor", "regedit", "Edytor rejestru", "Registrierungs-Editor",
                "Éditeur du Registre", "Editor del Registro", "Editor del Registro di sistema",
                "Editor de Registro", "Register-editor", "Редактор реестра",
                "レジストリ エディター", "注册表编辑器", "登錄編輯程式", "레지스트리 편집기" },
        new[] { "Control Panel", "Panel sterowania", "Systemsteuerung",
                "Panneau de configuration", "Panel de control", "Pannello di controllo",
                "Painel de Controle", "Configuratiescherm", "Панель управления",
                "コントロール パネル", "控制面板", "控制台", "제어판" },
        new[] { "Camera", "Aparat", "Kamera", "Appareil photo", "Cámara",
                "Fotocamera", "Câmera", "Камера", "カメラ", "相机", "相機", "카메라" },
        new[] { "Calendar", "Kalendarz", "Kalender", "Calendrier", "Calendario",
                "Календарь", "カレンダー", "日历", "行事曆", "달력" },
        new[] { "Mail", "Poczta", "E-Mail", "Courrier", "Correo", "Posta",
                "Почта", "メール", "邮件", "郵件", "메일" },
        new[] { "Maps", "Mapy", "Karten", "Cartes", "Mapas", "Mappe",
                "Карты", "マップ", "地图", "地圖", "지도" },
        new[] { "Microsoft Store", "Sklep Microsoft", "Microsoft Store",
                "Магазин Microsoft", "Microsoft ストア", "微软商店", "Microsoft 商店",
                "Microsoft 스토어" },
        new[] { "Weather", "Pogoda", "Wetter", "Météo", "Tiempo", "Meteo",
                "Tempo", "Weer", "Погода", "天気", "天气", "天氣", "날씨" },
        new[] { "Microsoft Edge", "Edge" },
        new[] { "Microsoft Teams", "Teams" },
        new[] { "Microsoft Word", "Word" },
        new[] { "Microsoft Excel", "Excel" },
        new[] { "Microsoft PowerPoint", "PowerPoint" },
        new[] { "Microsoft Outlook", "Outlook" },
        new[] { "Microsoft OneNote", "OneNote" },
        new[] { "Microsoft OneDrive", "OneDrive" },
        new[] { "Run", "Uruchom", "Ausführen", "Exécuter", "Ejecutar",
                "Esegui", "Executar", "Uitvoeren", "Выполнить",
                "ファイル名を指定して実行", "运行", "執行", "실행" },
        new[] { "System Configuration", "msconfig", "Konfiguracja systemu",
                "Systemkonfiguration", "Configuration du système",
                "Configuración del sistema", "Configurazione di sistema",
                "Systeemconfiguratie", "Конфигурация системы",
                "システム構成", "系统配置", "系統設定", "시스템 구성" },
        new[] { "Disk Management", "diskmgmt", "Zarządzanie dyskami",
                "Datenträgerverwaltung", "Gestion des disques",
                "Administración de discos", "Gestione disco", "Schijfbeheer",
                "Управление дисками", "ディスクの管理", "磁盘管理", "磁碟管理", "디스크 관리" },
        new[] { "Services", "services", "Usługi", "Dienste", "Services",
                "Servicios", "Servizi", "Diensten", "Службы",
                "サービス", "服务", "服務", "서비스" },
        new[] { "Event Viewer", "eventvwr", "Podgląd zdarzeń", "Ereignisanzeige",
                "Observateur d'événements", "Visor de eventos", "Visualizzatore eventi",
                "Logboeken", "Просмотр событий", "イベント ビューアー",
                "事件查看器", "事件檢視器", "이벤트 뷰어" },
        new[] { "Resource Monitor", "resmon", "Monitor zasobów",
                "Ressourcenmonitor", "Moniteur de ressources", "Monitor de recursos",
                "Monitoraggio risorse", "Resourcecontrole", "Монитор ресурсов",
                "リソース モニター", "资源监视器", "資源監視器", "리소스 모니터" },
        new[] { "Microsoft Edge", "Edge" },
        new[] { "Notepad", "Notatnik" },
    };

    /// Case-insensitive index from any name in any group to the full
    /// group. Built once at static init.
    private static readonly Dictionary<string, string[]> Index = BuildIndex();

    private static Dictionary<string, string[]> BuildIndex()
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            foreach (var name in group)
            {
                // Later groups don't overwrite earlier ones — the canonical
                // English name is always the first entry, so look-ups
                // converge on the first-defined group.
                d.TryAdd(name, group);
            }
        }
        return d;
    }

    /// Returns the full alias group for an app whose display name (or
    /// any other alias) matches one of the known sets. Empty array on
    /// miss. Callers typically populate <c>AppEntry.SearchAliases</c>
    /// with the result minus the entry's own display name.
    public static IReadOnlyList<string> GetAliases(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return Array.Empty<string>();
        return Index.TryGetValue(displayName, out var aliases)
            ? aliases
            : Array.Empty<string>();
    }
}

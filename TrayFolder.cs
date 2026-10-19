// TrayFolder — замена «Панелей инструментов» для Windows 11.
//
// Значок папки живёт в трее у часов. Левый клик раскрывает содержимое папки
// каскадным меню, как стрелка » у панелей инструментов в Windows 10:
// подпапки раскрываются при наведении, клик по файлу открывает файл,
// клик по папке открывает её в Проводнике. Средний клик по значку — открыть
// папку целиком. Правый клик — служебное меню: вид значка, язык, автозапуск, ещё папка, закрыть.
//
// Папки читаются в фоне: медленная сетевая или огромная папка не подвешивает программу,
// а сначала показывает «Загрузка…». Если папка недоступна (вынули флешку, отвалилась сеть),
// значок становится приглушённым. Неожиданные ошибки записываются в журнал
// %LOCALAPPDATA%\TrayFolder\TrayFolder.log (открывается из меню правой кнопки).
//
// Сборка: дважды щёлкните build.bat. Он использует компилятор C#, который уже
// есть в Windows (.NET Framework 4.x), устанавливать ничего не нужно.
//
// Запуск:
//   TrayFolder.exe                 — вернёт в трей сохранённые папки, а если их нет — спросит папку
//   TrayFolder.exe "D:\Папка"      — сразу покажет эту папку
//   или просто перетащите папку (или несколько) на TrayFolder.exe.
// Каждая папка — отдельный значок, как отдельная панель инструментов.
// Добавленная папка сама прописывается в автозапуск (снимается в меню правой кнопки).
// Вид значка выбирается в меню правой кнопки → «Значок» и запоминается для каждой папки.
// Язык интерфейса — по языку Windows; сменить его можно в меню правой кнопки → «Язык»,
// выбор общий для всех значков. Переводы — в классе Lang, ближе к концу файла.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("TrayFolder")]
[assembly: AssemblyProduct("TrayFolder")]
[assembly: AssemblyDescription("Папка в трее с раскрывающимся меню")]
[assembly: AssemblyVersion("1.4.1.0")]
[assembly: AssemblyFileVersion("1.4.1.0")]

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }   // чёткие шрифты и иконки при масштабе 125–200 %
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
        {
            Log.Error("Ошибка в программе", e.Exception);
            Util.Message(Lang.F("Crash", e.Exception.Message, Log.FilePath));
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
        {
            Log.Error("Сбой в фоновом потоке", e.ExceptionObject as Exception);
        };

        List<string> folders = new List<string>();
        bool fromStartup = false;
        foreach (string arg in args)
        {
            if (string.Equals(arg, Util.StartupSwitch, StringComparison.OrdinalIgnoreCase)) fromStartup = true;
            else if (arg.Trim().Length > 0) folders.Add(arg);
        }

        // Если на программу перетащили сразу несколько папок, каждая получает свой значок
        for (int i = 1; i < folders.Count; i++) Util.StartInstance(folders[i], false);

        // Запуск без папки возвращает в трей сохранённые папки, которых там сейчас нет:
        // из автозапуска и те, что были открыты в прошлый раз. Если возвращать нечего — спрашивает папку
        if (folders.Count == 0 && !fromStartup)
        {
            int restored = 0;
            foreach (string saved in Util.SavedFolders())
                if (!Util.IsRunning(saved)) { Util.StartInstance(saved, true); restored++; }
            if (restored > 0) return;
        }

        string folder = folders.Count > 0 ? folders[0] : Util.PickFolder(null);
        if (folder == null) return;
        folder = Util.NormalizePath(folder);
        Log.Folder = folder;

        if (!fromStartup && !Directory.Exists(folder))
        {
            Util.Message(Lang.F("NotAFolder", folder));
            return;
        }

        // Одна папка — один значок: повторный запуск для той же папки ничего не дублирует
        bool createdNew;
        using (new Mutex(true, Util.MutexName(folder), out createdNew))
        {
            if (!createdNew)
            {
                if (!fromStartup) Util.Message(Lang.F("AlreadyInTray", folder));
                return;
            }
            if (!fromStartup) Util.SetAutostart(folder, true);
            Util.MarkOpen(folder, true);
            using (new TrayFolderApp(folder))
            {
                Application.Run();
            }
        }
    }
}

sealed class TrayFolderApp : IDisposable
{
    const int MaxItems = 400;            // больше пунктов в одном меню не строим — остальное через «Ещё…»
    const int QuickWaitMs = 500;         // столько меню ждёт папку, прежде чем показать «Загрузка…»
    const int CheckIntervalMs = 15000;   // как часто проверять, доступна ли папка

    readonly string folder;
    readonly FolderNode rootNode;
    readonly NotifyIcon tray;
    readonly ContextMenuStrip folderMenu;
    readonly ContextMenuStrip serviceMenu;
    readonly ToolStripMenuItem openItem;
    readonly ToolStripMenuItem addItem;
    readonly ToolStripMenuItem iconMenu;
    readonly ToolStripMenuItem languageMenu;
    readonly ToolStripMenuItem autostartItem;
    readonly ToolStripMenuItem logItem;
    readonly ToolStripMenuItem closeItem;
    readonly Size iconSize = SystemInformation.SmallIconSize;
    readonly IconCache iconCache = new IconCache();
    readonly Control ui = new Control();   // невидимый посредник: через него фоновые потоки возвращаются в поток меню
    readonly System.Windows.Forms.Timer availabilityTimer = new System.Windows.Forms.Timer();
    readonly UserPreferenceChangedEventHandler onPreferenceChanged;

    FlatRenderer renderer;
    DateTime folderMenuClosedAt = DateTime.MinValue;
    DateTime serviceMenuClosedAt = DateTime.MinValue;
    Rectangle rootAnchor;   // значок, от которого открыто меню папки
    string displayName;
    string iconStyle;
    Icon trayIcon;
    bool available = true;
    bool checking;
    Color globeInk;   // каким цветом нарисован глобус у пункта «Язык»

    public TrayFolderApp(string folder)
    {
        this.folder = folder;
        rootNode = new FolderNode(folder);
        ui.CreateControl();
        GC.KeepAlive(ui.Handle);   // окно-посредник создаётся сейчас, в потоке интерфейса

        // Имя сначала берём из пути, без обращения к диску; имя из Проводника уточняется в фоне
        displayName = Util.NameFromPath(folder);
        iconStyle = Util.GetIconStyle(folder);

        folderMenu = new ContextMenuStrip();
        folderMenu.ImageScalingSize = iconSize;
        folderMenu.Closed += delegate { folderMenuClosedAt = DateTime.Now; };

        // Подписи пунктов служебного меню ставит ApplyLanguage — на выбранном языке
        openItem = new ToolStripMenuItem();
        openItem.Click += delegate { Shell.Open(this.folder); };

        addItem = new ToolStripMenuItem();
        addItem.Click += delegate { AddFolder(); };

        iconMenu = new ToolStripMenuItem();
        iconMenu.DropDown.ImageScalingSize = iconSize;
        iconMenu.DropDownItems.Add(Placeholder("…"));
        iconMenu.DropDownOpening += delegate { FillIconMenu(); };

        // Подменю «Язык»: каждый язык подписан на нём самом, текущий отмечен галочкой
        languageMenu = new ToolStripMenuItem();
        languageMenu.DropDown.ImageScalingSize = iconSize;
        foreach (Lang.Language language in Lang.All)
        {
            // Пустая картинка нужна только для того, чтобы место слева было таким же, как в других меню
            ToolStripMenuItem item = new ToolStripMenuItem(language.Name, BlankImage());
            string code = language.Code;
            item.Tag = code;
            item.Click += delegate
            {
                Lang.Set(code);
                ApplyLanguage();
                UpdateTrayText();
            };
            languageMenu.DropDownItems.Add(item);
        }
        languageMenu.DropDownOpening += delegate
        {
            foreach (ToolStripItem item in languageMenu.DropDownItems)
            {
                ToolStripMenuItem choice = item as ToolStripMenuItem;
                if (choice != null) choice.Checked = (string)choice.Tag == Lang.Current;
            }
        };

        autostartItem = new ToolStripMenuItem();
        autostartItem.Click += delegate { Util.SetAutostart(this.folder, !autostartItem.Checked); };

        logItem = new ToolStripMenuItem();
        logItem.Click += delegate { Shell.Open(Log.FilePath); };

        closeItem = new ToolStripMenuItem();
        closeItem.Click += delegate
        {
            Util.MarkOpen(this.folder, false);   // закрыта вручную — после пересборки её возвращать не нужно
            tray.Visible = false;
            Application.Exit();
        };

        serviceMenu = new ContextMenuStrip();
        serviceMenu.ImageScalingSize = iconSize;
        serviceMenu.Items.Add(openItem);
        serviceMenu.Items.Add(addItem);
        serviceMenu.Items.Add(iconMenu);
        serviceMenu.Items.Add(languageMenu);
        serviceMenu.Items.Add(new ToolStripSeparator());
        serviceMenu.Items.Add(autostartItem);
        serviceMenu.Items.Add(logItem);
        serviceMenu.Items.Add(closeItem);
        serviceMenu.Opening += delegate { PrepareServiceMenu(); };
        serviceMenu.Closed += delegate { serviceMenuClosedAt = DateTime.Now; };
        ApplyLanguage();

        // Оба меню показывает сама программа, а не NotifyIcon: так их можно ставить от значка
        tray = new NotifyIcon();
        tray.MouseClick += OnTrayClick;
        UpdateTrayText();
        UpdateTrayIcon();
        tray.Visible = true;

        // Сменилась светлая/тёмная тема или цвет акцента — перерисовать значок
        onPreferenceChanged = delegate { UpdateTrayIcon(); };
        SystemEvents.UserPreferenceChanged += onPreferenceChanged;

        availabilityTimer.Interval = CheckIntervalMs;
        availabilityTimer.Tick += delegate { CheckAvailability(); };
        availabilityTimer.Start();
        CheckAvailability();
    }

    void OnTrayClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle) { Shell.Open(folder); return; }

        // Клик по значку при открытом меню должен просто закрыть меню, а не открыть его заново
        if (e.Button == MouseButtons.Right)
        {
            if ((DateTime.Now - serviceMenuClosedAt).TotalMilliseconds < 300) return;
            PrepareServiceMenu();
            ShowAtIcon(serviceMenu, TrayAnchor(), false);   // служебное меню растёт вправо от значка
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if ((DateTime.Now - folderMenuClosedAt).TotalMilliseconds < 300) return;

        Lang.Refresh();   // язык могли сменить в меню другого значка
        ApplyTheme();
        iconCache.ReleaseRetired();   // меню закрыто — устаревшие картинки нигде не показываются
        rootAnchor = TrayAnchor();
        OpenListing(rootNode, folderMenu.Items, delegate(Listing listing)
        {
            if (!folderMenu.Visible) { NoteRootStatus(listing); return; }
            // Папка дочиталась, пока висело «Загрузка…»: подставляем содержимое в открытое меню,
            // и оно растёт вверх от той же точки над значком
            Fill(rootNode, folderMenu.Items, listing);
            folderMenu.PerformLayout();
            folderMenu.Location = TrayPlacement.MenuLocation(rootAnchor, folderMenu.Size, true);
        });
        ShowAtIcon(folderMenu, rootAnchor, true);   // меню папки растёт влево от значка
    }

    // Опора для меню — значок в трее, а если Windows не сообщила, где он, — точка курсора
    Rectangle TrayAnchor()
    {
        Rectangle icon = TrayPlacement.IconRect(tray);
        return icon.IsEmpty ? new Rectangle(Cursor.Position, new Size(1, 1)) : icon;
    }

    // Меню встаёт нижним краем на верхний край панели задач, а по горизонтали — от края значка
    // (правило — в TrayPlacement). Перед показом активным делается окно значка, как это делает
    // сам NotifyIcon: иначе меню не закроется щелчком мимо него
    void ShowAtIcon(ContextMenuStrip menu, Rectangle anchor, bool growLeft)
    {
        Size size = menu.GetPreferredSize(Size.Empty);
        IntPtr owner = TrayPlacement.TrayWindow(tray);
        try { Native.SetForegroundWindow(owner != IntPtr.Zero ? owner : menu.Handle); }
        catch (EntryPointNotFoundException) { }   // не Windows — меню всё равно откроется
        menu.Show(TrayPlacement.MenuLocation(anchor, size, growLeft));
        // При показе меню оказалось другого размера — ставим его заново, иначе край уедет от значка
        if (menu.Size != size) menu.Location = TrayPlacement.MenuLocation(anchor, menu.Size, growLeft);
    }

    // Служебное меню готовится до того, как известен его размер: язык, тема, глобус, галочки
    void PrepareServiceMenu()
    {
        Lang.Refresh();   // язык могли сменить в меню другого значка
        ApplyLanguage();
        ApplyTheme();
        UpdateLanguageIcon();
        autostartItem.Checked = Util.IsAutostart(this.folder);
        logItem.Visible = File.Exists(Log.FilePath);   // пункт появляется, только когда есть что посмотреть
    }

    // Подписи служебного меню на текущем языке; обновляются при каждом открытии меню
    void ApplyLanguage()
    {
        openItem.Text = Lang.T("OpenInExplorer");
        addItem.Text = Lang.T("AddFolder");
        iconMenu.Text = Lang.T("Icon");
        languageMenu.Text = Lang.T("Language");
        autostartItem.Text = Lang.T("Autostart");
        logItem.Text = Lang.T("ErrorLog");
        closeItem.Text = Lang.T("Close");
    }

    Image blank;
    Image BlankImage()
    {
        if (blank == null) blank = new Bitmap(iconSize.Width, iconSize.Height, PixelFormat.Format32bppArgb);
        return blank;
    }

    // Глобус у пункта «Язык»: его узнают, даже если интерфейс на незнакомом языке.
    // Перерисовывается, когда меню сменило цвет
    void UpdateLanguageIcon()
    {
        Color ink = renderer != null ? renderer.Fore : SystemColors.MenuText;
        if (languageMenu.Image != null && ink == globeInk) return;
        Image old = languageMenu.Image;
        try
        {
            languageMenu.Image = MenuIcons.Globe(iconSize.Width, ink);
            globeInk = ink;
        }
        catch (Exception ex) { Log.Error("Значок пункта «Язык»", ex); return; }   // без глобуса меню всё равно работает
        if (old != null) old.Dispose();
    }

    // Светлое или тёмное меню — в тон панели задач; проверяется при каждом открытии
    void ApplyTheme()
    {
        if (SystemInformation.HighContrast)
        {
            ToolStripManager.RenderMode = ToolStripManagerRenderMode.System;
            renderer = null;
            return;
        }
        bool dark = Util.IsDarkTaskbar();
        if (renderer == null || renderer.Dark != dark)
        {
            renderer = new FlatRenderer(dark);
            ToolStripManager.Renderer = renderer;
        }
    }

    // Готовит меню папки. Папка читается в фоне; если она прочиталась за QuickWaitMs, меню сразу
    // полное, как раньше. Если нет (сеть, огромная папка) — пока «Загрузка…», а когда чтение
    // закончится, результат передаётся в deliver: корневое меню подставляет его в открытое меню,
    // подменю открывается заново уже с содержимым. Результат устаревшего чтения отбрасывается.
    void OpenListing(FolderNode node, ToolStripItemCollection items, Action<Listing> deliver)
    {
        Listing ready = node.Pending;
        node.Pending = null;
        if (ready != null) { Fill(node, items, ready); return; }

        int ticket = ++node.Ticket;   // результат старого чтения, если меню успели открыть заново, не нужен
        Task<Listing> load = LoadInBackground(node.Dir);
        if (load.Wait(QuickWaitMs)) { Fill(node, items, load.Result); return; }

        SetPlaceholder(items, Lang.T("Loading"));
        load.ContinueWith(delegate(Task<Listing> done)
        {
            Listing listing = done.Result;
            OnUi(delegate { if (ticket == node.Ticket) deliver(listing); });
        });
    }

    // Строит пункты меню из уже прочитанной папки — без обращения к диску, быстро
    void Fill(FolderNode node, ToolStripItemCollection items, Listing listing)
    {
        if (node == rootNode) NoteRootStatus(listing);
        Clear(items);
        if (listing.Error != null) { items.Add(Placeholder(listing.Error)); return; }

        foreach (Entry entry in listing.Entries)
            items.Add(entry.IsDir ? MakeFolderItem(entry) : MakeFileItem(entry));
        if (listing.Entries.Count == 0) items.Add(Placeholder(Lang.T("Empty")));

        if (listing.Truncated || listing.Hidden > 0)
        {
            items.Add(new ToolStripSeparator());
            string text = listing.Truncated
                ? Lang.F("MoreThan", FolderReader.MaxScan.ToString("N0"))
                : Lang.F("MoreItems", listing.Hidden);
            ToolStripMenuItem more = new ToolStripMenuItem(text);
            string dir = node.Dir;
            more.Click += delegate { Shell.Open(dir); };
            items.Add(more);
        }
    }

    ToolStripMenuItem MakeFolderItem(Entry entry)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(Util.MenuText(entry.Name), entry.Icon);
        FolderNode node = new FolderNode(entry.FullPath);
        item.DropDown.ImageScalingSize = iconSize;
        item.DropDownItems.Add(Placeholder("…"));   // заглушка, чтобы у пункта была стрелка ►
        item.DropDownOpening += delegate   // содержимое читается только при раскрытии
        {
            OpenListing(node, item.DropDownItems, delegate(Listing listing)
            {
                if (item.IsDisposed || !item.DropDown.Visible) return;   // подменю уже закрыли
                node.Pending = listing;
                item.HideDropDown();
                item.ShowDropDown();   // откроется заново и возьмёт готовый результат
            });
        };
        item.Click += delegate
        {
            folderMenu.Close(ToolStripDropDownCloseReason.ItemClicked);
            Shell.Open(entry.FullPath);   // клик по папке открывает её в Проводнике, как в старых панелях
        };
        return item;
    }

    ToolStripMenuItem MakeFileItem(Entry entry)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(Util.MenuText(entry.Name), entry.Icon);
        item.Click += delegate { Shell.Open(entry.FullPath); };
        return item;
    }

    static ToolStripMenuItem Placeholder(string text)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.Enabled = false;
        return item;
    }

    static void Clear(ToolStripItemCollection items)
    {
        ToolStripItem[] old = new ToolStripItem[items.Count];
        items.CopyTo(old, 0);
        items.Clear();
        foreach (ToolStripItem item in old) item.Dispose();   // картинки принадлежат кэшу и не уничтожаются
    }

    static void SetPlaceholder(ToolStripItemCollection items, string text)
    {
        Clear(items);
        items.Add(Placeholder(text));
    }

    // Чтение папки — в отдельном фоновом потоке STA: так оболочка Windows спокойно отдаёт имена и иконки
    Task<Listing> LoadInBackground(string dir)
    {
        TaskCompletionSource<Listing> result = new TaskCompletionSource<Listing>();
        Background(delegate()
        {
            Listing listing;
            try { listing = LoadListing(dir); }
            catch (Exception ex)
            {
                Log.Error("Чтение папки " + dir, ex);
                listing = Listing.Failed(Lang.T("ReadFailed"));
            }
            result.SetResult(listing);
        });
        return result.Task;
    }

    // Работает в фоновом потоке: только файловая система, оболочка и кэш, к меню не прикасается
    Listing LoadListing(string dir)
    {
        FolderReader.Scan scan = FolderReader.ScanFolder(dir, FolderReader.MaxScan,
            Util.ExplorerOption("Hidden", 2) == 1, Util.ExplorerOption("ShowSuperHidden", 0) == 1,
            Native.StrCmpLogicalW);
        Listing listing = new Listing();
        listing.Error = scan.Error;
        listing.Missing = scan.Missing;
        listing.Truncated = scan.Truncated;
        if (scan.Error != null) return listing;

        int total = scan.Dirs.Count + scan.Files.Count;
        int shown = Math.Min(total, MaxItems);
        for (int i = 0; i < shown; i++)
        {
            bool isDir = i < scan.Dirs.Count;
            FileSystemInfo info = isDir ? scan.Dirs[i] : scan.Files[i - scan.Dirs.Count];
            Entry entry = new Entry();
            entry.FullPath = info.FullName;
            entry.IsDir = isDir;
            entry.Name = Shell.GetDisplayName(info.FullName);
            entry.Icon = iconCache.Get(info.FullName, isDir, info.LastWriteTimeUtc);
            listing.Entries.Add(entry);
        }
        listing.Hidden = total - shown;
        return listing;
    }

    static void Background(ThreadStart work)
    {
        Thread thread = new Thread(work);
        thread.IsBackground = true;                    // не мешает программе закрыться
        thread.SetApartmentState(ApartmentState.STA);  // этого требует SHGetFileInfo
        thread.Start();
    }

    void OnUi(MethodInvoker action)
    {
        try { ui.BeginInvoke(action); }
        catch (InvalidOperationException) { }   // программа уже закрывается
    }

    // Доступна ли папка и как её называет Проводник — проверяется в фоне, поэтому не подвешивает
    // программу, даже если сетевой диск отвечает по полминуты
    void CheckAvailability()
    {
        if (checking) return;
        checking = true;
        Background(delegate()
        {
            bool exists = false;
            string name = null;
            try
            {
                exists = Directory.Exists(folder);
                if (exists) name = Shell.GetDisplayName(folder);
            }
            catch (Exception ex) { Log.Error("Проверка доступности папки", ex); }
            OnUi(delegate
            {
                checking = false;
                if (name != null) displayName = name;
                if (exists != available) { available = exists; UpdateTrayIcon(); }
                Lang.Refresh();   // подсказка у значка тоже переходит на язык, выбранный в другом значке
                UpdateTrayText();
            });
        });
    }

    void NoteRootStatus(Listing listing)
    {
        bool exists = !listing.Missing;
        if (exists == available) return;
        available = exists;
        UpdateTrayIcon();
        UpdateTrayText();
    }

    void UpdateTrayText()
    {
        tray.Text = Util.TrayText(displayName, available);
    }

    // Подменю «Значок»: каждый вариант с живым превью, текущий отмечен галочкой
    void FillIconMenu()
    {
        ToolStripItemCollection items = iconMenu.DropDownItems;
        ToolStripItem[] old = new ToolStripItem[items.Count];
        items.CopyTo(old, 0);
        items.Clear();
        foreach (ToolStripItem item in old)
        {
            if (item.Image != null) item.Image.Dispose();
            item.Dispose();
        }

        bool dark = Util.IsDarkTaskbar();
        foreach (string style in TrayIcons.Styles)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(TrayIcons.Title(style), TwoCellImage(TrayIcons.Draw(style, iconSize.Width, dark)));
            item.ImageScaling = ToolStripItemImageScaling.None;   // картинка шириной в две ячейки, без растягивания
            item.Checked = iconStyle == style;
            string chosen = style;
            item.Click += delegate { ApplyIconStyle(chosen); };
            items.Add(item);
        }
    }

    // Слева в меню «Значок» две ячейки — галочка и превью. Картинка пункта шириной в обе ячейки
    // с промежутком (превью — в правой), поэтому Windows оставляет под них место; рисует их FlatRenderer
    Bitmap TwoCellImage(Bitmap preview)
    {
        Bitmap wide = new Bitmap(FlatRenderer.TwoCells(iconSize).Width, iconSize.Height, PixelFormat.Format32bppArgb);
        if (preview == null) return wide;
        using (preview)
        using (Graphics g = Graphics.FromImage(wide))
            g.DrawImageUnscaled(preview, wide.Width - preview.Width, 0);
        return wide;
    }

    void ApplyIconStyle(string style)
    {
        Icon icon = TrayIcons.Create(style, iconSize.Width, Util.IsDarkTaskbar(), !available);
        if (icon == null)
        {
            Util.Message(Lang.T("IconFailed"));
            return;
        }
        iconStyle = style;
        Util.SetIconStyle(folder, style);
        ReplaceTrayIcon(icon);
    }

    void UpdateTrayIcon()
    {
        bool dark = Util.IsDarkTaskbar();
        Icon icon = TrayIcons.Create(iconStyle, iconSize.Width, dark, !available)
                    ?? TrayIcons.Create(TrayIcons.DefaultStyle, iconSize.Width, dark, !available);
        ReplaceTrayIcon(icon);
    }

    void ReplaceTrayIcon(Icon icon)
    {
        Icon old = trayIcon;
        trayIcon = icon;
        tray.Icon = icon ?? SystemIcons.Application;
        if (old != null) old.Dispose();
    }

    void AddFolder()
    {
        string picked = Util.PickFolder(folder);
        if (picked != null) Util.StartInstance(picked, false);
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= onPreferenceChanged;
        availabilityTimer.Stop();
        availabilityTimer.Dispose();
        tray.Visible = false;
        tray.Dispose();
        folderMenu.Dispose();
        if (languageMenu.Image != null) languageMenu.Image.Dispose();
        if (blank != null) blank.Dispose();
        serviceMenu.Dispose();
        ui.Dispose();
        if (trayIcon != null) trayIcon.Dispose();
        iconCache.Dispose();
    }
}

// Пункт-папка в меню: путь, номер последнего чтения и результат, дочитанный после «Загрузка…»
sealed class FolderNode
{
    public readonly string Dir;
    public int Ticket;
    public Listing Pending;
    public FolderNode(string dir) { Dir = dir; }
}

// Прочитанная папка: первые элементы с готовыми именами и иконками
sealed class Listing
{
    public readonly List<Entry> Entries = new List<Entry>();
    public int Hidden;        // сколько элементов не поместилось в меню
    public bool Truncated;    // папка больше FolderReader.MaxScan — дальше читать не стали
    public string Error;      // текст вместо содержимого, если папку прочитать не удалось
    public bool Missing;      // папки нет или диск недоступен

    public static Listing Failed(string error)
    {
        Listing listing = new Listing();
        listing.Error = error;
        return listing;
    }
}

sealed class Entry
{
    public string FullPath;
    public string Name;
    public bool IsDir;
    public Image Icon;
}

// Обход папки для меню: только файловая система, без оболочки Windows
static class FolderReader
{
    public const int MaxScan = 10000;   // дальше папку не читаем: столько в меню всё равно не показать

    public sealed class Scan
    {
        public readonly List<FileSystemInfo> Dirs = new List<FileSystemInfo>();
        public readonly List<FileSystemInfo> Files = new List<FileSystemInfo>();
        public bool Truncated;
        public string Error;
        public bool Missing;
    }

    // Скрытые и системные файлы показываются так же, как в Проводнике; папки сверху, сортировка — compare
    public static Scan ScanFolder(string dir, int maxScan, bool showHidden, bool showProtected, Comparison<string> compare)
    {
        Scan scan = new Scan();
        try
        {
            int seen = 0;
            foreach (FileSystemInfo entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (++seen > maxScan) { scan.Truncated = true; break; }
                FileAttributes attrs = entry.Attributes;
                bool hidden = (attrs & FileAttributes.Hidden) != 0;
                bool sys = (attrs & FileAttributes.System) != 0;
                if (hidden && sys && !showProtected) continue;
                if (hidden && !showHidden) continue;
                if ((attrs & FileAttributes.Directory) != 0) scan.Dirs.Add(entry);
                else scan.Files.Add(entry);
            }
        }
        catch (UnauthorizedAccessException) { scan.Error = Lang.T("AccessDenied"); return scan; }
        catch (System.Security.SecurityException) { scan.Error = Lang.T("AccessDenied"); return scan; }
        catch (IOException) { scan.Error = Lang.T("FolderUnavailable"); scan.Missing = true; return scan; }

        Comparison<FileSystemInfo> byName = delegate(FileSystemInfo x, FileSystemInfo y)
        {
            return compare(x.Name, y.Name);
        };
        scan.Dirs.Sort(byName);
        scan.Files.Sort(byName);
        return scan;
    }
}

// Кэш иконок для меню. У обычных файлов иконка одна на расширение, у папок, ярлыков и программ —
// своя, и в ключ входит время изменения: сменили значок ярлыка — меню сразу покажет новый.
// Кэш ограничен: при переполнении он начинается заново, а старые картинки освобождаются,
// когда меню закрыто и они точно нигде не показываются. Безопасен для фоновых потоков.
sealed class IconCache : IDisposable
{
    public const int Limit = 2000;
    readonly Dictionary<string, Image> map = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
    readonly List<Image> retired = new List<Image>();
    readonly object sync = new object();
    readonly Func<string, bool, bool, Image> extract;
    bool disposed;

    public IconCache() : this(Shell.GetSmallIcon) { }
    public IconCache(Func<string, bool, bool, Image> extract) { this.extract = extract; }

    public Image Get(string path, bool isDir, DateTime modified)
    {
        string ext = isDir ? "" : Path.GetExtension(path);
        bool own = isDir || ext.Length == 0 || Shell.HasOwnIcon(ext);
        string key = own ? path + "|" + modified.Ticks : ext;
        Image image;
        lock (sync)
        {
            if (disposed) return null;
            if (map.TryGetValue(key, out image)) return image;
        }

        image = extract(path, isDir, !own);   // вне блокировки: на сетевом диске это может быть небыстро

        lock (sync)
        {
            Image existing = null;
            if (disposed || map.TryGetValue(key, out existing))   // программа закрывается или другой поток успел раньше
            {
                if (image != null) image.Dispose();
                return disposed ? null : existing;
            }
            if (map.Count >= Limit)
            {
                foreach (Image old in map.Values)
                    if (old != null) retired.Add(old);
                map.Clear();
            }
            map[key] = image;
            return image;
        }
    }

    public int Count { get { lock (sync) return map.Count; } }

    // Вызывать, когда меню закрыто: тогда вытесненные картинки точно нигде не показываются
    public void ReleaseRetired()
    {
        lock (sync)
        {
            foreach (Image old in retired) old.Dispose();
            retired.Clear();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            foreach (Image image in map.Values)
                if (image != null) image.Dispose();
            map.Clear();
            ReleaseRetired();
        }
    }
}

// Журнал неожиданных ошибок со стеком вызовов: %LOCALAPPDATA%\TrayFolder\TrayFolder.log.
// Ожидаемые ситуации (нет доступа, папка недоступна) сюда не пишутся. После 256 КБ журнал
// начинается заново, а предыдущий остаётся рядом как TrayFolder.log.old.
static class Log
{
    const long MaxSize = 256 * 1024;
    static readonly object sync = new object();
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayFolder", "TrayFolder.log");
    public static string Folder = "";   // папка этого экземпляра: журнал общий для всех значков

    public static void Error(string what, Exception ex)
    {
        try
        {
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                FileInfo info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxSize)
                {
                    string previous = FilePath + ".old";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(FilePath, previous);
                }
                string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "  TrayFolder " + Assembly.GetExecutingAssembly().GetName().Version
                    + "  [" + Folder + "]  " + what + Environment.NewLine
                    + (ex == null ? "(без подробностей)" : ex.ToString())
                    + Environment.NewLine + Environment.NewLine;
                File.AppendAllText(FilePath, text, Encoding.UTF8);
            }
        }
        catch { }   // сам журнал не должен ронять программу
    }
}

// Языки интерфейса. Пока язык не выбран в меню «Язык», берётся язык Windows, а если перевода
// на него нет — английский. Выбор хранится в HKCU\Software\TrayFolder (значение Language)
// и общий для всех значков. Журнал ошибок не переводится: он для разработчика.
// Чтобы добавить язык, скопируйте блок English, переведите строки и добавьте блок в список All.
static class Lang
{
    public sealed class Language
    {
        public readonly string Code;   // двухбуквенный код, как у языков Windows: ru, en, de…
        public readonly string Name;   // самоназвание — так язык подписан в меню
        public readonly Dictionary<string, string> Text;

        public Language(string code, string name, Dictionary<string, string> text)
        {
            Code = code;
            Name = name;
            Text = text;
        }
    }

    const string SettingsKey = @"Software\TrayFolder";
    const string SettingName = "Language";

    static readonly Language Russian = new Language("ru", "Русский", new Dictionary<string, string>
    {
        { "OpenInExplorer", "Открыть в Проводнике" },
        { "AddFolder", "Добавить ещё папку…" },
        { "Icon", "Значок" },
        { "Language", "Язык" },
        { "Autostart", "Запускать вместе с Windows" },
        { "ErrorLog", "Журнал ошибок" },
        { "Close", "Закрыть" },
        { "Loading", "Загрузка…" },
        { "Empty", "(пусто)" },
        { "MoreThan", "Больше {0} элементов — открыть всю папку" },
        { "MoreItems", "Ещё {0} — открыть всю папку" },
        { "ReadFailed", "(не удалось прочитать папку)" },
        { "AccessDenied", "(нет доступа)" },
        { "FolderUnavailable", "(папка недоступна)" },
        { "UnavailableMark", " — недоступна" },
        { "IconFailed", "Не получилось нарисовать значок. Подробности — в журнале ошибок." },
        { "Crash", "Ошибка: {0}\n\nПодробности записаны в журнал:\n{1}" },
        { "NotAFolder", "Это не папка или она сейчас недоступна:\n{0}" },
        { "AlreadyInTray", "Эта папка уже есть в трее:\n{0}" },
        { "AutostartFailed", "Не удалось изменить автозапуск: {0}" },
        { "IconSaveFailed", "Значок сменился, но запомнить выбор не удалось: {0}" },
        { "LanguageSaveFailed", "Язык сменился, но запомнить выбор не удалось: {0}" },
        { "PickFolder", "Какую папку показать в трее у часов?" },
        { "Style.chevron", "Папка со стрелкой »" },
        { "Style.outline-chevron", "Контурная папка со стрелкой »" },
        { "Style.outline", "Контурная папка, как значки Windows" },
        { "Style.arrows", "Стрелка », как у старых панелей" },
    });

    static readonly Language English = new Language("en", "English", new Dictionary<string, string>
    {
        { "OpenInExplorer", "Open in File Explorer" },
        { "AddFolder", "Add another folder…" },
        { "Icon", "Icon" },
        { "Language", "Language" },
        { "Autostart", "Start with Windows" },
        { "ErrorLog", "Error log" },
        { "Close", "Close" },
        { "Loading", "Loading…" },
        { "Empty", "(empty)" },
        { "MoreThan", "More than {0} items — open the whole folder" },
        { "MoreItems", "{0} more — open the whole folder" },
        { "ReadFailed", "(couldn't read the folder)" },
        { "AccessDenied", "(access denied)" },
        { "FolderUnavailable", "(folder unavailable)" },
        { "UnavailableMark", " — unavailable" },
        { "IconFailed", "Couldn't draw the icon. See the error log for details." },
        { "Crash", "Error: {0}\n\nDetails have been written to the log:\n{1}" },
        { "NotAFolder", "This isn't a folder, or it's unavailable right now:\n{0}" },
        { "AlreadyInTray", "This folder is already in the system tray:\n{0}" },
        { "AutostartFailed", "Couldn't change the startup setting: {0}" },
        { "IconSaveFailed", "The icon has changed, but the choice couldn't be saved: {0}" },
        { "LanguageSaveFailed", "The language has changed, but the choice couldn't be saved: {0}" },
        { "PickFolder", "Which folder should appear in the system tray next to the clock?" },
        { "Style.chevron", "Folder with » arrow" },
        { "Style.outline-chevron", "Folder outline with » arrow" },
        { "Style.outline", "Folder outline, like Windows icons" },
        { "Style.arrows", "» arrow, like the old toolbars" },
    });

    static readonly Language German = new Language("de", "Deutsch", new Dictionary<string, string>
    {
        { "OpenInExplorer", "Im Datei-Explorer öffnen" },
        { "AddFolder", "Weiteren Ordner hinzufügen…" },
        { "Icon", "Symbol" },
        { "Language", "Sprache" },
        { "Autostart", "Mit Windows starten" },
        { "ErrorLog", "Fehlerprotokoll" },
        { "Close", "Schließen" },
        { "Loading", "Wird geladen…" },
        { "Empty", "(leer)" },
        { "MoreThan", "Mehr als {0} Elemente – ganzen Ordner öffnen" },
        { "MoreItems", "{0} weitere – ganzen Ordner öffnen" },
        { "ReadFailed", "(Ordner konnte nicht gelesen werden)" },
        { "AccessDenied", "(Zugriff verweigert)" },
        { "FolderUnavailable", "(Ordner nicht verfügbar)" },
        { "UnavailableMark", " – nicht verfügbar" },
        { "IconFailed", "Das Symbol konnte nicht gezeichnet werden. Details stehen im Fehlerprotokoll." },
        { "Crash", "Fehler: {0}\n\nDetails wurden ins Protokoll geschrieben:\n{1}" },
        { "NotAFolder", "Das ist kein Ordner, oder er ist gerade nicht verfügbar:\n{0}" },
        { "AlreadyInTray", "Dieser Ordner ist bereits im Infobereich:\n{0}" },
        { "AutostartFailed", "Der Autostart konnte nicht geändert werden: {0}" },
        { "IconSaveFailed", "Das Symbol wurde geändert, aber die Auswahl konnte nicht gespeichert werden: {0}" },
        { "LanguageSaveFailed", "Die Sprache wurde geändert, aber die Auswahl konnte nicht gespeichert werden: {0}" },
        { "PickFolder", "Welcher Ordner soll im Infobereich neben der Uhr erscheinen?" },
        { "Style.chevron", "Ordner mit Pfeil »" },
        { "Style.outline-chevron", "Ordnerumriss mit Pfeil »" },
        { "Style.outline", "Ordnerumriss wie die Windows-Symbole" },
        { "Style.arrows", "Pfeil » wie bei den alten Symbolleisten" },
    });

    // Во французском перед двоеточием и вопросительным знаком ставится неразрывный пробел (\u00A0)
    static readonly Language French = new Language("fr", "Français", new Dictionary<string, string>
    {
        { "OpenInExplorer", "Ouvrir dans l'Explorateur de fichiers" },
        { "AddFolder", "Ajouter un autre dossier…" },
        { "Icon", "Icône" },
        { "Language", "Langue" },
        { "Autostart", "Lancer au démarrage de Windows" },
        { "ErrorLog", "Journal des erreurs" },
        { "Close", "Fermer" },
        { "Loading", "Chargement…" },
        { "Empty", "(vide)" },
        { "MoreThan", "Plus de {0} éléments – ouvrir tout le dossier" },
        { "MoreItems", "{0} de plus – ouvrir tout le dossier" },
        { "ReadFailed", "(impossible de lire le dossier)" },
        { "AccessDenied", "(accès refusé)" },
        { "FolderUnavailable", "(dossier indisponible)" },
        { "UnavailableMark", " – indisponible" },
        { "IconFailed", "Impossible de dessiner l'icône. Les détails se trouvent dans le journal des erreurs." },
        { "Crash", "Erreur\u00A0: {0}\n\nLes détails ont été enregistrés dans le journal\u00A0:\n{1}" },
        { "NotAFolder", "Ce n'est pas un dossier, ou il est indisponible pour le moment\u00A0:\n{0}" },
        { "AlreadyInTray", "Ce dossier est déjà dans la zone de notification\u00A0:\n{0}" },
        { "AutostartFailed", "Impossible de modifier le lancement au démarrage\u00A0: {0}" },
        { "IconSaveFailed", "L'icône a changé, mais le choix n'a pas pu être enregistré\u00A0: {0}" },
        { "LanguageSaveFailed", "La langue a changé, mais le choix n'a pas pu être enregistré\u00A0: {0}" },
        { "PickFolder", "Quel dossier afficher dans la zone de notification, près de l'horloge\u00A0?" },
        { "Style.chevron", "Dossier avec flèche »" },
        { "Style.outline-chevron", "Contour de dossier avec flèche »" },
        { "Style.outline", "Contour de dossier, comme les icônes de Windows" },
        { "Style.arrows", "Flèche », comme les anciennes barres d'outils" },
    });

    static readonly Language Spanish = new Language("es", "Español", new Dictionary<string, string>
    {
        { "OpenInExplorer", "Abrir en el Explorador de archivos" },
        { "AddFolder", "Agregar otra carpeta…" },
        { "Icon", "Icono" },
        { "Language", "Idioma" },
        { "Autostart", "Iniciar con Windows" },
        { "ErrorLog", "Registro de errores" },
        { "Close", "Cerrar" },
        { "Loading", "Cargando…" },
        { "Empty", "(vacía)" },
        { "MoreThan", "Más de {0} elementos — abrir toda la carpeta" },
        { "MoreItems", "{0} más — abrir toda la carpeta" },
        { "ReadFailed", "(no se pudo leer la carpeta)" },
        { "AccessDenied", "(acceso denegado)" },
        { "FolderUnavailable", "(carpeta no disponible)" },
        { "UnavailableMark", " — no disponible" },
        { "IconFailed", "No se pudo dibujar el icono. Los detalles están en el registro de errores." },
        { "Crash", "Error: {0}\n\nLos detalles se guardaron en el registro:\n{1}" },
        { "NotAFolder", "No es una carpeta o no está disponible en este momento:\n{0}" },
        { "AlreadyInTray", "Esta carpeta ya está en el área de notificación:\n{0}" },
        { "AutostartFailed", "No se pudo cambiar el inicio con Windows: {0}" },
        { "IconSaveFailed", "El icono cambió, pero no se pudo guardar la elección: {0}" },
        { "LanguageSaveFailed", "El idioma cambió, pero no se pudo guardar la elección: {0}" },
        { "PickFolder", "¿Qué carpeta se mostrará en el área de notificación, junto al reloj?" },
        { "Style.chevron", "Carpeta con flecha »" },
        { "Style.outline-chevron", "Contorno de carpeta con flecha »" },
        { "Style.outline", "Contorno de carpeta, como los iconos de Windows" },
        { "Style.arrows", "Flecha », como las antiguas barras de herramientas" },
    });

    static readonly Language Italian = new Language("it", "Italiano", new Dictionary<string, string>
    {
        { "OpenInExplorer", "Apri in Esplora file" },
        { "AddFolder", "Aggiungi un'altra cartella…" },
        { "Icon", "Icona" },
        { "Language", "Lingua" },
        { "Autostart", "Avvia con Windows" },
        { "ErrorLog", "Registro errori" },
        { "Close", "Chiudi" },
        { "Loading", "Caricamento…" },
        { "Empty", "(vuota)" },
        { "MoreThan", "Più di {0} elementi – apri l'intera cartella" },
        { "MoreItems", "Altri {0} – apri l'intera cartella" },
        { "ReadFailed", "(impossibile leggere la cartella)" },
        { "AccessDenied", "(accesso negato)" },
        { "FolderUnavailable", "(cartella non disponibile)" },
        { "UnavailableMark", " – non disponibile" },
        { "IconFailed", "Impossibile disegnare l'icona. I dettagli sono nel registro errori." },
        { "Crash", "Errore: {0}\n\nI dettagli sono stati scritti nel registro:\n{1}" },
        { "NotAFolder", "Non è una cartella oppure al momento non è disponibile:\n{0}" },
        { "AlreadyInTray", "Questa cartella è già nell'area di notifica:\n{0}" },
        { "AutostartFailed", "Impossibile modificare l'avvio con Windows: {0}" },
        { "IconSaveFailed", "L'icona è cambiata, ma non è stato possibile salvare la scelta: {0}" },
        { "LanguageSaveFailed", "La lingua è cambiata, ma non è stato possibile salvare la scelta: {0}" },
        { "PickFolder", "Quale cartella mostrare nell'area di notifica, accanto all'orologio?" },
        { "Style.chevron", "Cartella con freccia »" },
        { "Style.outline-chevron", "Contorno di cartella con freccia »" },
        { "Style.outline", "Contorno di cartella, come le icone di Windows" },
        { "Style.arrows", "Freccia », come le vecchie barre degli strumenti" },
    });

    // Порядок в меню «Язык» — по алфавиту самоназваний, как в списке языков Windows
    public static readonly Language[] All = { German, English, Spanish, French, Italian, Russian };

    static volatile Language current = Detect();
    static Language unsaved;   // язык, выбранный в меню, если записать выбор в реестр не удалось

    // Код текущего языка: ru, en, de…
    public static string Current { get { return current.Code; } }

    // Перечитать выбор: язык могли сменить в меню другого значка
    public static void Refresh() { current = Detect(); }

    public static string T(string key)
    {
        string text;
        if (current.Text.TryGetValue(key, out text)) return text;
        if (English.Text.TryGetValue(key, out text)) return text;   // на случай неполного перевода
        return key;
    }

    public static string F(string key, params object[] args)
    {
        return string.Format(T(key), args);
    }

    public static void Set(string code)
    {
        Language chosen = Find(code);
        if (chosen == null) return;
        current = chosen;
        unsaved = null;
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                key.SetValue(SettingName, chosen.Code);
        }
        catch (Exception ex)
        {
            unsaved = chosen;   // до закрытия программы язык всё равно останется выбранным
            Log.Error("Сохранение языка", ex);
            Util.Message(F("LanguageSaveFailed", ex.Message));
        }
    }

    public static Language Find(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        foreach (Language language in All)
            if (string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)) return language;
        return null;
    }

    // Язык, выбранный в меню, иначе язык Windows, иначе английский
    static Language Detect()
    {
        return unsaved
            ?? Find(SavedCode())
            ?? Find(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
            ?? English;
    }

    static string SavedCode()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                return key == null ? null : key.GetValue(SettingName) as string;
        }
        catch { return null; }
    }
}

// Значки для трея, нарисованные кодом под точный размер значка в трее
// (16 px при масштабе 100 %, 20 px при 125 %, 24 px при 150 %), поэтому они всегда чёткие
// и сами перекрашиваются под светлую или тёмную панель задач, как значки самой Windows.
static class TrayIcons
{
    public const string DefaultStyle = "chevron";
    const float DimOpacity = 0.4f;   // насколько приглушён значок недоступной папки

    // Варианты в том порядке, в каком они стоят в меню «Значок»
    public static readonly string[] Styles = { "chevron", "outline-chevron", "outline", "arrows" };

    public static bool IsKnown(string style) { return Array.IndexOf(Styles, style) >= 0; }

    // Название варианта в меню — на текущем языке (переводы — в Lang, ключи "Style.<вариант>")
    public static string Title(string style)
    {
        return Lang.T("Style." + style);
    }

    // Значок для трея; dimmed — приглушённый, для недоступной папки. null — если нарисовать не удалось
    public static Icon Create(string style, int size, bool dark, bool dimmed)
    {
        try
        {
            using (Bitmap bmp = Draw(style, size, dark))
            {
                if (bmp == null) return null;
                if (dimmed) Fade(bmp, DimOpacity);
                return ToIcon(bmp);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Значок «" + style + "»", ex);
            return null;
        }
    }

    // Значок рисуется в 16 раз крупнее и уменьшается усреднением: сглаживание как у векторных
    // значков Windows, а края, попавшие на границы пикселей, остаются идеально чёткими
    const int Supersample = 16;

    // Рисует вариант значка; null — если такого варианта нет. Все координаты — в пикселях готового значка.
    public static Bitmap Draw(string style, int size, bool dark)
    {
        float s = size / 16f;   // рисунок задуман на сетке 16×16 и масштабируется
        Color ink = dark ? Color.White : Color.FromArgb(28, 28, 28);
        Folder f = new Folder(s);
        float cx = size / 2f, cy = (f.Top + f.Bottom) / 2;   // середина передней стенки папки
        using (Bitmap big = new Bitmap(size * Supersample, size * Supersample, PixelFormat.Format32bppArgb))
        {
            switch (style)
            {
                case "chevron":           // сплошная папка, стрелка вырезана насквозь
                    Paint(big, delegate(Graphics g) { f.Fill(g, ink); });
                    Cut(big, delegate(Graphics g)
                    {
                        FillChevrons(g, Brushes.Black, cx, cy, 2.75f * s, 2.75f * s, 3.25f * s, 1.6f * s);
                    });
                    break;
                case "outline-chevron":   // контурная папка, стрелка нарисована внутри
                    // На 16 px внутри папки тесно, поэтому стрелка там мельче и тоньше
                    float half = size < 20 ? 2.1f : 2.5f, step = size < 20 ? 2.8f : 3.2f, weight = size < 20 ? 1.2f : 1.4f;
                    Paint(big, delegate(Graphics g)
                    {
                        f.Outline(g, ink);
                        using (SolidBrush brush = new SolidBrush(ink))
                            FillChevrons(g, brush, cx, cy, half * s, half * s, step * s, weight * s);
                    });
                    break;
                case "outline":
                    Paint(big, delegate(Graphics g) { f.Outline(g, ink); });
                    break;
                case "arrows":
                    Paint(big, delegate(Graphics g)
                    {
                        using (SolidBrush brush = new SolidBrush(ink))
                            FillChevrons(g, brush, cx, size / 2f, 4.5f * s, 4f * s, 4.5f * s, 1.75f * s);
                    });
                    break;
                default:
                    return null;
            }
            return Downsample(big, Supersample);
        }
    }

    // Картинка → значок через ICO в памяти: так Windows сохраняет полупрозрачные края
    public static Icon ToIcon(Bitmap bmp)
    {
        using (MemoryStream stream = new MemoryStream(IcoBytes(bmp)))
            return new Icon(stream, bmp.Width, bmp.Height);
    }

    public static byte[] IcoBytes(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = (w + 31) / 32 * 4;
        int[] px = Pixels(bmp);
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter wr = new BinaryWriter(stream))
        {
            wr.Write((short)0); wr.Write((short)1); wr.Write((short)1);                  // заголовок ICO
            wr.Write((byte)(w >= 256 ? 0 : w)); wr.Write((byte)(h >= 256 ? 0 : h));      // запись о картинке
            wr.Write((byte)0); wr.Write((byte)0);
            wr.Write((short)1); wr.Write((short)32);
            wr.Write(40 + w * h * 4 + maskStride * h); wr.Write(22);
            wr.Write(40); wr.Write(w); wr.Write(h * 2);                                   // BITMAPINFOHEADER
            wr.Write((short)1); wr.Write((short)32);
            wr.Write(0); wr.Write(w * h * 4); wr.Write(0); wr.Write(0); wr.Write(0); wr.Write(0);
            for (int y = h - 1; y >= 0; y--)                                               // пиксели BGRA, снизу вверх
                for (int x = 0; x < w; x++) wr.Write(px[y * w + x]);
            for (int y = h - 1; y >= 0; y--)                                               // маска: 1 — прозрачно
            {
                byte[] row = new byte[maskStride];
                for (int x = 0; x < w; x++)
                    if (((px[y * w + x] >> 24) & 0xFF) == 0) row[x >> 3] |= (byte)(0x80 >> (x & 7));
                wr.Write(row);
            }
            wr.Flush();
            return stream.ToArray();
        }
    }

    // Геометрия папки на сетке 16×16; края округлены до целых пикселей, чтобы не было размытия
    sealed class Folder
    {
        public readonly float Left, TabTop, Top, Right, Bottom, TabEnd, SlopeEnd, Corner;
        readonly float s;

        public Folder(float s)
        {
            this.s = s;
            Left = R(1, s); TabTop = R(2, s); Top = R(4, s); Right = R(15, s); Bottom = R(14, s);
            TabEnd = R(6, s); SlopeEnd = R(8, s); Corner = Math.Max(2, R(3, s));
        }

        public void Fill(Graphics g, Color ink)
        {
            using (GraphicsPath p = Shape(0))
            using (SolidBrush brush = new SolidBrush(ink))
                g.FillPath(brush, p);
        }

        // Контур рисуется заливкой «внешний силуэт минус внутренний», а не пером:
        // так линия толщиной в целый пиксель ложится ровно в пиксели и не размывается
        public void Outline(Graphics g, Color ink)
        {
            float w = Math.Max(1, (float)Math.Round(s * 0.9f));   // толщина линии в целых пикселях, как у значков Windows
            using (GraphicsPath ring = Shape(0))
            using (GraphicsPath inner = Shape(w))
            using (SolidBrush brush = new SolidBrush(ink))
            {
                ring.AddPath(inner, false);
                ring.FillMode = FillMode.Alternate;
                g.FillPath(brush, ring);
                g.FillRectangle(brush, Left + w, Top, SlopeEnd - Left - w, w);   // линия под ярлычком
            }
        }

        // Контур папки; inset — отступ внутрь, чтобы обводка ложилась ровно в пиксели
        GraphicsPath Shape(float inset)
        {
            float l = Left + inset, t = TabTop + inset, m = Top + inset, r = Right - inset, b = Bottom - inset;
            float d = Math.Max(1, Corner - 2 * inset);
            GraphicsPath p = new GraphicsPath();
            p.AddArc(l, t, d, d, 180, 90);
            p.AddLine(l + d / 2, t, TabEnd, t);
            p.AddLine(TabEnd, t, SlopeEnd, m);
            p.AddLine(SlopeEnd, m, r - d / 2, m);
            p.AddArc(r - d, m, d, d, 270, 90);
            p.AddArc(r - d, b - d, d, d, 0, 90);
            p.AddArc(l, b - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    static float R(float v, float s) { return (float)Math.Round(v * s); }

    static void Paint(Bitmap big, Action<Graphics> draw)
    {
        using (Graphics g = Graphics.FromImage(big))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;        // целые координаты — границы пикселей
            g.ScaleTransform(Supersample, Supersample);      // рисуем в пикселях готового значка
            draw(g);
        }
    }

    // Уменьшение в k раз: каждый пиксель — среднее k×k пикселей с учётом прозрачности
    static Bitmap Downsample(Bitmap big, int k)
    {
        int w = big.Width / k, h = big.Height / k, n = k * k;
        int[] src = Pixels(big);
        int[] dst = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                long a = 0, r = 0, g = 0, b = 0;
                for (int dy = 0; dy < k; dy++)
                {
                    int row = (y * k + dy) * big.Width + x * k;
                    for (int dx = 0; dx < k; dx++)
                    {
                        int p = src[row + dx];
                        int pa = (p >> 24) & 0xFF;
                        a += pa;
                        r += ((p >> 16) & 0xFF) * pa;
                        g += ((p >> 8) & 0xFF) * pa;
                        b += (p & 0xFF) * pa;
                    }
                }
                int alpha = (int)((a + n / 2) / n);
                int red = a > 0 ? (int)(r / a) : 0, green = a > 0 ? (int)(g / a) : 0, blue = a > 0 ? (int)(b / a) : 0;
                dst[y * w + x] = (alpha << 24) | (red << 16) | (green << 8) | blue;
            }
        Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        SetPixels(small, dst);
        return small;
    }

    // Вырезает фигуру с мягкими краями: непрозрачность значка умножается на (1 − непрозрачность фигуры)
    static void Cut(Bitmap bmp, Action<Graphics> draw)
    {
        using (Bitmap mask = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb))
        {
            Paint(mask, draw);
            int[] px = Pixels(bmp);
            int[] m = Pixels(mask);
            for (int i = 0; i < px.Length; i++)
            {
                int alpha = (px[i] >> 24) & 0xFF;
                int cut = (m[i] >> 24) & 0xFF;
                px[i] = (px[i] & 0x00FFFFFF) | ((alpha * (255 - cut) / 255) << 24);
            }
            SetPixels(bmp, px);
        }
    }

    // Приглушает значок: непрозрачность каждого пикселя умножается на opacity
    static void Fade(Bitmap bmp, float opacity)
    {
        int[] px = Pixels(bmp);
        for (int i = 0; i < px.Length; i++)
        {
            int alpha = (px[i] >> 24) & 0xFF;
            px[i] = (px[i] & 0x00FFFFFF) | ((int)(alpha * opacity + 0.5f) << 24);
        }
        SetPixels(bmp, px);
    }

    static int[] Pixels(Bitmap bmp)
    {
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] px = new int[bmp.Width * bmp.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);
        return px;
    }

    static void SetPixels(Bitmap bmp, int[] px)
    {
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
    }

    // Двойная стрелка »: две «галочки», нарисованные заливкой многоугольника (края одинаково чёткие везде).
    // (cx, cy) — центр, hh — половина высоты, hw — ширина одной галочки, gap — шаг, w — толщина линии
    static void FillChevrons(Graphics g, Brush brush, float cx, float cy, float hh, float hw, float gap, float w)
    {
        float h = w / 2;
        float len = (float)Math.Sqrt(hw * hw + hh * hh);
        float nx = hh * h / len, ny = hw * h / len;   // смещение краёв линии от её середины
        float apex = h * len / hh;                    // насколько острый угол выступает за середину
        float x0 = cx - (gap + hw) / 2;
        for (int i = 0; i < 2; i++)
        {
            float x = x0 + i * gap;
            g.FillPolygon(brush, new PointF[]
            {
                new PointF(x + nx, cy - hh - ny),
                new PointF(x + hw + apex, cy),
                new PointF(x + nx, cy + hh + ny),
                new PointF(x - nx, cy + hh - ny),
                new PointF(x + hw - apex, cy),
                new PointF(x - nx, cy - hh + ny),
            });
        }
    }
}

// Значки для пунктов меню: знаки из системного шрифта значков Windows, как в меню самой Windows 11.
// Если шрифта нет, знак рисуется кодом
static class MenuIcons
{
    const char GlobeGlyph = '';   // «Глобус»
    const char CheckGlyph = '';   // «Галочка»
    static readonly string[] IconFonts = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };   // Windows 11, Windows 10

    // Глобус для пункта «Язык»: 14 px из 16, как значки Windows
    public static Bitmap Globe(int size, Color ink)
    {
        return Draw(GlobeGlyph, size, 14f / 16f, ink, DrawGlobe);
    }

    // Галочка выбранного пункта: 12 px из 16
    public static Bitmap Check(int size, Color ink)
    {
        return Draw(CheckGlyph, size, 12f / 16f, ink, DrawCheck);
    }

    delegate void Fallback(Graphics g, Color ink, int size);

    static Bitmap Draw(char glyph, int size, float fill, Color ink, Fallback fallback)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (GraphicsPath path = GlyphPath(glyph, size, fill))
            {
                if (path != null)
                {
                    using (SolidBrush brush = new SolidBrush(ink)) g.FillPath(brush, path);
                    return bmp;
                }
            }
            fallback(g, ink, size);
        }
        return bmp;
    }

    // Контур знака из шрифта значков, вписанный в квадрат size×size точно по центру; null — шрифта нет
    static GraphicsPath GlyphPath(char glyph, int size, float fill)
    {
        foreach (string name in IconFonts)
        {
            FontFamily family;
            try { family = new FontFamily(name); }
            catch (ArgumentException) { continue; }
            using (family)
            {
                if (!string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                GraphicsPath path = new GraphicsPath();
                path.AddString(glyph.ToString(), family, (int)FontStyle.Regular, size, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF bounds = path.GetBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0) { path.Dispose(); continue; }
                float k = size * fill / Math.Max(bounds.Width, bounds.Height);
                using (Matrix m = new Matrix())
                {
                    m.Translate(size / 2f, size / 2f);
                    m.Scale(k, k);
                    m.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
                    path.Transform(m);
                }
                return path;
            }
        }
        return null;
    }

    static void DrawGlobe(Graphics g, Color ink, int size)
    {
        float s = size / 16f;
        RectangleF r = new RectangleF(1.5f * s, 1.5f * s, 13 * s, 13 * s);
        using (Pen pen = new Pen(ink, Math.Max(1f, s)))
        {
            g.DrawEllipse(pen, r);
            g.DrawEllipse(pen, r.X + r.Width * 0.28f, r.Y, r.Width * 0.44f, r.Height);
            g.DrawLine(pen, r.Left, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);
        }
    }

    static void DrawCheck(Graphics g, Color ink, int size)
    {
        float s = size / 16f;
        using (Pen pen = new Pen(ink, Math.Max(1f, 1.2f * s)))
        {
            pen.LineJoin = LineJoin.Round;
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLines(pen, new PointF[] { new PointF(2.5f * s, 8.5f * s), new PointF(6f * s, 12f * s), new PointF(13.5f * s, 4f * s) });
        }
    }
}

// Плоское меню в духе Windows 11: светлое или тёмное, со скруглённой подсветкой пункта.
// Значки и галочки стоят ровно посередине свободного места слева от текста, с равными
// промежутками; подсветка и разделители отступают от краёв меню одинаково
sealed class FlatRenderer : ToolStripProfessionalRenderer
{
    const int Inset = 4;   // отступ подсветки пункта и разделителей от краёв меню

    public readonly bool Dark;
    readonly Color back, border, hover, fore, foreDisabled, line;
    Bitmap check;   // галочка, нарисованная под размер значков меню

    public Color Fore { get { return fore; } }   // цвет текста — им же рисуются значки пунктов

    public FlatRenderer(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            back = Color.FromArgb(44, 44, 44);
            border = Color.FromArgb(70, 70, 70);
            hover = Color.FromArgb(64, 64, 64);
            fore = Color.FromArgb(255, 255, 255);
            foreDisabled = Color.FromArgb(135, 135, 135);
            line = Color.FromArgb(72, 72, 72);
        }
        else
        {
            back = Color.FromArgb(249, 249, 249);
            border = Color.FromArgb(212, 212, 212);
            hover = Color.FromArgb(232, 232, 232);
            fore = Color.FromArgb(26, 26, 26);
            foreDisabled = Color.FromArgb(150, 150, 150);
            line = Color.FromArgb(222, 222, 222);
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (SolidBrush brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using (Pen pen = new Pen(border)) e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // без отдельной серой колонки под иконками
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        ToolStripItem item = e.Item;
        if (!item.Enabled || !(item.Selected || item.Pressed)) return;
        Rectangle r = new Rectangle(Inset, 1, item.Width - 2 * Inset, item.Height - 2);
        Graphics g = e.Graphics;
        SmoothingMode oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = RoundedRect(r, 4))
        using (SolidBrush brush = new SolidBrush(hover))
            g.FillPath(brush, path);
        g.SmoothingMode = oldMode;
    }

    // Иконки и галочки рисуются вместе с текстом, а не здесь: только при выводе текста известно,
    // где он начинается, и значки можно поставить посередине свободного места слева от него
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (!(e.Item is ToolStripMenuItem)) base.OnRenderItemImage(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        if (!(e.Item is ToolStripMenuItem)) base.OnRenderItemCheck(e);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        ToolStripMenuItem item = e.Item as ToolStripMenuItem;
        if (item != null && e.Text == item.Text) DrawLeftColumn(e.Graphics, item, e.ToolStrip, e.TextRectangle.Left);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle,
            e.Item.Enabled ? fore : foreDisabled, e.TextFormat);
    }

    // Свободное место слева от текста — от края подсветки до начала текста. В нём одна ячейка
    // (иконка или галочка) или две (галочка и превью, как в меню «Значок»), и промежутки
    // между краем, ячейками и текстом одинаковые
    void DrawLeftColumn(Graphics g, ToolStripMenuItem item, ToolStrip strip, int textLeft)
    {
        Image image = item.Image;
        if (image == null && !item.Checked) return;
        // Картинка шириной в две ячейки (см. TwoCells) — это галочка и превью; иначе ячейка одна
        Size area = image != null && image.Width >= 2 * image.Height ? image.Size : strip.ImageScalingSize;
        bool twoCells = area.Width >= 2 * area.Height;
        Size cell = new Size(area.Height, area.Height);
        int cells = twoCells ? 2 : 1;
        float gap = Math.Max(0f, (textLeft - Inset - cells * cell.Width) / (float)(cells + 1));
        int y = (item.Height - cell.Height) / 2;
        Rectangle first = new Rectangle((int)Math.Round(Inset + gap), y, cell.Width, cell.Height);
        Rectangle second = new Rectangle((int)Math.Round(Inset + 2 * gap + cell.Width), y, cell.Width, cell.Height);

        if (twoCells)
        {
            if (item.Checked) DrawCheck(g, first);
            g.DrawImage(image, second, new Rectangle(image.Width - cell.Width, 0, cell.Width, cell.Height), GraphicsUnit.Pixel);
        }
        else if (item.Checked) DrawCheck(g, first);
        else DrawImage(g, image, first);
    }

    // Размер поля под картинки для меню с двумя ячейками: галочка, промежуток, превью
    public static Size TwoCells(Size cell)
    {
        return new Size(2 * cell.Width + (int)Math.Round(cell.Width * 0.4), cell.Height);
    }

    void DrawCheck(Graphics g, Rectangle r)
    {
        if (check == null || check.Width != r.Width)
        {
            if (check != null) check.Dispose();
            check = MenuIcons.Check(r.Width, fore);
        }
        g.DrawImage(check, r);
    }

    static void DrawImage(Graphics g, Image image, Rectangle r)
    {
        if (image.Width != r.Width || image.Height != r.Height) g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, r);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = (e.Item == null || e.Item.Enabled) ? fore : foreDisabled;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (Pen pen = new Pen(line)) e.Graphics.DrawLine(pen, Inset, y, e.Item.Width - Inset - 1, y);
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// Иконки, имена и запуск — через оболочку Windows, как это делает Проводник
static class Shell
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    const uint SHGFI_SMALLICON = 0x1;
    const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_DISPLAYNAME = 0x200;
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // Типы файлов, у которых иконка своя у каждого файла, а не одна на весь тип
    static readonly HashSet<string> ownIconExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".url", ".ico", ".cur", ".ani", ".scr", ".cpl", ".msc", ".appref-ms", ".website", ".library-ms"
    };

    public static bool HasOwnIcon(string extension) { return ownIconExtensions.Contains(extension); }

    static uint InfoSize { get { return (uint)Marshal.SizeOf(typeof(SHFILEINFO)); } }

    // Имя так, как его показывает Проводник: без «.lnk», с учётом настройки расширений и русских имён папок
    public static string GetDisplayName(string path)
    {
        SHFILEINFO info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, InfoSize, SHGFI_DISPLAYNAME) != IntPtr.Zero
            && !string.IsNullOrEmpty(info.szDisplayName))
            return info.szDisplayName;
        return Util.NameFromPath(path);
    }

    static IntPtr GetIconHandle(string path, bool isDir, bool byTypeOnly)
    {
        SHFILEINFO info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON;
        uint attributes = 0;
        if (byTypeOnly)   // файл не читаем, берём стандартную иконку его типа — это быстро
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        }
        if (SHGetFileInfo(path, attributes, ref info, InfoSize, flags) == IntPtr.Zero) return IntPtr.Zero;
        return info.hIcon;
    }

    public static Image GetSmallIcon(string path, bool isDir, bool byTypeOnly)
    {
        IntPtr handle = GetIconHandle(path, isDir, byTypeOnly);
        if (handle == IntPtr.Zero) return null;
        try
        {
            using (Icon icon = Icon.FromHandle(handle)) return icon.ToBitmap();
        }
        catch { return null; }
        finally { Native.DestroyIcon(handle); }
    }

    public static void Open(string path)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(path);
            psi.UseShellExecute = true;
            psi.ErrorDialog = true;   // ошибки и окно «Чем открыть?» покажет сама Windows
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            using (Process.Start(psi)) { }
        }
        catch (System.ComponentModel.Win32Exception) { }   // эту ошибку Windows уже показала сама
        catch (Exception ex) { Log.Error("Открытие " + path, ex); }
    }
}

// Где показывать меню значка. Windows сообщает, где на экране значок программы в трее и где
// проходит панель задач; меню ставится к краю панели задач и к краю значка
static class TrayPlacement
{
    public const int EdgeLeft = 0, EdgeTop = 1, EdgeRight = 2, EdgeBottom = 3;   // как ABE_* в Windows

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("shell32.dll")]
    static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    const uint ABM_GETTASKBARPOS = 5;

    // Закрытые поля NotifyIcon: скрытое окно, которое получает щелчки по значку, и номер значка
    static readonly FieldInfo windowField = typeof(NotifyIcon).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo idField = typeof(NotifyIcon).GetField("id", BindingFlags.Instance | BindingFlags.NonPublic);

    // Окно значка; IntPtr.Zero — если его не удалось найти
    public static IntPtr TrayWindow(NotifyIcon tray)
    {
        try
        {
            NativeWindow window = windowField == null ? null : windowField.GetValue(tray) as NativeWindow;
            return window == null ? IntPtr.Zero : window.Handle;
        }
        catch { return IntPtr.Zero; }
    }

    // Значок в трее на экране; Rectangle.Empty — если Windows его не сообщила (например, значок спрятан под ^)
    public static Rectangle IconRect(NotifyIcon tray)
    {
        try
        {
            IntPtr hwnd = TrayWindow(tray);
            if (hwnd == IntPtr.Zero || idField == null) return Rectangle.Empty;
            NOTIFYICONIDENTIFIER id = new NOTIFYICONIDENTIFIER();
            id.cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
            id.hWnd = hwnd;
            id.uID = (uint)(int)idField.GetValue(tray);
            RECT r;
            if (Shell_NotifyIconGetRect(ref id, out r) != 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
        catch { return Rectangle.Empty; }   // старая Windows или NotifyIcon устроен иначе
    }

    // Панель задач: где она и к какому краю экрана прикреплена
    static bool Taskbar(out Rectangle bounds, out int edge)
    {
        bounds = Rectangle.Empty;
        edge = EdgeBottom;
        try
        {
            APPBARDATA data = new APPBARDATA();
            data.cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA));
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == IntPtr.Zero) return false;
            bounds = Rectangle.FromLTRB(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
            edge = (int)data.uEdge;
            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch { return false; }
    }

    // Где поставить меню размера size, открытое от значка anchor
    public static Point MenuLocation(Rectangle anchor, Size size, bool growLeft)
    {
        Screen screen = Screen.FromRectangle(anchor);
        Rectangle taskbar;
        int edge;
        if (!Taskbar(out taskbar, out edge) || !taskbar.IntersectsWith(screen.Bounds))
        {
            // Панели задач на этом экране не нашлось — считаем, что она внизу, под рабочей областью
            taskbar = Rectangle.FromLTRB(screen.Bounds.Left, screen.WorkingArea.Bottom, screen.Bounds.Right, screen.Bounds.Bottom);
            edge = EdgeBottom;
        }
        return MenuLocation(anchor, size, growLeft, taskbar, edge, screen.WorkingArea);
    }

    // Само правило. Панель задач внизу: нижний край меню — на верхнем крае панели, а если значок
    // выше неё (в списке скрытых значков) — над значком. По горизонтали меню папки (growLeft)
    // стоит правым краем над правым краем значка, служебное — левым краем над левым.
    // Панель сверху или сбоку — то же самое от её края. Меню не выходит за рабочую область экрана
    public static Point MenuLocation(Rectangle anchor, Size size, bool growLeft, Rectangle taskbar, int edge, Rectangle area)
    {
        int x, y;
        switch (edge)
        {
            case EdgeTop:
                x = growLeft ? anchor.Right - size.Width : anchor.Left;
                y = Math.Max(taskbar.Bottom, anchor.Bottom);
                break;
            case EdgeLeft:
                x = Math.Max(taskbar.Right, anchor.Right);
                y = growLeft ? anchor.Bottom - size.Height : anchor.Top;
                break;
            case EdgeRight:
                x = Math.Min(taskbar.Left, anchor.Left) - size.Width;
                y = growLeft ? anchor.Bottom - size.Height : anchor.Top;
                break;
            default:
                x = growLeft ? anchor.Right - size.Width : anchor.Left;
                y = Math.Min(taskbar.Top, anchor.Top) - size.Height;
                break;
        }
        x = Math.Max(area.Left, Math.Min(x, area.Right - size.Width));
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - size.Height));
        return new Point(x, y);
    }
}

static class Native
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string x, string y);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);
}

static class Util
{
    public const string StartupSwitch = "/startup";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string IconsKey = @"Software\TrayFolder\Icons";
    const string OpenKey = @"Software\TrayFolder\Open";
    const string RunPrefix = "TrayFolder: ";

    static string RunValueName(string folder) { return RunPrefix + folder; }

    // Папки, прописанные в автозапуск
    public static List<string> AutostartFolders()
    {
        List<string> result = new List<string>();
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                if (key != null)
                    foreach (string name in key.GetValueNames())
                        if (name.StartsWith(RunPrefix, StringComparison.OrdinalIgnoreCase))
                            result.Add(name.Substring(RunPrefix.Length));
        }
        catch { }
        return result;
    }

    // Какие папки сейчас в трее. Запись снимается только пунктом «Закрыть»: если программу закрыли
    // принудительно (пересборка, выход из Windows), запись остаётся, и папку можно вернуть
    public static void MarkOpen(string folder, bool open)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(OpenKey))
            {
                if (open) key.SetValue(folder, "");
                else key.DeleteValue(folder, false);
            }
        }
        catch (Exception ex) { Log.Error("Список открытых папок", ex); }
    }

    // Папки, которые стоит вернуть в трей: из автозапуска и те, что были открыты в прошлый раз
    public static List<string> SavedFolders()
    {
        List<string> result = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in AutostartFolders())
            if (seen.Add(f)) result.Add(f);
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(OpenKey))
                if (key != null)
                    foreach (string name in key.GetValueNames())
                        if (seen.Add(name)) result.Add(name);
        }
        catch { }
        return result;
    }

    // Один значок на папку: экземпляр программы для папки держит именованный мьютекс
    public static string MutexName(string folder) { return "Local\\TrayFolder_" + Hash(folder.ToLowerInvariant()); }

    public static bool IsRunning(string folder)
    {
        Mutex existing;
        if (!Mutex.TryOpenExisting(MutexName(folder), out existing)) return false;
        existing.Dispose();
        return true;
    }

    public static bool IsAutostart(string folder)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunValueName(folder)) != null;
        }
        catch { return false; }
    }

    public static void SetAutostart(string folder, bool enable)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable)
                    key.SetValue(RunValueName(folder),
                        Quote(Application.ExecutablePath) + " " + StartupSwitch + " " + Quote(folder));
                else
                    key.DeleteValue(RunValueName(folder), false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Автозапуск", ex);
            Message(Lang.F("AutostartFailed", ex.Message));
        }
    }

    // Вид значка запоминается отдельно для каждой папки. Вид, которого больше нет
    // (из прежних версий), заменяется значком по умолчанию
    public static string GetIconStyle(string folder)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(IconsKey))
            {
                string value = key == null ? null : key.GetValue(folder) as string;
                return TrayIcons.IsKnown(value) ? value : TrayIcons.DefaultStyle;
            }
        }
        catch { return TrayIcons.DefaultStyle; }
    }

    public static void SetIconStyle(string folder, string style)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(IconsKey))
                key.SetValue(folder, style);
        }
        catch (Exception ex)
        {
            Log.Error("Сохранение вида значка", ex);
            Message(Lang.F("IconSaveFailed", ex.Message));
        }
    }

    // Отдельный экземпляр программы для ещё одной папки; quiet — без сообщений и без записи в автозапуск
    public static void StartInstance(string folder, bool quiet)
    {
        try
        {
            string args = (quiet ? StartupSwitch + " " : "") + Quote(NormalizePath(folder));
            using (Process.Start(Application.ExecutablePath, args)) { }
        }
        catch (Exception ex)
        {
            Log.Error("Запуск значка для " + folder, ex);
            Message(ex.Message);
        }
    }

    public static bool IsDarkTaskbar()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object value = key == null ? null : key.GetValue("SystemUsesLightTheme");
                return value is int && (int)value == 0;
            }
        }
        catch { return false; }
    }

    public static int ExplorerOption(string name, int fallback)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
            {
                object value = key == null ? null : key.GetValue(name);
                return value is int ? (int)value : fallback;
            }
        }
        catch { return fallback; }
    }

    public static string PickFolder(string initial)
    {
        using (FolderBrowserDialog dialog = new FolderBrowserDialog())
        {
            dialog.Description = Lang.T("PickFolder");
            if (!string.IsNullOrEmpty(initial)) dialog.SelectedPath = initial;
            return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
        }
    }

    public static string NormalizePath(string path)
    {
        path = path.Trim().Trim('"');
        try { path = Path.GetFullPath(path); } catch { }
        if (path.Length > 3) path = path.TrimEnd('\\');
        return path;
    }

    // Имя по пути, без обращения к диску: «C:\Users\Я\Downloads» → «Downloads», корень диска — как есть
    public static string NameFromPath(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    // Кавычки для командной строки. Завершающий «\» удваивается, иначе он «съест» кавычку: D:\ → "D:\\"
    public static string Quote(string s) { return "\"" + (s.EndsWith("\\") ? s + "\\" : s) + "\""; }

    public static string Hash(string s)
    {
        ulong h = 14695981039346656037UL;   // FNV-1a
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    public static string MenuText(string s)
    {
        if (s.Length > 80) s = s.Substring(0, 79) + "…";
        return s.Replace("&", "&&");   // одиночный «&» меню считает подчёркиванием горячей клавиши
    }

    public static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    // Подсказка у значка в трее. Больше 63 символов Windows не принимает, поэтому у недоступной
    // папки укорачивается имя, а пометка «недоступна» остаётся целой
    public const int TrayTextMax = 63;

    public static string TrayText(string name, bool available)
    {
        if (available) return Truncate(name, TrayTextMax);
        string mark = Lang.T("UnavailableMark");
        return Truncate(name, TrayTextMax - mark.Length) + mark;
    }

    public static void Message(string text)
    {
        MessageBox.Show(text, "TrayFolder", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

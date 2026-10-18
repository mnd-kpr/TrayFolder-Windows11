# TrayFolder

**Windows 10 taskbar toolbars, back in Windows 11.** A folder icon in the system tray next to the clock: click it, and the folder's contents open as a menu with subfolders, just like the » chevron on the old taskbar.

![Four icon styles in the system tray, in the dark and light themes](docs/icons.png)

A single C# source file that builds with the compiler already built into Windows. Nothing to install and no dependencies, and the ready exe is about 55 KB.

The code was written with [Claude](https://claude.ai), Anthropic's AI assistant. See [How it was made](#how-it-was-made) for details.

**Other languages:** [Deutsch](#deutsch) · [Español](#español) · [Français](#français) · [Italiano](#italiano) · [Русский](#русский)

## Why

In Windows 10 you could pin any folder to the taskbar as a toolbar: the » chevron next to the clock expanded its contents as a list, and the file you needed opened in a couple of clicks, without a File Explorer window. Windows 11 rewrote the taskbar, and this feature is gone. TrayFolder brings it back as an ordinary tray app, with no Explorer patches and no third-party shells.

## Features

- **Left-click** the icon to open a menu with the folder's contents. Subfolders expand on hover, clicking a file opens the file, and clicking a folder opens it in File Explorer. **Middle-click** opens the whole folder.
- Names, icons, hidden files and sort order are the same as in File Explorer.
- Several folders, several icons, like separate toolbars.
- The menu is light or dark to match the taskbar. The icon is drawn in code at the exact tray size and recolors itself when the theme changes. There are four icon styles to choose from.
- Slow network folders and huge folders are read in the background and don't freeze the app. If a folder becomes unavailable (a USB drive is unplugged, the network drops), its icon is dimmed.
- The interface is available in six languages: English, German, French, Spanish, Italian and Russian. It follows the Windows display language, and you can change it in the right-click menu → **Language**.
- Optional start with Windows, and an error log.

## Installation

**Build it yourself.** This is the recommended way, because you can see all the code you run:

1. Download the repository archive (the green **Code → Download ZIP** button) and extract it to the folder where the app will live permanently, for example `C:\Tools\TrayFolder`.
2. Run `build.bat`. It builds `TrayFolder.exe` with the C# compiler from .NET Framework 4.x, which comes with Windows 10 and 11, so there's nothing to install. If Windows asks whether to run the downloaded file, allow it.
3. Run `TrayFolder.exe` and choose a folder. You can also simply drag a folder onto `TrayFolder.exe`.

**A ready-made exe** is in [Releases](../../releases). GitHub builds it from this same code (see [`.github/workflows/build.yml`](.github/workflows/build.yml)), but the file has no digital signature. So on the first launch Windows shows a SmartScreen warning: click **More info**, then **Run anyway**.

> Windows 11 hides new tray icons behind the ^ arrow. Drag the icon next to the clock, or turn it on in **Settings → Personalization → Taskbar**.

## Usage

- **Right-click** the icon to open the folder in File Explorer, add another folder, choose the icon style and the interface language, turn **Start with Windows** on or off, open the error log, or close the icon.
- An added folder is set to start with Windows automatically.
- `TrayFolder.exe` without arguments brings all saved folders back to the tray, and `TrayFolder.exe "D:\Folder"` adds that folder right away.

## Updating

Put the new `TrayFolder.cs` next to `TrayFolder.exe` and run `build.bat`. It closes the app, builds the new version and starts it again with all your folders.

## Uninstalling

For each folder: right-click → clear **Start with Windows** → **Close**. Then delete the app folder. Settings are stored in the registry under `HKCU\Software\TrayFolder` and the log in `%LOCALAPPDATA%\TrayFolder`; you can delete them too.

## How it works

WinForms and Win32: the tray icon is a `NotifyIcon`, names and icons come from `SHGetFileInfo`, and files are opened with `ShellExecute`, the same way File Explorer does it. The app runs with user rights, writes only to `HKCU` and never goes online. Icons are drawn with 16× supersampling at the exact tray size (16, 20, 24 px and so on), so their edges stay crisp at any display scaling.

## Limitations

- The menu has no File Explorer context menu on right-click and no drag and drop.
- It's an icon in the system tray, not a labeled button on the taskbar.

## How it was made

All of TrayFolder's code, the icons, the interface translations and this README were written by [Claude](https://claude.ai), Anthropic's AI assistant. The idea, requirements, design choices and testing on Windows 11 are by the project author, mnd-kpr.

If you spot a mistake in the translation into your language, please open an [issue](../../issues).

## License

[MIT](LICENSE): you can freely use, modify and distribute the app. It is provided "as is", without warranty.

---

## Deutsch

**TrayFolder bringt die Ordner-Symbolleisten der Windows-10-Taskleiste zurück in Windows 11.** Das Programm legt ein Ordnersymbol neben die Uhr. Ein Klick darauf öffnet den Inhalt des Ordners als verschachteltes Menü, genau wie der alte Pfeil » der Symbolleisten.

- Eine einzige C#-Quelldatei, die mit dem in Windows enthaltenen C#-Compiler gebaut wird: `build.bat` starten, nichts installieren. Die exe ist etwa 55 KB groß.
- Namen, Symbole, versteckte Dateien und Sortierung wie im Datei-Explorer, ein Symbol pro Ordner, helle und dunkle Menüs passend zur Taskleiste, vier einfarbige, im Code gezeichnete Symbolvarianten.
- Langsame Netzwerkordner und riesige Ordner werden im Hintergrund geladen. Ein nicht verfügbarer Ordner bekommt ein abgeblendetes Symbol.
- Fertige, nicht signierte exe-Dateien liegen unter [Releases](../../releases). GitHub Actions baut sie aus genau diesem Quellcode.
- Die Oberfläche gibt es auf Deutsch, Englisch, Französisch, Spanisch, Italienisch und Russisch. Sie folgt der Anzeigesprache von Windows und lässt sich im Rechtsklickmenü unter „Sprache“ umstellen.

**Mit Claude entwickelt.** Code, Symbole, Übersetzungen der Oberfläche und diese Beschreibung hat [Claude](https://claude.ai) geschrieben, der KI-Assistent von Anthropic. Idee, Anforderungen, Designentscheidungen und Tests unter Windows 11 stammen vom Projektautor mnd-kpr. Wenn Ihnen ein Fehler in der deutschen Übersetzung auffällt, eröffnen Sie bitte ein [Issue](../../issues).

Lizenz: [MIT](LICENSE).

## Español

**TrayFolder devuelve a Windows 11 las barras de herramientas de carpetas de la barra de tareas de Windows 10.** El programa coloca un icono de carpeta junto al reloj. Al hacer clic en él, el contenido de la carpeta se abre como un menú en cascada, igual que la antigua flecha » de las barras de herramientas.

- Un único archivo de código fuente en C# que se compila con el compilador de C# incluido en Windows: ejecute `build.bat`, no hay nada que instalar. El exe ocupa unos 55 KB.
- Nombres, iconos, archivos ocultos y orden como en el Explorador de archivos, un icono por carpeta, menús claros u oscuros a juego con la barra de tareas y cuatro estilos de icono monocromos dibujados por código.
- Las carpetas de red lentas y las carpetas enormes se cargan en segundo plano, y el icono de una carpeta no disponible se atenúa.
- En [Releases](../../releases) hay ejecutables listos para usar, sin firma digital, que GitHub Actions compila a partir de este mismo código.
- La interfaz está disponible en español, inglés, alemán, francés, italiano y ruso. Sigue el idioma de visualización de Windows y se puede cambiar en el menú del clic derecho → «Idioma».

**Hecho con Claude.** El código, los iconos, las traducciones de la interfaz y este README los escribió [Claude](https://claude.ai), el asistente de IA de Anthropic. La idea, los requisitos, las decisiones de diseño y las pruebas en Windows 11 son del autor del proyecto, mnd-kpr. Si detecta un error en la traducción al español, abra un [issue](../../issues).

Licencia: [MIT](LICENSE).

## Français

**TrayFolder ramène dans Windows 11 les barres d'outils de dossiers de la barre des tâches de Windows 10.** Le programme place une icône de dossier à côté de l'horloge : un clic dessus ouvre le contenu du dossier sous forme de menu en cascade, comme l'ancienne flèche » des barres d'outils.

- Un seul fichier source C#, compilé avec le compilateur C# fourni avec Windows : lancez `build.bat`, rien à installer. L'exe pèse environ 55 Ko.
- Noms, icônes, fichiers cachés et tri identiques à ceux de l'Explorateur de fichiers, une icône par dossier, menus clairs ou sombres assortis à la barre des tâches, quatre styles d'icône monochromes dessinés par le code.
- Les dossiers réseau lents et les très gros dossiers se chargent en arrière-plan. L'icône d'un dossier indisponible est grisée.
- Des exécutables prêts à l'emploi, non signés, se trouvent dans [Releases](../../releases). GitHub Actions les compile à partir de ce même code.
- L'interface est disponible en français, anglais, allemand, espagnol, italien et russe. Elle suit la langue d'affichage de Windows et se change dans le menu du clic droit → « Langue ».

**Réalisé avec Claude.** Le code, les icônes, les traductions de l'interface et ce README ont été écrits par [Claude](https://claude.ai), l'assistant IA d'Anthropic. L'idée, les exigences, les choix de conception et les tests sous Windows 11 sont l'œuvre de l'auteur du projet, mnd-kpr. Si vous remarquez une erreur dans la traduction française, merci d'ouvrir une [issue](../../issues).

Licence : [MIT](LICENSE).

## Italiano

**TrayFolder riporta in Windows 11 le barre degli strumenti con le cartelle della barra delle applicazioni di Windows 10.** Il programma mette un'icona di cartella accanto all'orologio: un clic e il contenuto della cartella si apre come menu a cascata, proprio come la vecchia freccia » delle barre degli strumenti.

- Un unico file sorgente C# che si compila con il compilatore C# incluso in Windows: avvia `build.bat`, non serve installare nulla. L'exe pesa circa 55 KB.
- Nomi, icone, file nascosti e ordinamento come in Esplora file, un'icona per cartella, menu chiari o scuri in tinta con la barra delle applicazioni, quattro stili di icona monocromatici disegnati dal codice.
- Le cartelle di rete lente e quelle enormi vengono lette in background, e l'icona di una cartella non disponibile viene attenuata.
- Gli eseguibili pronti, senza firma digitale, sono in [Releases](../../releases): li compila GitHub Actions da questo stesso codice.
- L'interfaccia è disponibile in italiano, inglese, tedesco, francese, spagnolo e russo. Segue la lingua di visualizzazione di Windows e si può cambiare nel menu del tasto destro → «Lingua».

**Realizzato con Claude.** Il codice, le icone, le traduzioni dell'interfaccia e questo README sono stati scritti da [Claude](https://claude.ai), l'assistente IA di Anthropic. L'idea, i requisiti, le scelte di design e i test su Windows 11 sono dell'autore del progetto, mnd-kpr. Se noti un errore nella traduzione italiana, apri una [issue](../../issues).

Licenza: [MIT](LICENSE).

## Русский

**TrayFolder возвращает в Windows 11 панели инструментов с папками, которые были на панели задач Windows 10.** Программа ставит значок папки рядом с часами, а клик по нему раскрывает содержимое папки каскадным меню, как стрелка » у старых панелей.

- Один файл исходника на C#, который собирается компилятором C#, встроенным в Windows: запустите `build.bat`, ничего устанавливать не нужно. Готовый exe занимает около 55 КБ.
- Имена, иконки, скрытые файлы и сортировка — как в Проводнике. Каждая папка — отдельный значок. Меню светлое или тёмное в тон панели задач, на выбор четыре монохромных варианта значка, нарисованных кодом.
- Медленные сетевые и огромные папки читаются в фоне, а значок недоступной папки становится приглушённым.
- Готовый exe без цифровой подписи лежит в [Releases](../../releases). Его собирает GitHub Actions из этого же кода.
- Интерфейс на русском, английском, немецком, французском, испанском и итальянском. Язык берётся из Windows, а сменить его можно в меню правой кнопки → «Язык».

**Сделано с Claude.** Код, значки, переводы интерфейса и это описание написал [Claude](https://claude.ai) — ИИ-ассистент компании Anthropic. Идея, требования, выбор вариантов и проверка на Windows 11 — за автором проекта, mnd-kpr. Если заметите неточность в переводе, напишите в [Issues](../../issues).

Лицензия — [MIT](LICENSE).

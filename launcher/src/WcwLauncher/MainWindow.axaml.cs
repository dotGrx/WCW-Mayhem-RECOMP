using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WcwLauncher.Core;
using Path = System.IO.Path;

namespace WcwLauncher;

public partial class MainWindow : Window
{
    private static readonly IBrush Ready = new SolidColorBrush(Color.Parse("#3BD16F"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#F0B44C"));
    private static readonly IBrush Problem = new SolidColorBrush(Color.Parse("#E5242F"));

    private static readonly int[] WindowWidths = [960, 1280, 1440, 1600, 1920, 2560];
    private static readonly double[] ScanlineSteps = [0.3, 0.5, 0.8];
    private static readonly (string Label, int Value)[] DeadzoneSteps =
        [("Low  (5%)", 1638), ("Medium  (10%, default)", PlayerSettings.DefaultDeadzone), ("High  (20%)", 6554)];

    private enum Tab { Display, Enhancements, Controls, Audio, GameFiles }

    private readonly LauncherState _state = LauncherState.Load();
    private readonly DispatcherTimer _kenBurns = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _padTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _kenBurnsStart = DateTime.Now;
    private readonly string? _captureFolder;
    private string? _gameExe;
    private TomlTable _gameToml = new();
    private DiscIdentity _identity = new();
    private DiscInfo _disc = new(null, null, 0, default, DiscStatus.Missing, "");
    private CancellationTokenSource? _hashCancel;
    private double _hashProgress;
    private string? _pendingDisc;
    private bool _running;
    private bool _settingsLoaded;
    private Tab _tab = Tab.Display;
    private int _padCount;
    private Action? _statusAction;

    // What the settings page showed when it opened, for keys whose exact value must survive.
    private int _loadedFullscreen;
    private int _loadedVsync;
    private int _loadedScanlineIndex;
    private int _loadedDeadzone;

    private ComboBox[] PlayerBoxes => [PlayerBox1, PlayerBox2, PlayerBox3, PlayerBox4];
    private TextBlock[] PlayerHints => [PlayerHint1, PlayerHint2, PlayerHint3, PlayerHint4];

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? captureFolder)
    {
        _captureFolder = captureFolder;
        InitializeComponent();
        if (captureFolder != null)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
        }

        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseButton.Click += (_, _) => Close();

        NavPlay.Click += (_, _) => ShowHome();
        NavSettings.Click += (_, _) => OpenSettings(_tab is Tab.Controls or Tab.GameFiles ? Tab.Display : _tab);
        NavControls.Click += (_, _) => OpenSettings(Tab.Controls);
        NavGameFiles.Click += (_, _) => OpenSettings(Tab.GameFiles);
        NavLogs.Click += (_, _) => OpenLogs();
        NavAbout.Click += (_, _) => ShowAbout();
        TabDisplay.Click += (_, _) => ShowTab(Tab.Display);
        TabEnhancements.Click += (_, _) => ShowTab(Tab.Enhancements);
        TabControls.Click += (_, _) => ShowTab(Tab.Controls);
        TabAudio.Click += (_, _) => ShowTab(Tab.Audio);
        TabGameFiles.Click += (_, _) => ShowTab(Tab.GameFiles);

        PlayButton.Click += async (_, _) => await PlayAsync();
        StatusAction.Click += (_, _) => _statusAction?.Invoke();
        CancelSettingsButton.Click += (_, _) => ShowHome();
        SaveSettingsButton.Click += (_, _) => SaveSettings();
        ChooseDiscButton.Click += async (_, _) => await ChooseDiscAsync(applyNow: false);
        OpenGameFolderButton.Click += (_, _) => OpenGameFolder();
        OpenLogsButton.Click += (_, _) => OpenLogs();
        OpenInputIniButton.Click += (_, _) => OpenGameFile(InputMap.FileName);
        OpenKeybindsButton.Click += (_, _) => OpenGameFile(InputMap.KeyboardFileName);
        EditInputIniButton.Click += (_, _) => OpenGameFile(InputMap.FileName);
        EditKeybindsButton.Click += (_, _) => OpenGameFile(InputMap.KeyboardFileName);
        ResolutionBox.SelectionChanged += (_, _) => UpdateResolutionHint();
        RendererBox.SelectionChanged += (_, _) => UpdateRendererHint();
        ScanlinesToggle.IsCheckedChanged += (_, _) => ScanlineStrengthBox.IsEnabled = ScanlinesToggle.IsChecked == true;
        WidescreenToggle.IsCheckedChanged += (_, _) => AspectBox.IsEnabled = WidescreenToggle.IsEnabled && WidescreenToggle.IsChecked == true;
        SmoothMotionToggle.IsCheckedChanged += (_, _) => UpdateSmoothMotionControls();
        SmoothBlendBox.SelectionChanged += (_, _) => UpdateSmoothMotionControls();
        foreach (ComboBox box in PlayerBoxes) box.SelectionChanged += (_, _) => UpdateControllerPanel();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !HomePage.IsVisible) ShowHome();
        };

        VersionText.Text =
            $"Launcher {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}  ·  game: WCW Mayhem (Europe), {DiscIdentity.Serial}";

        _kenBurns.Tick += (_, _) => AnimateBackground();
        _padTimer.Tick += (_, _) => PollControllers();
        _padCount = XInput.Count();
        _padTimer.Start();
        LoadArt();
        SelectRail(NavPlay);
        Refresh();
    }

    // --- Pages -----------------------------------------------------------------------------------

    private void ShowHome()
    {
        _settingsLoaded = false;  // leaving Settings without saving drops the edits
        HomePage.IsVisible = true;
        SettingsPage.IsVisible = false;
        AboutPage.IsVisible = false;
        SelectRail(NavPlay);
        UpdateStatus();
    }

    private void ShowAbout()
    {
        _settingsLoaded = false;
        HomePage.IsVisible = false;
        SettingsPage.IsVisible = false;
        AboutPage.IsVisible = true;
        SelectRail(NavAbout);
    }

    private void OpenSettings(Tab tab)
    {
        if (_gameExe == null)
        {
            ShowHome();
            SetStatus(Problem, $"Build the game first: settings live next to {GameInstall.GameExeName}.");
            return;
        }
        if (!_settingsLoaded) LoadSettingsPage();
        HomePage.IsVisible = false;
        AboutPage.IsVisible = false;
        SettingsPage.IsVisible = true;
        ShowTab(tab);
    }

    private void ShowTab(Tab tab)
    {
        _tab = tab;
        DisplayTab.IsVisible = tab == Tab.Display;
        EnhancementsTab.IsVisible = tab == Tab.Enhancements;
        ControlsTab.IsVisible = tab == Tab.Controls;
        AudioTab.IsVisible = tab == Tab.Audio;
        GameFilesTab.IsVisible = tab == Tab.GameFiles;
        var tabs = new[] { (TabDisplay, Tab.Display), (TabEnhancements, Tab.Enhancements), (TabControls, Tab.Controls),
                           (TabAudio, Tab.Audio), (TabGameFiles, Tab.GameFiles) };
        foreach (var (button, which) in tabs) button.Classes.Set("selected", which == tab);
        SelectRail(tab switch { Tab.Controls => NavControls, Tab.GameFiles => NavGameFiles, _ => NavSettings });
        SettingsScroll.Offset = default;
        if (tab == Tab.Controls) UpdateControllerPanel();
    }

    private void SelectRail(Button selected)
    {
        foreach (Button b in new[] { NavPlay, NavSettings, NavControls, NavGameFiles, NavLogs, NavAbout })
            b.Classes.Set("selected", b == selected);
    }

    // --- Home ------------------------------------------------------------------------------------

    private void Refresh()
    {
        _gameExe = GameInstall.FindGameExe(_state.GameExe);
        _gameToml = GameInstall.ReadGameToml(_gameExe);
        _identity = DiscIdentity.From(_gameToml, GameInstall.GameTomlPath(_gameExe));
        if (_state.DiscPath == null && GameInstall.GuessDisc(_gameExe, _gameToml) is { } guess)
        {
            _state.DiscPath = guess;
            _state.Save();
        }
        DiscInfo disc = DiscImage.Inspect(_state.DiscPath, _identity, _state);
        bool sameDisc = disc.TrackFile == _disc.TrackFile && disc.Size == _disc.Size && disc.ModifiedUtc == _disc.ModifiedUtc;
        if (!(sameDisc && _hashCancel != null && disc.Status == DiscStatus.NeedsHash))
        {
            _disc = disc;
            if (disc.Status == DiscStatus.NeedsHash) StartHash(disc);
            else CancelHash();
        }
        RefreshChips();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_running) return;
        if (_gameExe == null)
        {
            SetStatus(Problem, $"The recompiled game ({GameInstall.GameExeName}) wasn't found. Build WCWMayhemRecomp first.");
            PlayButton.IsEnabled = false;
            return;
        }
        PlayButton.IsEnabled = true;
        switch (_disc.Status)
        {
            case DiscStatus.Missing:
            case DiscStatus.NotADisc:
                SetStatus(Warning, _disc.Detail, "CHOOSE DISC", async () => await ChooseDiscAsync(applyNow: true));
                break;
            case DiscStatus.NeedsHash:
                SetStatus(Warning, $"Checking the disc image…  {_hashProgress:0%}");
                break;
            case DiscStatus.UnknownVersion:
                SetStatus(Warning, _disc.Detail);
                break;
            default:
                var settings = PlayerSettings.Load(Path.GetDirectoryName(_gameExe)!, _gameToml);
                string pads = _padCount == 0 ? "no controller (keyboard)" : XInput.Describe(_padCount);
                string last = _state.LastPlayed is { } t ? $"  ·  last played {t:d MMM, HH:mm}" : "";
                SetStatus(Ready, $"Ready  ·  {InternalResolution.Label(settings.InternalResolutionValue)}  ·  {pads}{last}");
                break;
        }
    }

    private void SetStatus(IBrush dot, string text, string? actionLabel = null, Action? action = null)
    {
        StatusDot.Fill = dot;
        StatusText.Text = text;
        StatusAction.IsVisible = actionLabel != null;
        StatusAction.Content = actionLabel;
        _statusAction = action;
    }

    /// <summary>Small tags for what's switched on (widescreen, smooth motion, ...), at most three.</summary>
    private void RefreshChips()
    {
        ChipsPanel.Children.Clear();
        if (_gameExe == null) return;
        var chips = new List<string>();
        try
        {
            var settings = PlayerSettings.Load(Path.GetDirectoryName(_gameExe)!, _gameToml);
            string modsRoot = LauncherMods.ModsRoot(_gameExe);
            var catalog = ModCatalog.Scan(modsRoot);
            var mods = ModState.Load(modsRoot);
            bool On(string package, string feature) =>
                catalog.Find(package, mods.PinnedVersion(package)) is { } p && p.Feature(feature) != null && mods.IsEnabled(p, feature);
            // First, so it is never the tag that falls off: it changes the game, not just the picture.
            if (On(LauncherMods.UnlockAllPackage, LauncherMods.UnlockAllFeature)) chips.Add("Unlock all");
            if (On(LauncherMods.WidescreenPackage, LauncherMods.WidescreenFeature) &&
                catalog.Find(LauncherMods.WidescreenPackage) is { } ws)
            {
                string aspect = mods.OptionValue(ws, LauncherMods.WidescreenFeature, LauncherMods.WidescreenAspect);
                chips.Add(aspect == "fit" ? "Widescreen" : $"Widescreen {aspect}");
            }
            if (On(LauncherMods.SmoothMotionPackage, LauncherMods.SmoothMotionFeature)) chips.Add("Smooth motion");
            if (On(LauncherMods.PgxpPackage, LauncherMods.PgxpFeature)) chips.Add("PGXP");
            if (On(LauncherMods.CdSpeedPackage, LauncherMods.CdSpeedFeature) &&
                catalog.Find(LauncherMods.CdSpeedPackage, mods.PinnedVersion(LauncherMods.CdSpeedPackage)) is { } cd)
            {
                chips.Add(mods.OptionValue(cd, LauncherMods.CdSpeedFeature, LauncherMods.CdSpeedInstant) == "true"
                    ? "CD speed instant"
                    : $"CD speed {mods.OptionValue(cd, LauncherMods.CdSpeedFeature, LauncherMods.CdSpeedMultiplier)}×");
            }
            if (settings.Fullscreen != 0) chips.Add("Fullscreen");
        }
        catch (Exception)
        {
            return;  // chips are decoration; never block the home screen
        }
        foreach (string chip in chips.Take(3))
        {
            ChipsPanel.Children.Add(new Border { Classes = { "chip" }, Child = new TextBlock { Text = chip } });
        }
    }

    // --- Controllers -----------------------------------------------------------------------------

    private void PollControllers()
    {
        if (WindowState == WindowState.Minimized && !_running) return;
        int count = XInput.Count();
        if (count != _padCount)
        {
            _padCount = count;
            UpdateStatus();
        }
        if (SettingsPage.IsVisible && _tab == Tab.Controls) UpdateControllerPanel();
    }

    private string[] ChosenControllers() => PlayerBoxes
        .Select((box, i) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? DeviceChoice.Normalize(_state.Controllers.ElementAtOrDefault(i)))
        .ToArray();

    private void UpdateControllerPanel()
    {
        bool[] connected = XInput.Connected();
        int count = connected.Count(c => c);
        PadsDot.Fill = count > 0 ? Ready : Warning;
        PadsText.Text = count == 0 ? "No Xbox controller connected" : $"{XInput.Describe(count)} connected";
        PadsHint.Text = count == 0
            ? "Plug one in (wired or wireless) and it shows up here. Automatic gives Player 1 the keyboard until then."
            : $"XInput slot{(count > 1 ? "s" : "")} {string.Join(", ", connected.Select((c, i) => c ? (i + 1).ToString() : null).OfType<string>())}. " +
              "Automatic shares them out in player order when you press PLAY.";

        string[] devices = ControllerPlan.Resolve(ChosenControllers(), count);
        for (int i = 0; i < ControllerPlan.Players; i++)
        {
            string chosen = ChosenControllers()[i];
            PlayerHints[i].Text = devices[i] switch
            {
                DeviceChoice.Gamepad when count == 0 => "Xbox controller (none connected yet)",
                DeviceChoice.Gamepad => chosen == DeviceChoice.Automatic ? "Now: Xbox controller" : "Xbox controller",
                DeviceChoice.Keyboard => chosen == DeviceChoice.Automatic ? "Now: keyboard" : "Keyboard (keybinds.ini)",
                _ => chosen == DeviceChoice.Automatic ? "Now: not plugged in" : "Not plugged in",
            };
        }
        bool multitap = ControllerPlan.NeedsMultitap(devices);
        MultitapHint.Text = multitap
            ? "3 or more players: the PlayStation multitap is switched on for players 3 and 4."
            : "Players 3 and 4 use the PlayStation multitap, switched on when 3 or more players have a controller.";
        int? slots = BuildInfo.PlayerSlots(_gameExe);
        MultitapWarning.IsVisible = slots is { } s && s < BuildInfo.MultitapMinimumSlots;
        MultitapWarning.Text = $"This game build has {slots} player slots; its multitap needs {BuildInfo.MultitapMinimumSlots} " +
                               "(MAX_PLAYERS in CMakeLists.txt), so players 3 and 4 won't reach the game yet.";
    }

    /// <summary>The PlayStation-to-Xbox reference table, from input.ini next to the game.</summary>
    private void BuildButtonLayout()
    {
        var map = InputMap.Load(_gameExe);
        ButtonLayoutGrid.Children.Clear();
        ButtonLayoutGrid.RowDefinitions.Clear();
        (string Key, string Name, Control Glyph)[] buttons =
        [
            ("cross", "Cross", PsxGlyph("M3,3 L13,13 M13,3 L3,13", "#7FA8F5")),
            ("circle", "Circle", PsxGlyph("M8,2.5 A5.5,5.5 0 1 1 7.99,2.5 Z", "#FF6F7E")),
            ("square", "Square", PsxGlyph("M3,3 H13 V13 H3 Z", "#F08FD8")),
            ("triangle", "Triangle", PsxGlyph("M8,2.5 L14,13 H2 Z", "#4FD3A6")),
            ("l1", "L1", PsxBadge("L1")), ("r1", "R1", PsxBadge("R1")),
            ("l2", "L2", PsxBadge("L2")), ("r2", "R2", PsxBadge("R2")),
            ("select", "Select", PsxBadge("SEL")), ("start", "Start", PsxBadge("START")),
            ("l3", "L3", PsxBadge("L3")), ("r3", "R3", PsxBadge("R3")),
        ];
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i % 2 == 0) ButtonLayoutGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var (key, name, glyph) = buttons[i];
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 3, 6, 3) };
            cell.Children.Add(glyph);
            cell.Children.Add(new TextBlock { Text = "←", Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            cell.Children.Add(XboxBadge(map.XboxFor(key)));
            ToolTip.SetTip(cell, $"PlayStation {name}");
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            ButtonLayoutGrid.Children.Add(cell);
        }
        MoveHint.Text = (map.LeftStickMoves ? "D-pad and left stick move (digital pad). " : "D-pad moves. ") +
                        (map.FromFile ? "Read from input.ini next to the game." : "input.ini not found: the game's defaults.");
    }

    private static Control PsxGlyph(string data, string color) => new Border
    {
        Width = 28, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.Parse("#26FFFFFF")),
        Child = new Viewbox
        {
            Width = 12, Height = 12,
            Child = new Canvas
            {
                Width = 16, Height = 16,
                Children =
                {
                    new Avalonia.Controls.Shapes.Path
                    {
                        Data = Geometry.Parse(data), Stroke = new SolidColorBrush(Color.Parse(color)), StrokeThickness = 2.2,
                        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                    },
                },
            },
        },
    };

    private static Control PsxBadge(string text) => new Border
    {
        MinWidth = 28, Height = 22, CornerRadius = new CornerRadius(11), Padding = new Thickness(6, 0),
        Background = new SolidColorBrush(Color.Parse("#26FFFFFF")),
        Child = new TextBlock
        {
            Text = text, FontSize = 9.5, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#ECEAF3")),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static Control XboxBadge(string label)
    {
        string? face = label switch { "A" => "#5DC21E", "B" => "#E5322D", "X" => "#2F7BEA", "Y" => "#F5C400", _ => null };
        if (face != null)
        {
            return new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.Parse("#1A1A1F")),
                BorderBrush = new SolidColorBrush(Color.Parse(face)), BorderThickness = new Thickness(1.5),
                Child = new TextBlock
                {
                    Text = label, FontSize = 11, FontWeight = FontWeight.Black, Foreground = new SolidColorBrush(Color.Parse(face)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
        return new TextBlock
        {
            Text = label, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#ECEAF3")),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // --- Disc check: size first, then a background MD5 of track 1, cached in launcher.json ------

    private void StartHash(DiscInfo disc)
    {
        CancelHash();
        var cancel = new CancellationTokenSource();
        _hashCancel = cancel;
        _hashProgress = 0;
        string track = disc.TrackFile!;
        Task.Run(() => DiscImage.Md5(track, p => Dispatcher.UIThread.Post(() =>
        {
            if (_hashCancel != cancel) return;
            _hashProgress = p;
            if (_disc.Status == DiscStatus.NeedsHash) UpdateStatus();
        }), cancel.Token), cancel.Token).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (_hashCancel != cancel) return;
            _hashCancel = null;
            if (task.IsCanceled) return;
            if (task.Exception is { } error)
            {
                _disc = disc with
                {
                    Status = DiscStatus.UnknownVersion,
                    Detail = $"The disc image couldn't be read: {error.InnerException?.Message ?? error.Message}",
                };
            }
            else
            {
                _state.RememberMd5(track, disc.Size, disc.ModifiedUtc, task.Result);
                _state.Save();
                _disc = DiscImage.WithMd5(disc, task.Result, _identity);
            }
            UpdateStatus();
            if (SettingsPage.IsVisible && _pendingDisc == null) ShowDisc(_state.DiscPath);
        }));
    }

    private void CancelHash()
    {
        _hashCancel?.Cancel();
        _hashCancel = null;
    }

    // --- Play ------------------------------------------------------------------------------------

    private async Task PlayAsync()
    {
        if (_running || _gameExe == null) return;
        if (!_disc.Playable || _state.DiscPath == null)
        {
            // No disc chosen yet: offer the choice right at the button.
            await ChooseDiscAsync(applyNow: true);
            return;
        }
        if (_disc.Status == DiscStatus.NeedsHash) CancelHash();  // checked again next time

        // Automatic controllers are decided now, from what is plugged in at this moment.
        int pads = XInput.Count();
        string[] devices = ControllerPlan.Resolve(_state.Controllers, pads);
        bool multitap = ControllerPlan.NeedsMultitap(devices);
        string note = $"controllers: {ControllerPlan.Summary(devices)} ({XInput.Describe(pads)} via XInput; " +
                      $"choices {string.Join("/", _state.Controllers.Take(ControllerPlan.Players).Select(DeviceChoice.Normalize))})";
        try
        {
            var settings = PlayerSettings.Load(Path.GetDirectoryName(_gameExe)!, _gameToml);
            settings.WriteControllerAssignment(devices, multitap);
            if (settings.HasChanges) settings.Save();
        }
        catch (Exception ex)
        {
            note += $"; couldn't write settings.toml: {ex.Message}";
        }

        _running = true;
        PlayButton.IsEnabled = false;
        PlayLabel.Text = "RUNNING";
        SetStatus(Ready, $"The game is running ({string.Join(", ", devices.Select((d, i) => d == DeviceChoice.None ? null : $"P{i + 1} {ControllerPlan.Label(d).ToLowerInvariant()}").OfType<string>())}). " +
                         "This window comes back when it closes.");
        _state.LastPlayed = DateTime.Now;
        _state.Save();
        WindowState = WindowState.Minimized;

        try
        {
            SessionResult result = await GameRunner.RunAsync(_gameExe, _state.DiscPath, note);
            _running = false;
            ComeBack(result.WasForeground);
            Refresh();
            if (result.Crashed)
            {
                string why = result.Reason is { } r && r != "atexit" ? $" ({r})" : $" (exit code 0x{unchecked((uint)result.ExitCode):X8})";
                SetStatus(Problem, $"The game crashed{why}. Its output and crash report are in the logs folder.", "OPEN LOGS", OpenLogs);
            }
            else if (result.ExitCode != 0)
            {
                SetStatus(Problem, $"The game stopped: {result.LastError ?? $"exit code {result.ExitCode}"}", "OPEN LOGS", OpenLogs);
            }
            else
            {
                string played = result.Duration.TotalMinutes >= 1
                    ? $"{(int)result.Duration.TotalMinutes} min"
                    : $"{(int)result.Duration.TotalSeconds} s";
                SetStatus(Ready, $"Session ended after {played}. Ready to play again.");
            }
        }
        catch (Exception ex)
        {
            _running = false;
            WindowState = WindowState.Normal;
            SetStatus(Problem, $"The game couldn't start: {ex.Message}");
        }
        finally
        {
            PlayLabel.Text = "PLAY";
            PlayButton.IsEnabled = _gameExe != null;
        }
    }

    // Back from the game: to the front if the player was in it, otherwise quietly behind
    // whatever they switched to, with a taskbar flash.
    private void ComeBack(bool takeFocus)
    {
        if (takeFocus)
        {
            WindowState = WindowState.Normal;
            Activate();
            return;
        }
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        Win32.ShowWindow(hwnd, Win32.SwShowNoActivate);
        Win32.Flash(hwnd);
    }

    private void OpenLogs()
    {
        try
        {
            GameRunner.OpenFolder(GameRunner.LogsFolder);
        }
        catch (Exception ex)
        {
            SetStatus(Problem, $"Couldn't open the logs folder: {ex.Message}");
        }
    }

    private void OpenGameFolder()
    {
        if (_gameExe != null) GameRunner.OpenFolder(Path.GetDirectoryName(_gameExe)!);
    }

    /// <summary>Opens input.ini / keybinds.ini next to the game in the user's editor for .ini files.</summary>
    private void OpenGameFile(string name)
    {
        if (_gameExe == null) return;
        string path = Path.Combine(Path.GetDirectoryName(_gameExe)!, name);
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else GameRunner.OpenFolder(Path.GetDirectoryName(path)!);
        }
        catch (Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SettingsWarning.Text = $"Couldn't open {name}: {ex.Message}";
                SettingsWarning.IsVisible = true;
            }
        }
    }

    private async Task<bool> ChooseDiscAsync(bool applyNow)
    {
        string? current = _pendingDisc ?? _state.DiscPath;
        string? dir = current != null ? Path.GetDirectoryName(current) : null;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose your WCW Mayhem disc image (the .cue next to its .bin)",
            AllowMultiple = false,
            SuggestedStartLocation = dir != null && Directory.Exists(dir) ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null,
            FileTypeFilter =
            [
                new FilePickerFileType("Cue sheet") { Patterns = ["*.cue"] },
                new FilePickerFileType("PlayStation disc image") { Patterns = DiscImage.Patterns },
                FilePickerFileTypes.All,
            ],
        });
        string? path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (path == null) return false;
        if (applyNow)
        {
            _state.DiscPath = path;
            _state.Save();
            Refresh();
        }
        else
        {
            _pendingDisc = path;
            ShowDisc(path);
        }
        return true;
    }

    // --- Settings --------------------------------------------------------------------------------

    private void LoadSettingsPage()
    {
        string gameDir = Path.GetDirectoryName(_gameExe!)!;
        var settings = PlayerSettings.Load(gameDir, _gameToml);
        var catalog = ModCatalog.Scan(LauncherMods.ModsRoot(_gameExe!));
        var mods = ModState.Load(LauncherMods.ModsRoot(_gameExe!));

        // Display.
        ResolutionBox.Items.Clear();
        int display = InternalResolution.PrimaryDisplayHeight();
        int gameDefault = InternalResolution.Parse(_gameToml.Table("video")?.GetValueOrDefault("internal_resolution")) ?? 1440;
        foreach (var (_, value) in InternalResolution.Presets)
        {
            int scale = InternalResolution.Scale(value, display);
            string label = value switch
            {
                InternalResolution.Native => "Native  (the console's own)",
                InternalResolution.Display => $"Match display  ({display} lines, {scale}×)",
                _ => $"{InternalResolution.Label(value)}  ({scale}×)",
            };
            if (value == gameDefault) label += "  (recommended)";
            ResolutionBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        int resolution = settings.InternalResolutionValue;
        if (InternalResolution.IdFor(resolution) == null)
        {
            ResolutionBox.Items.Add(new ComboBoxItem
            {
                Content = $"{resolution} lines  ({InternalResolution.Scale(resolution, display)}×, from settings.toml)",
                Tag = resolution,
            });
        }
        SelectByTag(ResolutionBox, resolution);
        UpdateResolutionHint();

        RendererBox.Items.Clear();
        RendererBox.Items.Add(new ComboBoxItem { Content = "OpenGL  (recommended)", Tag = "opengl" });
        RendererBox.Items.Add(new ComboBoxItem { Content = "Software", Tag = "software" });
        if (settings.VulkanOffered) RendererBox.Items.Add(new ComboBoxItem { Content = "Vulkan  (experimental)", Tag = "vulkan" });
        SelectByTag(RendererBox, settings.Renderer);
        UpdateRendererHint();

        _loadedFullscreen = settings.Fullscreen;
        FullscreenToggle.IsChecked = _loadedFullscreen != 0;
        _loadedVsync = settings.VSync;
        VsyncToggle.IsChecked = _loadedVsync != 0;

        WindowSizeBox.Items.Clear();
        WindowSizeBox.Items.Add(new ComboBoxItem { Content = "Automatic  (fits your screen)", Tag = 0 });
        var widths = WindowWidths.ToList();
        if (settings.WindowWidth is { } w && !widths.Contains(w)) widths.Add(w);
        foreach (int width in widths.Order()) WindowSizeBox.Items.Add(new ComboBoxItem { Content = $"{width} pixels wide", Tag = width });
        SelectByTag(WindowSizeBox, settings.WindowWidth ?? 0);

        TextureBox.SelectedIndex = settings.BilinearTextures ? 1 : 0;
        AntialiasingToggle.IsChecked = settings.Antialiasing;
        CrtBox.SelectedIndex = Math.Max(0, Array.IndexOf(PlayerSettings.CrtFilters, settings.CrtFilter));
        ScanlinesToggle.IsChecked = settings.Scanlines;
        _loadedScanlineIndex = NearestIndex(ScanlineSteps, settings.ScanlineStrength);
        ScanlineStrengthBox.SelectedIndex = _loadedScanlineIndex;
        ScanlineStrengthBox.IsEnabled = settings.Scanlines;

        // Enhancements (mods\state.toml).
        ShowModToggle(catalog, mods, LauncherMods.WidescreenPackage, LauncherMods.WidescreenFeature, WidescreenToggle, WidescreenHint);
        FillChoices(AspectBox, catalog, mods, LauncherMods.WidescreenPackage, LauncherMods.WidescreenFeature, LauncherMods.WidescreenAspect);
        AspectBox.IsEnabled = WidescreenToggle.IsEnabled && WidescreenToggle.IsChecked == true;
        ShowModToggle(catalog, mods, LauncherMods.SmoothMotionPackage, LauncherMods.SmoothMotionFeature, SmoothMotionToggle, SmoothMotionHint);
        FillChoices(SmoothRateBox, catalog, mods, LauncherMods.SmoothMotionPackage, LauncherMods.SmoothMotionFeature, LauncherMods.SmoothMotionRate);
        FillChoices(SmoothBlendBox, catalog, mods, LauncherMods.SmoothMotionPackage, LauncherMods.SmoothMotionFeature, LauncherMods.SmoothMotionBlend);
        UpdateSmoothMotionControls();
        ShowModToggle(catalog, mods, LauncherMods.PgxpPackage, LauncherMods.PgxpFeature, PgxpToggle, PgxpHint);
        FillCdSpeed(catalog, mods);
        FastBootToggle.IsChecked = settings.FastBoot;
        ShowModToggle(catalog, mods, LauncherMods.UnlockAllPackage, LauncherMods.UnlockAllFeature, UnlockAllToggle, UnlockAllHint);

        // Controls.
        for (int i = 0; i < ControllerPlan.Players; i++)
        {
            ComboBox box = PlayerBoxes[i];
            box.Items.Clear();
            box.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = DeviceChoice.Automatic });
            box.Items.Add(new ComboBoxItem { Content = "Xbox controller / gamepad", Tag = DeviceChoice.Gamepad });
            box.Items.Add(new ComboBoxItem { Content = "Keyboard", Tag = DeviceChoice.Keyboard });
            box.Items.Add(new ComboBoxItem { Content = "None", Tag = DeviceChoice.None });
            SelectByTag(box, DeviceChoice.Normalize(_state.Controllers.ElementAtOrDefault(i)));
        }
        PadTypeBox.Items.Clear();
        PadTypeBox.Items.Add(new ComboBoxItem { Content = "Digital pad  (left stick moves)", Tag = "digital" });
        PadTypeBox.Items.Add(new ComboBoxItem { Content = "DualShock  (analog + vibration)", Tag = "analog" });
        string[] modes = Enumerable.Range(1, ControllerPlan.Players).Select(settings.PadMode).ToArray();
        if (modes.Distinct().Count() > 1) PadTypeBox.Items.Add(new ComboBoxItem { Content = "Per player  (as in settings.toml)", Tag = "mixed" });
        SelectByTag(PadTypeBox, modes.Distinct().Count() > 1 ? "mixed" : modes[0]);
        DeadzoneBox.Items.Clear();
        foreach (var (label, value) in DeadzoneSteps) DeadzoneBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _loadedDeadzone = settings.Deadzone(1);
        if (DeadzoneSteps.All(d => d.Value != _loadedDeadzone))
            DeadzoneBox.Items.Add(new ComboBoxItem { Content = $"{_loadedDeadzone * 100.0 / 32767:0}%  (from settings.toml)", Tag = _loadedDeadzone });
        SelectByTag(DeadzoneBox, _loadedDeadzone);
        BuildButtonLayout();

        // Audio.
        SpuHqToggle.IsChecked = settings.HighQualityAudio;
        FrequencyBox.Items.Clear();
        FrequencyBox.Items.Add(new ComboBoxItem { Content = "44.1 kHz  (default)", Tag = 44100 });
        FrequencyBox.Items.Add(new ComboBoxItem { Content = "48 kHz", Tag = 48000 });
        if (settings.AudioFrequency is not (44100 or 48000))
            FrequencyBox.Items.Add(new ComboBoxItem { Content = $"{settings.AudioFrequency / 1000.0:0.###} kHz", Tag = settings.AudioFrequency });
        SelectByTag(FrequencyBox, settings.AudioFrequency);

        // Game files.
        _pendingDisc = null;
        ShowDisc(_state.DiscPath);
        GameFolderText.Text = gameDir;
        LogsFolderText.Text = GameRunner.LogsFolder;

        var warnings = new List<string>();
        if (settings.SyntaxError != null)
            warnings.Add($"settings.toml has a syntax error ({settings.SyntaxError}), so the game ignores all of it. Fix or delete it, then save here.");
        if (mods.Error != null)
            warnings.Add($"mods\\state.toml couldn't be read ({mods.Error}), so the game starts with no mods. Saving rewrites it (the old one is kept as state.toml.bad).");
        warnings.AddRange(catalog.Errors.Select(e => $"Mod manifest skipped: {e}"));
        SettingsWarning.Text = string.Join("\n", warnings);
        SettingsWarning.IsVisible = warnings.Count > 0;
        _settingsLoaded = true;
    }

    private static void ShowModToggle(ModCatalog catalog, ModState mods, string packageId, string featureId,
        ToggleSwitch toggle, TextBlock hint)
    {
        ModPackage? package = catalog.Find(packageId, mods.PinnedVersion(packageId));
        if (package?.Feature(featureId) == null)
        {
            toggle.IsChecked = false;
            toggle.IsEnabled = false;
            hint.Tag ??= hint.Text;
            hint.Text = $"Not available: this game build has no {packageId} mod (mods\\bundled next to the game).";
            return;
        }
        if (hint.Tag is string original) hint.Text = original;
        toggle.IsEnabled = true;
        toggle.IsChecked = mods.IsEnabled(package, featureId);
    }

    private static void FillChoices(ComboBox box, ModCatalog catalog, ModState mods, string packageId, string featureId, string optionId)
    {
        box.Items.Clear();
        ModPackage? package = catalog.Find(packageId, mods.PinnedVersion(packageId));
        ModOption? option = package?.Option(featureId, optionId);
        if (package == null || option == null)
        {
            box.IsEnabled = false;
            return;
        }
        string current = mods.OptionValue(package, featureId, optionId);
        foreach (ModChoice choice in option.Choices)
        {
            string label = choice.Label;
            if (choice.Value == option.Default) label += "  (default)";
            box.Items.Add(new ComboBoxItem { Content = label, Tag = choice.Value });
        }
        if (option.Choices.All(c => c.Value != current))
            box.Items.Add(new ComboBoxItem { Content = $"{current}  (from state.toml)", Tag = current });
        SelectByTag(box, current);
    }

    private void UpdateSmoothMotionControls()
    {
        bool on = SmoothMotionToggle.IsEnabled && SmoothMotionToggle.IsChecked == true;
        SmoothRateBox.IsEnabled = on && SmoothRateBox.Items.Count > 0;
        SmoothBlendBox.IsEnabled = on && SmoothBlendBox.Items.Count > 0;
        SmoothBlendHint.Text = (SmoothBlendBox.SelectedItem as ComboBoxItem)?.Tag as string == "linear"
            ? "Linear always crossfades between frames."
            : "Motion-adaptive crossfades small changes and cuts cleanly on big ones, for fewer double images.";
    }

    private void ShowDisc(string? path)
    {
        DiscText.Text = path ?? "No disc image chosen";
        DiscInfo info = path == _state.DiscPath ? _disc : DiscImage.Inspect(path, _identity, _state);
        DiscStatusText.Text = info.Status == DiscStatus.NeedsHash
            ? (path == _state.DiscPath ? "Checking the disc image…" : "Right size; it's checked once you save.")
            : info.Detail;
    }

    private void UpdateResolutionHint()
    {
        int value = (ResolutionBox.SelectedItem as ComboBoxItem)?.Tag is int v ? v : 1440;
        ResolutionHint.Text = value switch
        {
            InternalResolution.Native => "The PlayStation's own resolution. Lightest on the GPU; PGXP has no visible effect here.",
            720 => "Three times the console's resolution in each direction.",
            1080 => "Five times the console's resolution, resolved down to 1080 lines.",
            1440 => "Six times the console's resolution: crisp, and easy for a modern GPU.",
            2160 => "Nine times the console's resolution. Wants a strong GPU.",
            2880 or 4320 => "Very demanding. The game steps down to what your GPU can hold.",
            InternalResolution.Display => "Renders at your screen's own height.",
            _ => "A custom value from settings.toml.",
        };
    }

    private void UpdateRendererHint()
    {
        RendererHint.Text = ((RendererBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "software" => "Draws on the CPU: slower, and limited to 4× internal resolution. Smooth motion switches back to OpenGL.",
            "vulkan" => "Experimental in this recomp.",
            _ => "Hardware rendering, needed for high internal resolutions and smooth motion.",
        };
    }

    private void SaveSettings()
    {
        if (_gameExe == null) return;
        string gameDir = Path.GetDirectoryName(_gameExe)!;
        try
        {
            var settings = PlayerSettings.Load(gameDir, _gameToml);
            if (ResolutionBox.SelectedItem is ComboBoxItem { Tag: int resolution }) settings.InternalResolutionValue = resolution;
            if (RendererBox.SelectedItem is ComboBoxItem { Tag: string renderer }) settings.Renderer = renderer;
            // A hand-set exclusive fullscreen (2) or adaptive vsync (-1) survives while switched on.
            settings.Fullscreen = FullscreenToggle.IsChecked == true ? (_loadedFullscreen == 2 ? 2 : 1) : 0;
            settings.VSync = VsyncToggle.IsChecked == true ? (_loadedVsync == -1 ? -1 : 1) : 0;
            if (WindowSizeBox.SelectedItem is ComboBoxItem { Tag: int width }) settings.WindowWidth = width == 0 ? null : width;
            settings.BilinearTextures = TextureBox.SelectedIndex == 1;
            settings.Antialiasing = AntialiasingToggle.IsChecked == true;
            settings.CrtFilter = PlayerSettings.CrtFilters[Math.Clamp(CrtBox.SelectedIndex, 0, PlayerSettings.CrtFilters.Length - 1)];
            settings.Scanlines = ScanlinesToggle.IsChecked == true;
            if (ScanlineStrengthBox.SelectedIndex >= 0 && ScanlineStrengthBox.SelectedIndex != _loadedScanlineIndex)
                settings.ScanlineStrength = ScanlineSteps[ScanlineStrengthBox.SelectedIndex];
            settings.FastBoot = FastBootToggle.IsChecked == true;
            settings.HighQualityAudio = SpuHqToggle.IsChecked == true;
            if (FrequencyBox.SelectedItem is ComboBoxItem { Tag: int frequency }) settings.AudioFrequency = frequency;
            if (PadTypeBox.SelectedItem is ComboBoxItem { Tag: string mode } && mode != "mixed")
            {
                for (int p = 1; p <= ControllerPlan.Players; p++) settings.SetPadMode(p, mode);
            }
            if (DeadzoneBox.SelectedItem is ComboBoxItem { Tag: int deadzone } && deadzone != _loadedDeadzone)
            {
                for (int p = 1; p <= ControllerPlan.Players; p++) settings.SetDeadzone(p, deadzone);
            }

            string modsRoot = LauncherMods.ModsRoot(_gameExe);
            var catalog = ModCatalog.Scan(modsRoot);
            var mods = ModState.Load(modsRoot);
            SetMod(catalog, mods, LauncherMods.WidescreenPackage, LauncherMods.WidescreenFeature, WidescreenToggle,
                (LauncherMods.WidescreenAspect, AspectBox));
            SetMod(catalog, mods, LauncherMods.SmoothMotionPackage, LauncherMods.SmoothMotionFeature, SmoothMotionToggle,
                (LauncherMods.SmoothMotionRate, SmoothRateBox), (LauncherMods.SmoothMotionBlend, SmoothBlendBox));
            SetMod(catalog, mods, LauncherMods.PgxpPackage, LauncherMods.PgxpFeature, PgxpToggle);
            SetCdSpeed(catalog, mods);
            SetMod(catalog, mods, LauncherMods.UnlockAllPackage, LauncherMods.UnlockAllFeature, UnlockAllToggle);
            // psx.enhancement.fast-loading is no longer offered; an old state.toml entry for it is left as it is.

            if (settings.HasChanges) settings.Save();
            mods.Save();
        }
        catch (Exception ex)
        {
            SettingsWarning.Text = $"Couldn't save settings: {ex.Message}";
            SettingsWarning.IsVisible = true;
            return;
        }
        // Player device choices are the launcher's own (Automatic is resolved at each PLAY).
        _state.Controllers = ChosenControllers().ToList();
        if (_pendingDisc != null) _state.DiscPath = _pendingDisc;
        _state.Save();
        Refresh();
        ShowHome();
    }

    // CD speed as one dropdown: Off (feature disabled) or a multiplier. Tag 0 = off, -1 = a hand-set
    // "instant" from state.toml (kept while selected), N = multiplier N.
    private void FillCdSpeed(ModCatalog catalog, ModState mods)
    {
        CdSpeedBox.Items.Clear();
        ModPackage? package = catalog.Find(LauncherMods.CdSpeedPackage, mods.PinnedVersion(LauncherMods.CdSpeedPackage));
        if (package?.Feature(LauncherMods.CdSpeedFeature) == null)
        {
            CdSpeedBox.Items.Add(new ComboBoxItem { Content = "Not in this build", Tag = 0 });
            CdSpeedBox.SelectedIndex = 0;
            CdSpeedBox.IsEnabled = false;
            CdSpeedHint.Text = $"This game build has no {LauncherMods.CdSpeedPackage} mod (mods\\bundled next to the game).";
            return;
        }
        CdSpeedBox.IsEnabled = true;
        CdSpeedHint.Text = "WCW Mayhem paces its own loading screens, so this barely shortens them (a Quick Start match load measured the same at 4×). If a load ever stalls, turn it off.";
        CdSpeedBox.Items.Add(new ComboBoxItem { Content = "Off  (the console's own speed)", Tag = 0 });
        foreach (int step in LauncherMods.CdSpeedSteps)
        {
            CdSpeedBox.Items.Add(new ComboBoxItem
            {
                Content = $"{step}×",
                Tag = step,
            });
        }
        int current = 0;
        if (mods.IsEnabled(package, LauncherMods.CdSpeedFeature))
        {
            if (mods.OptionValue(package, LauncherMods.CdSpeedFeature, LauncherMods.CdSpeedInstant) == "true")
            {
                CdSpeedBox.Items.Add(new ComboBoxItem { Content = "Instant  (from state.toml)", Tag = -1 });
                current = -1;
            }
            else
            {
                current = int.TryParse(mods.OptionValue(package, LauncherMods.CdSpeedFeature, LauncherMods.CdSpeedMultiplier),
                    out int n) && n >= 1 ? n : LauncherMods.CdSpeedRecommended;
                if (Array.IndexOf(LauncherMods.CdSpeedSteps, current) < 0)
                    CdSpeedBox.Items.Add(new ComboBoxItem { Content = $"{current}×  (from state.toml)", Tag = current });
            }
        }
        SelectByTag(CdSpeedBox, current);
    }

    private void SetCdSpeed(ModCatalog catalog, ModState mods)
    {
        ModPackage? package = catalog.Find(LauncherMods.CdSpeedPackage, mods.PinnedVersion(LauncherMods.CdSpeedPackage));
        if (package?.Feature(LauncherMods.CdSpeedFeature) == null || !CdSpeedBox.IsEnabled) return;
        int choice = CdSpeedBox.SelectedItem is ComboBoxItem { Tag: int tag } ? tag : 0;
        if (choice == 0)
        {
            mods.Set(package, LauncherMods.CdSpeedFeature, false);
        }
        else if (choice < 0)
        {
            mods.Set(package, LauncherMods.CdSpeedFeature, true);  // keep the hand-set instant mode
        }
        else
        {
            mods.Set(package, LauncherMods.CdSpeedFeature, true, new Dictionary<string, string>
            {
                [LauncherMods.CdSpeedMultiplier] = choice.ToString(),
                [LauncherMods.CdSpeedInstant] = "false",
            });
        }
    }

    private static void SetMod(ModCatalog catalog, ModState mods, string packageId, string featureId, ToggleSwitch toggle,
        params (string Option, ComboBox Box)[] options)
    {
        ModPackage? package = catalog.Find(packageId, mods.PinnedVersion(packageId));
        if (package?.Feature(featureId) == null || !toggle.IsEnabled) return;
        var values = new Dictionary<string, string>();
        foreach (var (option, box) in options)
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string value }) values[option] = value;
        }
        mods.Set(package, featureId, toggle.IsChecked == true, values);
    }

    private static void SelectByTag(ComboBox box, object value)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value))
                           ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static int NearestIndex(double[] steps, double value)
    {
        int best = 0;
        for (int i = 1; i < steps.Length; i++)
        {
            if (Math.Abs(steps[i] - value) < Math.Abs(steps[best] - value)) best = i;
        }
        return best;
    }

    // --- Art -------------------------------------------------------------------------------------

    private void LoadArt()
    {
        Bitmap? background = TryBitmap(LauncherArt.Background);
        BackgroundImage.Source = background;
        BackgroundLayer.IsVisible = background != null;
        if (background != null)
        {
            BackgroundLayer.SizeChanged += (_, _) => LayoutBackground();
            _kenBurns.Start();
        }

        LogoImage.Source = TryBitmap(LauncherArt.Logo);
        LogoImage.IsVisible = LogoImage.Source != null;
        // The cover carries the game's own logo; a second, typed one would compete with it.
        TitleWordmark.IsVisible = LogoImage.Source == null && background == null;
    }

    private static Bitmap? TryBitmap(string? path)
    {
        try
        {
            return path != null ? new Bitmap(path) : null;
        }
        catch (Exception)
        {
            return null;  // unreadable art: keep the drawn backdrop / typed title
        }
    }

    // Cover the window (uniform to fill), centred across and anchored 40% down, so on the 16:9
    // cover both the face at the top and the logo at the bottom stay in view.
    private void LayoutBackground()
    {
        if (BackgroundImage.Source is not Bitmap bitmap) return;
        Size area = BackgroundLayer.Bounds.Size;
        if (area.Width <= 0 || area.Height <= 0) return;
        double scale = Math.Max(area.Width / bitmap.Size.Width, area.Height / bitmap.Size.Height);
        BackgroundImage.Width = bitmap.Size.Width * scale;
        BackgroundImage.Height = bitmap.Size.Height * scale;
        Canvas.SetLeft(BackgroundImage, (area.Width - BackgroundImage.Width) / 2);
        Canvas.SetTop(BackgroundImage, (area.Height - BackgroundImage.Height) * 0.4);
    }

    // A slow, endless push in and drift (about 40 s per cycle), small enough to keep the face whole.
    private void AnimateBackground()
    {
        if (WindowState == WindowState.Minimized) return;
        double t = (DateTime.Now - _kenBurnsStart).TotalSeconds;
        double phase = (1 - Math.Cos(t * 2 * Math.PI / 40.0)) / 2;  // 0..1..0
        double scale = 1.0 + 0.035 * phase;
        if (BackgroundImage.RenderTransform is TransformGroup { Children: [ScaleTransform zoom, TranslateTransform drift] })
        {
            zoom.ScaleX = scale;
            zoom.ScaleY = scale;
            drift.X = -10 * phase;
        }
    }

    // --- Screenshots (--capture-screenshots <folder>) --------------------------------------------

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_captureFolder == null) return;
        try
        {
            // Let the disc check (normally cached by --self-test) and the first layout settle.
            for (int i = 0; i < 600 && _disc.Status == DiscStatus.NeedsHash; i++) await Task.Delay(100);
            await Task.Delay(1200);
            Capture(Path.Combine(_captureFolder, "screenshot-home.png"));
            OpenSettings(Tab.Enhancements);
            await Task.Delay(500);
            SettingsScroll.ScrollToEnd();  // smooth motion, PGXP, CD speed and fast boot in view
            await Task.Delay(500);
            Capture(Path.Combine(_captureFolder, "screenshot-settings.png"));
            ShowTab(Tab.Controls);
            await Task.Delay(700);
            Capture(Path.Combine(_captureFolder, "screenshot-controls.png"));
            ShowHome();  // nothing is saved
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(_captureFolder, "capture-error.txt"), ex.ToString());
        }
        finally
        {
            Close();
        }
    }

    private void Capture(string path)
    {
        var size = new PixelSize((int)Math.Round(ClientSize.Width), (int)Math.Round(ClientSize.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(this);
        bitmap.Save(path);
    }
}

internal static class Win32
{
    public const int SwShowNoActivate = 4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>Flash the taskbar button until the window is next activated.</summary>
    public static void Flash(IntPtr hwnd)
    {
        var info = new FlashInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FlashInfo>(),
            Window = hwnd,
            Flags = 0x2 | 0xC,  // FLASHW_TRAY | FLASHW_TIMERNOFG
        };
        FlashWindowEx(ref info);
    }
}

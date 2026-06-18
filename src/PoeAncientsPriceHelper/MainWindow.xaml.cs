using System.Net.Http;
using System.Windows;
using MahApps.Metro.Controls;
using SharpHook.Data;

namespace PoeAncientsPriceHelper;

public partial class MainWindow : MetroWindow
{
    private AppConfig _config = new();
    private PriceRepository? _repo;
    private IconCache? _icons;
    private ScanEngine? _engine;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _loading;

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _trayBalloonShown;

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _config = ConfigStore.Load();
        PopulateFields();
        await StartupAsync();
        _ = CheckForUpdatesAsync();
    }

    private string? _updateUrl;

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (current is null) return;

            var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/pedro-quiterio/PoeAncientsPriceHelper/releases/latest");
            req.Headers.TryAddWithoutValidation("User-Agent", "PoeAncientsPriceHelper");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync();
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var tag = (string?)obj["tag_name"];
            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return;

            var cur = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
            var rem = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (rem <= cur) return;

            _updateUrl = (string?)obj["html_url"];
            Dispatcher.BeginInvoke(() =>
            {
                UpdateLink.Text = $"⬆ Доступно обновление: v{rem.Major}.{rem.Minor}.{rem.Build} — нажмите для загрузки";
                UpdateLink.Visibility = Visibility.Visible;
            });
        }
        catch { }
    }

    private void UpdateLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(_updateUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Update] не удалось открыть браузер: {ex.Message}");
        }
    }

    private void PopulateFields()
    {
        _loading = true;
        LeagueBox.ItemsSource = _config.AvailableLeagues;
        LeagueBox.SelectedItem = _config.AvailableLeagues.Contains(_config.LeagueName)
            ? _config.LeagueName
            : _config.AvailableLeagues.FirstOrDefault();
        var startStop = HotkeyBinding.Parse(_config.StartStopHotkey);
        var debug = HotkeyBinding.Parse(_config.DebugHotkey);
        var calibrate = HotkeyBinding.Parse(_config.CalibrateHotkey);
        HotkeyLabel.Text = HotkeyBinding.Display(startStop);
        DebugHotkeyLabel.Text = HotkeyBinding.Display(debug);
        CalibrateHotkeyLabel.Text = HotkeyBinding.Display(calibrate);
        App.SetStartStopKey(startStop);
        App.SetDebugKey(debug);
        App.SetCalibrateKey(calibrate);
        UpdateRegionLabel();
        _loading = false;
    }

    private void UpdateRegionLabel()
    {
        RegionLabel.Text = _config.IsCalibrated
            ? $"x={_config.RegionX} y={_config.RegionY} {_config.RegionWidth}×{_config.RegionHeight}"
            : "Не откалибровано";
    }

    private async Task StartupAsync()
    {
        StatusLabel.Text = "Загрузка цен с poe.ninja…";
        StartStopButton.IsEnabled = false;

        _repo?.Dispose();
        _icons?.Dispose();

        _repo = new PriceRepository(_http);
        _repo.PricesUpdated += OnPricesUpdated;
        _icons = new IconCache(_http);

        await Task.WhenAll(
            _repo.InitialFetchAsync(_config),
            _icons.LoadAsync());

        _repo.StartAutoRefresh(_config);

        UpdateStatusLabel();
        StartStopButton.IsEnabled = _config.IsCalibrated;
    }

    private void OnPricesUpdated() => Dispatcher.BeginInvoke(UpdateStatusLabel);

    private void UpdateStatusLabel()
    {
        if (_repo is null) return;
        string fetched = _repo.LastFetchedAt is { } t ? t.ToString("d MMM HH:mm") : "никогда";
        StatusLabel.Text = $"{_repo.ItemCount} позиций загружено  ·  обновлено {fetched}";
    }

    private void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://www.paypal.com/donate/?business=pedro.levi.magic%40gmail.com&currency_code=USD&item_name=PoeAncientsPriceHelper";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Donate] не удалось открыть браузер: {ex.Message}");
        }
    }

    internal void RunCalibration()
    {
        var rect = CalibrationOverlay.RunOnStaThread();
        if (rect is null) return;
        _config.RegionRect = rect.Value;
        ConfigStore.Save(_config);
        Dispatcher.Invoke(() =>
        {
            UpdateRegionLabel();
            StartStopButton.IsEnabled = _config.IsCalibrated;
        });
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e) => RunCalibration();

    private void StartStopButton_Click(object sender, RoutedEventArgs e) => ToggleStartStop();

    internal void ToggleStartStop()
    {
        if (_engine is null)
        {
            if (!_config.IsCalibrated || _repo is null || _icons is null) return;
            _engine = new ScanEngine(_config, _repo, _icons);
            _engine.Start();
            StartStopButton.Content = "Стоп";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkRed;
        }
        else
        {
            _engine.StopAndWait(TimeSpan.FromSeconds(2));
            _engine.Dispose();
            _engine = null;
            StartStopButton.Content = "Старт";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkGreen;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        EnsureTrayIcon();
        _trayIcon!.Visible = true;
        Hide();
        if (!_trayBalloonShown)
        {
            _trayIcon.ShowBalloonTip(3000, "Poe Ancients Price Helper",
                "Программа работает — дважды щёлкните по иконке в трее для восстановления.",
                System.Windows.Forms.ToolTipIcon.Info);
            _trayBalloonShown = true;
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;
        var exe = Environment.ProcessPath;
        var icon = exe is not null
            ? System.Drawing.Icon.ExtractAssociatedIcon(exe)
            : System.Drawing.SystemIcons.Application;
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Poe Ancients Price Helper",
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Показать", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Выход", null, (_, _) => ExitFromTray());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void ExitFromTray()
    {
        if (_trayIcon is not null) _trayIcon.Visible = false;
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _trayIcon?.Dispose();
        _engine?.StopAndWait(TimeSpan.FromSeconds(2));
        _engine?.Dispose();
        _repo?.Dispose();
        _icons?.Dispose();
        _http.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async void LeagueBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || LeagueBox.SelectedItem is not string league || league == _config.LeagueName) return;
        _config.LeagueName = league;
        ConfigStore.Save(_config);
        await StartupAsync();
    }

    private HotkeyBinding.Action _rebindAction;
    private System.Windows.Controls.Button? _rebindButton;
    private System.Windows.Controls.TextBlock? _rebindLabel;

    private void RebindButton_Click(object sender, RoutedEventArgs e) =>
        BeginRebind(HotkeyBinding.Action.StartStop, RebindButton, HotkeyLabel);

    private void RebindDebugButton_Click(object sender, RoutedEventArgs e) =>
        BeginRebind(HotkeyBinding.Action.Debug, RebindDebugButton, DebugHotkeyLabel);

    private void RebindCalibrateButton_Click(object sender, RoutedEventArgs e) =>
        BeginRebind(HotkeyBinding.Action.Calibrate, RebindCalibrateButton, CalibrateHotkeyLabel);

    private void BeginRebind(HotkeyBinding.Action action, System.Windows.Controls.Button button,
                             System.Windows.Controls.TextBlock label)
    {
        _rebindAction = action;
        _rebindButton = button;
        _rebindLabel = label;
        SetRebindButtonsEnabled(false);
        button.Content = "Нажмите клавишу… (Esc — отмена)";
        App.BeginHotkeyCapture(action, OnHotkeyCaptured);
    }

    private void OnHotkeyCaptured(App.CaptureOutcome outcome, KeyCode code)
    {
        switch (outcome)
        {
            case App.CaptureOutcome.Captured:
                switch (_rebindAction)
                {
                    case HotkeyBinding.Action.StartStop:
                        _config.StartStopHotkey = HotkeyBinding.ToStorage(code);
                        App.SetStartStopKey(code);
                        break;
                    case HotkeyBinding.Action.Debug:
                        _config.DebugHotkey = HotkeyBinding.ToStorage(code);
                        App.SetDebugKey(code);
                        break;
                    case HotkeyBinding.Action.Calibrate:
                        _config.CalibrateHotkey = HotkeyBinding.ToStorage(code);
                        App.SetCalibrateKey(code);
                        break;
                }
                ConfigStore.Save(_config);
                if (_rebindLabel is not null) _rebindLabel.Text = HotkeyBinding.Display(code);
                EndRebind();
                break;
            case App.CaptureOutcome.Reserved:
                if (_rebindButton is not null)
                    _rebindButton.Content = $"Клавиша {HotkeyBinding.Display(code)} занята — попробуйте другую";
                break;
            case App.CaptureOutcome.Cancelled:
                EndRebind();
                break;
        }
    }

    private void EndRebind()
    {
        if (_rebindButton is not null) _rebindButton.Content = "Изменить";
        _rebindButton = null;
        _rebindLabel = null;
        SetRebindButtonsEnabled(true);
    }

    private void SetRebindButtonsEnabled(bool enabled)
    {
        RebindButton.IsEnabled = enabled;
        RebindDebugButton.IsEnabled = enabled;
        RebindCalibrateButton.IsEnabled = enabled;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KuroLauncher.Models;
using KuroLauncher.Services;

namespace KuroLauncher;

public partial class MainWindow : Window
{
    private static readonly HttpClient ImgHttp = new();
    private static readonly Dictionary<string, Bitmap> _bitmapCache = new();

    private MinecraftAccount? _account;
    private CancellationTokenSource? _authCts;
    private CancellationTokenSource? _toastCts;
    private DeviceCodePrompt? _devicePrompt;

    private List<InstanceConfig> _instances = new();
    private readonly HashSet<string> _launchingIds = new();
    private readonly HashSet<string> _runningIds = new();
    private readonly Dictionary<string, TaskProgressEvent> _activeTasks = new();

    private LauncherSettings _savedSettings = new();
    private LauncherSettings _draftSettings = new();
    private bool _suppressSettingsEvents = true;

    private bool _isImportFolderMode;
    private bool _suppressCreateEvents;
    private InstanceConfig? _editingInst;
    private InstanceConfig? _detailInst;
    private string _detailTab = "general";
    private List<(string Name, string Meta, string? FullPath)> _detailCurrentItems = new();
    private bool _editFullscreen;
    private string? _editIconUrl;
    private string _instancesViewMode = "grid";

    private CurseForgeModpack? _selectedModpack;
    private readonly List<string> _consoleLines = new();

    public MainWindow()
    {
        InitializeComponent();
        InitFilters();
        LoadInitialState();
        InstDetailsModal.AddHandler(DragDrop.DragEnterEvent, InstDetails_DragOver);
        InstDetailsModal.AddHandler(DragDrop.DragOverEvent, InstDetails_DragOver);
        InstDetailsModal.AddHandler(DragDrop.DragLeaveEvent, InstDetails_DragLeave);
        InstDetailsModal.AddHandler(DragDrop.DropEvent, InstDetails_Drop);
    }

    private void InitFilters()
    {
        CfVersionCombo.ItemsSource = new[] { "Todas", "1.21.1", "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2" };
        CfVersionCombo.SelectedIndex = 0;

        CfLoaderCombo.ItemsSource = new[] { "Todos", "Forge", "Fabric", "NeoForge", "Quilt" };
        CfLoaderCombo.SelectedIndex = 0;

        CfSortCombo.ItemsSource = new[] { "Populares", "Más descargados", "Actualizados", "Destacados" };
        CfSortCombo.SelectedIndex = 0;

        CreateLoaderCombo.ItemsSource = new[] { "Vanilla", "Forge", "NeoForge", "Fabric", "Quilt" };
        CreateLoaderCombo.SelectedIndex = 1;
    }

    private void LoadInitialState()
    {
        _account = StoreService.LoadAccount();
        UpdateAccountUi();

        _savedSettings = StoreService.LoadSettings();
        _draftSettings = _savedSettings.Clone();
        _instancesViewMode = string.IsNullOrWhiteSpace(_savedSettings.InstancesViewMode) ? "grid" : _savedSettings.InstancesViewMode;
        UpdateStatusText.Text = $"Versión actual: v{UpdateService.CurrentVersion} · GitHub (KurosagiStar/KuroLauncher)";
        UpdateInstancesViewButtons();
        SyncSettingsControlsFromDraft();
        ApplyBackgroundPreview(_draftSettings);

        RefreshInstances();
        _ = SearchCurseForgeAsync();
    }

    private StreamGeometry GetIcon(string key) =>
        (StreamGeometry)Application.Current!.FindResource(key)!;

    private void ShowToast(string msg, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            var token = _toastCts.Token;

            ToastText.Text = msg;
            ToastBanner.Background = isError
                ? new SolidColorBrush(Color.Parse("#E6B91C3C"))
                : new SolidColorBrush(Color.Parse("#FF19152F"));
            ToastBanner.IsVisible = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(isError ? 5000 : 3000, token);
                    Dispatcher.UIThread.Post(() => ToastBanner.IsVisible = false);
                }
                catch (OperationCanceledException) { }
            }, token);
        });
    }

    private void Toast_Click(object? sender, PointerPressedEventArgs e) => ToastBanner.IsVisible = false;

    // Window chrome
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // Navigation
    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        ViewInstances.IsVisible = tag == "instances";
        ViewCurseForge.IsVisible = tag == "curseforge";
        ViewConsole.IsVisible = tag == "console";
        ViewSettings.IsVisible = tag == "settings";

        SetActiveClass(NavInstancesBtn, tag == "instances");
        SetActiveClass(NavCurseForgeBtn, tag == "curseforge");
        SetActiveClass(NavConsoleBtn, tag == "console");
        SetActiveClass(NavSettingsBtn, tag == "settings");

        UpdateUnsavedBarVisibility();
    }

    private static void SetActiveClass(Button b, bool active)
    {
        if (active) b.Classes.Add("active");
        else b.Classes.Remove("active");
    }

    // Account
    private void UpdateAccountUi()
    {
        if (_account == null)
        {
            AccountLabel.Text = "Iniciar sesión";
            AccountMsIcon.IsVisible = false;
            AccountIcon.IsVisible = true;
            AccountIcon.Data = GetIcon("IconUser");
            AccountIcon.Foreground = Brushes.White;
            CurrentAccountCard.IsVisible = false;
        }
        else
        {
            bool isMs = _account.Type == "microsoft";
            AccountLabel.Text = isMs ? _account.Name : $"{_account.Name} (Offline)";
            AccountMsIcon.IsVisible = isMs;
            AccountIcon.IsVisible = !isMs;
            AccountIcon.Data = GetIcon("IconUser");
            AccountIcon.Foreground = Brushes.White;
            CurrentAccountCard.IsVisible = true;
            CurrentAccountName.Text = _account.Name;
            CurrentAccountType.Text = isMs ? "Cuenta Microsoft oficial" : "Modo Offline";
        }
    }

    private void OpenAccount_Click(object? sender, RoutedEventArgs e)
    {
        AuthErrorText.IsVisible = false;
        DeviceCodePanel.IsVisible = false;
        UpdateAccountUi();
        AccountModal.IsVisible = true;
    }

    private void CloseAccount_Click(object? sender, RoutedEventArgs e)
    {
        _authCts?.Cancel();
        AccountModal.IsVisible = false;
    }

    private async void StartMsLogin_Click(object? sender, RoutedEventArgs e)
    {
        AuthErrorText.IsVisible = false;
        MsLoginBtn.IsEnabled = false;
        _authCts?.Cancel();
        _authCts = new CancellationTokenSource();
        try
        {
            _devicePrompt = await AuthService.StartDeviceCodeAsync();
            DeviceCodeText.Text = _devicePrompt.UserCode;
            DeviceCodePanel.IsVisible = true;

            var acc = await AuthService.PollDeviceCodeAsync(_devicePrompt, _authCts.Token);
            _account = acc;
            UpdateAccountUi();
            AccountModal.IsVisible = false;
            ShowToast($"Sesión iniciada como {acc.Name}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AuthErrorText.Text = ex.Message;
            AuthErrorText.IsVisible = true;
        }
        finally
        {
            DeviceCodePanel.IsVisible = false;
            MsLoginBtn.IsEnabled = true;
        }
    }

    private async void CopyDeviceCode_Click(object? sender, RoutedEventArgs e)
    {
        if (_devicePrompt != null && Clipboard != null)
        {
            await Clipboard.SetTextAsync(_devicePrompt.UserCode);
            ShowToast("Código copiado al portapapeles");
        }
    }

    private void OfflineLogin_Click(object? sender, RoutedEventArgs e)
    {
        var name = OfflineNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return;
        _account = AuthService.LoginOffline(name);
        OfflineNameBox.Text = "";
        UpdateAccountUi();
        AccountModal.IsVisible = false;
    }

    private void Logout_Click(object? sender, RoutedEventArgs e)
    {
        StoreService.SaveAccount(null);
        _account = null;
        UpdateAccountUi();
    }

    // Progress & Console events
    private void OnTaskProgress(TaskProgressEvent ev)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ev.Status is "done" or "error")
                _activeTasks.Remove(ev.TaskId);
            else
                _activeTasks[ev.TaskId] = ev;

            var first = _activeTasks.Values.FirstOrDefault(t => string.IsNullOrEmpty(t.InstanceId));
            if (first != null)
            {
                ProgressDock.IsVisible = true;
                ProgressTitle.Text = first.Title;
                ProgressMsg.Text = first.Message;
                ProgressPct.Text = $"{first.Percent}%";
                ProgressBarCtrl.Value = first.Percent;
            }
            else
            {
                ProgressDock.IsVisible = false;
            }

            RenderInstances();
        });
    }

    private void OnConsoleLog(ConsoleLogEvent ev)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _consoleLines.Add($"[{ev.Timestamp}] {ev.Message}");
            if (_consoleLines.Count > 500) _consoleLines.RemoveRange(0, _consoleLines.Count - 500);
            ConsoleText.Text = string.Join(Environment.NewLine, _consoleLines);
            ConsoleCountText.Text = $"Salida en tiempo real ({_consoleLines.Count} líneas)";
            ConsoleScroll.ScrollToEnd();
        });
    }

    private void ClearLogs_Click(object? sender, RoutedEventArgs e)
    {
        _consoleLines.Clear();
        ConsoleText.Text = "Esperando inicio de Minecraft...";
        ConsoleCountText.Text = "Salida en tiempo real de tus instancias de Minecraft.";
    }

    // Instances
    private void RefreshInstances()
    {
        _instances = StoreService.LoadInstances();
        RenderInstances();
    }

    private void InstancesViewMode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string mode) return;
        _instancesViewMode = mode;
        _savedSettings.InstancesViewMode = mode;
        _draftSettings.InstancesViewMode = mode;
        StoreService.SaveSettings(_savedSettings);
        UpdateInstancesViewButtons();
        RenderInstances();
    }

    private void UpdateInstancesViewButtons()
    {
        SetActiveClass(ViewGridBtn, _instancesViewMode == "grid");
        SetActiveClass(ViewCompactBtn, _instancesViewMode == "compact");
        SetActiveClass(ViewListBtn, _instancesViewMode == "list");
    }

    private static string GetMonogram(string name)
    {
        var m = new string(name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
        return string.IsNullOrEmpty(m) ? "MC" : m;
    }

    private Border CreateInstanceAvatar(InstanceConfig inst, int size, int cornerRadius)
    {
        var fallback = new TextBlock
        {
            Text = GetMonogram(inst.Name),
            FontWeight = FontWeight.Bold,
            FontSize = size >= 60 ? 18 : 14,
            Foreground = new SolidColorBrush(Color.Parse("#DDD6FE")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var panel = new Panel { Children = { fallback } };
        var effectiveIcon = !string.IsNullOrWhiteSpace(inst.IconUrl)
            ? inst.IconUrl
            : CurseForgeService.DetectInstanceIcon(inst.Path);

        if (!string.IsNullOrWhiteSpace(effectiveIcon))
        {
            var img = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
            panel.Children.Add(img);
            if (_bitmapCache.TryGetValue(effectiveIcon, out var cached))
            {
                img.Source = cached;
            }
            else if (effectiveIcon.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     effectiveIcon.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _ = LoadBitmapAsync(effectiveIcon, img);
            }
            else if (File.Exists(effectiveIcon))
            {
                try
                {
                    var bmp = new Bitmap(effectiveIcon);
                    _bitmapCache[effectiveIcon] = bmp;
                    img.Source = bmp;
                }
                catch { }
            }
        }

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(cornerRadius),
            ClipToBounds = true,
            Background = (IBrush)Application.Current!.FindResource("AvatarGradient")!,
            BorderBrush = new SolidColorBrush(Color.Parse("#1CFFFFFF")),
            BorderThickness = new Thickness(1),
            Child = panel
        };
    }

    private void RenderInstances()
    {
        InstancesWrap.Children.Clear();
        InstancesList.Children.Clear();
        bool isList = _instancesViewMode == "list";
        bool isCompact = _instancesViewMode == "compact";
        var pendingImports = _activeTasks.Values
            .Where(t => !t.TaskId.StartsWith("sp-", StringComparison.Ordinal) &&
                        (string.IsNullOrEmpty(t.InstanceId) || !_instances.Any(i => i.Id == t.InstanceId)))
            .ToList();

        EmptyInstancesPanel.IsVisible = _instances.Count == 0 && pendingImports.Count == 0;
        InstancesWrap.IsVisible = !isList;
        InstancesList.IsVisible = isList;

        foreach (var task in pendingImports)
        {
            var skAvatar = new Border { Width = isList ? 48 : (isCompact ? 68 : 56), Height = isList ? 48 : (isCompact ? 68 : 56), CornerRadius = new CornerRadius(isList ? 11 : (isCompact ? 14 : 12)), Background = new SolidColorBrush(Color.Parse("#1CFFFFFF")) };
            var skTitle = new TextBlock { Text = task.Title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            var skChip1 = new Border { Width = 64, Height = 20, CornerRadius = new CornerRadius(99), Background = new SolidColorBrush(Color.Parse("#14FFFFFF")) };
            var skChip2 = new Border { Width = 82, Height = 20, CornerRadius = new CornerRadius(99), Background = new SolidColorBrush(Color.Parse("#14FFFFFF")) };
            var skChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { skChip1, skChip2 } };

            var skPb = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = task.Percent,
                MinWidth = 0,
                Height = isCompact ? 32 : 36,
                CornerRadius = new CornerRadius(99),
                Foreground = new SolidColorBrush(Color.Parse("#A78BFA")),
                Background = new SolidColorBrush(Color.Parse("#FF131024")),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var skTxt = new TextBlock
            {
                Text = $"{task.Message} ({task.Percent}%)",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(10, 0)
            };
            var skProgGrid = new Grid { MinWidth = 0, Height = isCompact ? 32 : 36, ClipToBounds = true, Children = { skPb, skTxt } };

            if (isList)
            {
                var info = new StackPanel { Spacing = 6, Margin = new Thickness(14, 0), VerticalAlignment = VerticalAlignment.Center, Children = { skTitle, skChips } };
                skProgGrid.Width = 180;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                Grid.SetColumn(skAvatar, 0); Grid.SetColumn(info, 1); Grid.SetColumn(skProgGrid, 2);
                row.Children.Add(skAvatar); row.Children.Add(info); row.Children.Add(skProgGrid);
                var rowCard = new Border { Padding = new Thickness(14, 11), Child = row };
                rowCard.Classes.Add("card");
                InstancesList.Children.Add(rowCard);
            }
            else if (isCompact)
            {
                skAvatar.HorizontalAlignment = HorizontalAlignment.Center;
                skTitle.HorizontalAlignment = HorizontalAlignment.Center;
                skChips.HorizontalAlignment = HorizontalAlignment.Center;
                var card = new Border
                {
                    Width = 224,
                    Margin = new Thickness(0, 0, 12, 12),
                    Padding = new Thickness(14),
                    Child = new StackPanel { Spacing = 9, Children = { skAvatar, skTitle, skChips, skProgGrid } }
                };
                card.Classes.Add("card");
                InstancesWrap.Children.Add(card);
            }
            else
            {
                var info = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { skTitle, skChips } };
                var hg = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                Grid.SetColumn(skAvatar, 0); Grid.SetColumn(info, 1);
                hg.Children.Add(skAvatar); hg.Children.Add(info);
                var card = new Border
                {
                    Width = 365,
                    Margin = new Thickness(0, 0, 14, 14),
                    Padding = new Thickness(16),
                    Child = new StackPanel { Spacing = 12, Children = { hg, skProgGrid } }
                };
                card.Classes.Add("card");
                InstancesWrap.Children.Add(card);
            }
        }

        foreach (var inst in _instances)
        {
            bool isRunning = _runningIds.Contains(inst.Id);
            bool isLaunching = _launchingIds.Contains(inst.Id);
            var activeTask = _activeTasks.Values.FirstOrDefault(t => t.InstanceId == inst.Id);
            var loaderTxt = string.IsNullOrWhiteSpace(inst.LoaderVersion) ? inst.Loader : $"{inst.Loader} {inst.LoaderVersion}";

            // Shared Play / Progress-Pill / Force-Close control (constant height so cards never resize)
            Control playControl;
            int btnHeight = isCompact ? 32 : 36;
            if (isRunning)
            {
                var closeBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = btnHeight,
                    Padding = isCompact ? new Thickness(10, 0) : new Thickness(14, 0)
                };
                closeBtn.Classes.Add("danger");
                closeBtn.Content = new TextBlock
                {
                    Text = "Cerrar",
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var instCapture = inst;
                closeBtn.Click += (_, _) =>
                {
                    if (LauncherService.KillInstance(instCapture.Id))
                        ShowToast($"Cierre forzado de '{instCapture.Name}'");
                };
                playControl = closeBtn;
            }
            else if (activeTask != null || isLaunching)
            {
                var pct = activeTask?.Percent ?? 10;
                var label = activeTask != null ? $"{activeTask.Message} ({pct}%)" : "Iniciando...";
                var pb = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = pct,
                    MinWidth = 0,
                    Height = btnHeight,
                    CornerRadius = new CornerRadius(99),
                    Foreground = new SolidColorBrush(Color.Parse("#A78BFA")),
                    Background = new SolidColorBrush(Color.Parse("#FF131024")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#3F2E6B")),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var txt = new TextBlock
                {
                    Text = label,
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(10, 0)
                };
                playControl = new Grid
                {
                    MinWidth = 0,
                    Height = btnHeight,
                    ClipToBounds = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Children = { pb, txt }
                };
            }
            else
            {
                var playBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = btnHeight,
                    Padding = isCompact ? new Thickness(10, 0) : new Thickness(14, 0)
                };
                playBtn.Classes.Add("primary");
                playBtn.Content = new TextBlock
                {
                    Text = "Jugar",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var instCapture = inst;
                playBtn.Click += async (_, _) => await LaunchInstanceAsync(instCapture);
                playControl = playBtn;
            }

            // Shared Options MenuFlyout
            var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            flyout.Items.Add(MakeMenuItem("Ver detalles y mods", "IconLayers", () => OpenInstDetails(inst)));
            flyout.Items.Add(MakeMenuItem("Ajustes", "IconSettings", () => OpenInstSettings(inst)));
            if (inst.CurseforgeModId.HasValue)
                flyout.Items.Add(MakeMenuItem("Actualizar modpack", "IconRotate", () => _ = UpdateModpackAsync(inst)));
            flyout.Items.Add(MakeMenuItem("Abrir carpeta", "IconFolder", () => StoreService.OpenFolder(inst.Path)));
            flyout.Items.Add(MakeMenuItem("Duplicar instancia", "IconCopy", () => _ = DuplicateInstanceAsync(inst)));
            flyout.Items.Add(new Separator());
            flyout.Items.Add(MakeMenuItem("Eliminar", "IconTrash", () => DeleteInstance(inst), "#FDA4AF"));

            var optsBtn = new Button
            {
                Width = isCompact ? 34 : 38,
                Height = isCompact ? 32 : 36,
                Padding = new Thickness(0),
                Content = new PathIcon { Data = GetIcon("IconMore"), Width = 14, Height = 14 },
                Flyout = flyout
            };
            optsBtn.Classes.Add("glass-btn");

            Border? runBadge = null;
            if (isRunning)
            {
                runBadge = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#3010B981")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#7034D399")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(99),
                    Padding = new Thickness(8, 2),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5,
                        Children =
                        {
                            new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(99), Background = new SolidColorBrush(Color.Parse("#34D399")), VerticalAlignment = VerticalAlignment.Center },
                            new TextBlock { Text = "En ejecución", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#6EE7B7")) }
                        }
                    }
                };
            }

            if (isList)
            {
                var avatar = CreateInstanceAvatar(inst, 48, 11);
                var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                titleRow.Children.Add(new TextBlock
                {
                    Text = inst.Name,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (runBadge != null) titleRow.Children.Add(runBadge);

                var subRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        MakeChip($"MC {inst.MinecraftVersion}", "#C4B5FD"),
                        MakeChip(loaderTxt, "#A78BFA"),
                        new TextBlock
                        {
                            Text = $"{inst.ModCount ?? 0} mods · {inst.Source}",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.Parse("#9BA0B8")),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(4, 0, 0, 0)
                        }
                    }
                };

                var infoStack = new StackPanel { Spacing = 5, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Children = { titleRow, subRow } };

                playControl.Width = 148;
                var rightActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Height = btnHeight, VerticalAlignment = VerticalAlignment.Center, Children = { playControl, optsBtn } };

                var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                Grid.SetColumn(avatar, 0);
                Grid.SetColumn(infoStack, 1);
                Grid.SetColumn(rightActions, 2);
                rowGrid.Children.Add(avatar);
                rowGrid.Children.Add(infoStack);
                rowGrid.Children.Add(rightActions);

                var rowCard = new Border { Padding = new Thickness(14, 11), Cursor = new Cursor(StandardCursorType.Hand), Child = rowGrid };
                rowCard.Classes.Add("card");
                rowCard.Classes.Add("interactive");
                if (isRunning) rowCard.BorderBrush = new SolidColorBrush(Color.Parse("#8034D399"));
                var instCap = inst;
                rowCard.PointerPressed += (_, e) => { if (e.GetCurrentPoint(rowCard).Properties.IsLeftButtonPressed) OpenInstDetails(instCap); };
                InstancesList.Children.Add(rowCard);
                continue;
            }

            if (isCompact)
            {
                var avatar = CreateInstanceAvatar(inst, 68, 14);
                avatar.HorizontalAlignment = HorizontalAlignment.Center;

                var nameTxt = new TextBlock
                {
                    Text = inst.Name,
                    FontSize = 13.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var chips = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        MakeChip($"MC {inst.MinecraftVersion}", "#C4B5FD"),
                        MakeChip(inst.Loader, "#A78BFA")
                    }
                };

                var actionsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = btnHeight };
                Grid.SetColumn(playControl, 0);
                optsBtn.Margin = new Thickness(6, 0, 0, 0);
                Grid.SetColumn(optsBtn, 1);
                actionsGrid.Children.Add(playControl);
                actionsGrid.Children.Add(optsBtn);

                var stack = new StackPanel { Spacing = 9, Children = { avatar, nameTxt, chips, actionsGrid } };

                var card = new Border
                {
                    Width = 224,
                    Margin = new Thickness(0, 0, 12, 12),
                    Padding = new Thickness(14),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = stack
                };
                card.Classes.Add("card");
                card.Classes.Add("interactive");
                if (isRunning) card.BorderBrush = new SolidColorBrush(Color.Parse("#8034D399"));
                var instCap = inst;
                card.PointerPressed += (_, e) => { if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) OpenInstDetails(instCap); };
                InstancesWrap.Children.Add(card);
                continue;
            }

            // Default: Grid (Tarjetas) with square rounded avatar
            {
                var avatar = CreateInstanceAvatar(inst, 56, 12);

                var titleGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 22 };
                var nameTxt = new TextBlock
                {
                    Text = inst.Name,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameTxt, 0);
                titleGrid.Children.Add(nameTxt);
                if (runBadge != null)
                {
                    Grid.SetColumn(runBadge, 1);
                    titleGrid.Children.Add(runBadge);
                }

                var badges = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        MakeChip($"MC {inst.MinecraftVersion}", "#A78BFA"),
                        MakeChip(loaderTxt, "#7C3AED")
                    }
                };

                var meta = new TextBlock
                {
                    Text = $"{inst.ModCount ?? 0} mods · origen: {inst.Source}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#9BA0B8"))
                };

                var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                Grid.SetColumn(avatar, 0);
                var infoStack = new StackPanel { Spacing = 5, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { titleGrid, badges, meta } };
                Grid.SetColumn(infoStack, 1);
                headerGrid.Children.Add(avatar);
                headerGrid.Children.Add(infoStack);

                var cardStack = new StackPanel { Spacing = 12, Children = { headerGrid } };

                var actionsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = btnHeight };
                Grid.SetColumn(playControl, 0);
                optsBtn.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(optsBtn, 1);
                actionsGrid.Children.Add(playControl);
                actionsGrid.Children.Add(optsBtn);
                cardStack.Children.Add(actionsGrid);

                var card = new Border
                {
                    Width = 365,
                    Margin = new Thickness(0, 0, 14, 14),
                    Padding = new Thickness(16),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = cardStack
                };
                card.Classes.Add("card");
                card.Classes.Add("interactive");
                if (isRunning) card.BorderBrush = new SolidColorBrush(Color.Parse("#8034D399"));
                var instCap = inst;
                card.PointerPressed += (_, e) => { if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) OpenInstDetails(instCap); };
                InstancesWrap.Children.Add(card);
            }
        }
    }

    private MenuItem MakeMenuItem(string header, string iconKey, Action onClick, string? fgHex = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new PathIcon
            {
                Data = GetIcon(iconKey),
                Width = 14,
                Height = 14,
                Foreground = fgHex != null ? new SolidColorBrush(Color.Parse(fgHex)) : new SolidColorBrush(Color.Parse("#6EE7B7"))
            }
        };
        if (fgHex != null) item.Foreground = new SolidColorBrush(Color.Parse(fgHex));
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Border MakeChip(string text, string dotHex, string fgHex = "#F5F3FF") => new()
    {
        Background = new SolidColorBrush(Color.Parse("#FF110E22")),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(99),
        Padding = new Thickness(9, 3),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(99),
                    Background = new SolidColorBrush(Color.Parse(dotHex)),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse(fgHex)),
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        }
    };

    private async Task LaunchInstanceAsync(InstanceConfig inst)
    {
        if (_account == null)
        {
            OpenAccount_Click(null, new RoutedEventArgs());
            return;
        }
        _launchingIds.Add(inst.Id);
        RenderInstances();
        try
        {
            await LauncherService.LaunchInstanceAsync(
                inst,
                OnTaskProgress,
                OnConsoleLog,
                (id, running) => Dispatcher.UIThread.Post(() =>
                {
                    if (running) _runningIds.Add(id);
                    else _runningIds.Remove(id);
                    RenderInstances();
                }));
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
        finally
        {
            _launchingIds.Remove(inst.Id);
            RefreshInstances();
        }
    }

    private async Task DuplicateInstanceAsync(InstanceConfig orig)
    {
        try
        {
            ShowToast($"Duplicando '{orig.Name}'...");
            await Task.Run(() =>
            {
                var newId = Guid.NewGuid().ToString();
                var newName = $"{orig.Name} (Copia)";
                var dest = Path.Combine(_savedSettings.InstancesDir, $"{StoreService.SanitizeName(newName)}-{newId[..6]}");
                if (Directory.Exists(orig.Path)) StoreService.CopyDirectoryRecursive(orig.Path, dest);
                else Directory.CreateDirectory(dest);

                StoreService.UpsertInstance(new InstanceConfig
                {
                    Id = newId,
                    Name = newName,
                    Path = dest,
                    MinecraftVersion = orig.MinecraftVersion,
                    Loader = orig.Loader,
                    LoaderVersion = orig.LoaderVersion,
                    IconUrl = orig.IconUrl,
                    Source = orig.Source,
                    CreatedAt = DateTime.UtcNow.ToString("O"),
                    LinkedExternal = false,
                    MinRamMb = orig.MinRamMb,
                    MaxRamMb = orig.MaxRamMb,
                    JavaPath = orig.JavaPath,
                    JvmArgs = orig.JvmArgs,
                    WindowWidth = orig.WindowWidth,
                    WindowHeight = orig.WindowHeight,
                    Fullscreen = orig.Fullscreen
                });
            });
            RefreshInstances();
            ShowToast("Instancia duplicada correctamente");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
    }

    private void DeleteInstance(InstanceConfig inst)
    {
        var removed = StoreService.RemoveInstance(inst.Id);
        if (removed != null && removed.LinkedExternal != true && Directory.Exists(removed.Path))
        {
            try { Directory.Delete(removed.Path, true); } catch { }
        }
        RefreshInstances();
    }

    // Create / Import Modals
    private async void ImportZip_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleccionar Modpack o Instancia (.zip / .rar)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Archivo comprimido") { Patterns = ["*.zip", "*.rar"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        var tempTaskId = $"import-{Guid.NewGuid().ToString()[..6]}";
        var fileName = Path.GetFileNameWithoutExtension(path);
        OnTaskProgress(new(tempTaskId, fileName, null, "running", 5, "Analizando archivo..."));
        try
        {
            var inst = await Task.Run(() => CurseForgeService.InstallFromZipAsync(path, null, "zip", null, null, null, ev =>
            {
                Dispatcher.UIThread.Post(() => _activeTasks.Remove(tempTaskId));
                OnTaskProgress(ev);
            }));
            OnTaskProgress(new(tempTaskId, fileName, null, "done", 100, "Listo"));
            RefreshInstances();
            ShowToast($"Instancia '{inst.Name}' importada");
        }
        catch (Exception ex)
        {
            OnTaskProgress(new(tempTaskId, fileName, null, "error", 0, ex.Message));
            ShowToast(ex.Message, true);
        }
    }

    private async void OpenCreateInstance_Click(object? sender, RoutedEventArgs e)
    {
        _isImportFolderMode = false;
        CreateModalIcon.Data = GetIcon("IconPlus");
        CreateModalTitle.Text = "Nueva Instancia";
        CreateModalSubtitle.Text = "Configura versión de Minecraft y mod loader.";
        CreateSubmitLabel.Text = "Crear instancia";
        ImportFolderRow.IsVisible = false;
        ImportCopyFilesCheck.IsVisible = false;
        CreateNameBox.Text = "";
        CreateInstanceModal.IsVisible = true;
        await LoadCreateVersionsAsync();
    }

    private async void OpenImportFolder_Click(object? sender, RoutedEventArgs e)
    {
        _isImportFolderMode = true;
        CreateModalIcon.Data = GetIcon("IconFolder");
        CreateModalTitle.Text = "Importar Carpeta de Instancia";
        CreateModalSubtitle.Text = "Importa o enlaza una carpeta existente de Minecraft.";
        CreateSubmitLabel.Text = "Importar";
        ImportFolderRow.IsVisible = true;
        ImportCopyFilesCheck.IsVisible = true;
        ImportFolderPathBox.Text = "";
        CreateNameBox.Text = "";
        CreateInstanceModal.IsVisible = true;
        await LoadCreateVersionsAsync();
    }

    private void CloseCreateModal_Click(object? sender, RoutedEventArgs e) => CreateInstanceModal.IsVisible = false;

    private async void PickImportFolder_Click(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Seleccionar carpeta de instancia" });
        var path = dirs.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        ImportFolderPathBox.Text = path;

        var detected = CurseForgeService.DetectFolderInstanceMetadata(path);
        if (string.IsNullOrWhiteSpace(CreateNameBox.Text))
            CreateNameBox.Text = !string.IsNullOrWhiteSpace(detected.Name)
                ? detected.Name
                : Path.GetFileName(path.TrimEnd('\\', '/'));

        if (!string.IsNullOrWhiteSpace(detected.MinecraftVersion) || !string.IsNullOrWhiteSpace(detected.Loader))
        {
            _suppressCreateEvents = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(detected.MinecraftVersion))
                {
                    var currentVersions = (CreateMcVersionCombo.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>();
                    if (!currentVersions.Contains(detected.MinecraftVersion))
                    {
                        currentVersions.Insert(0, detected.MinecraftVersion);
                        CreateMcVersionCombo.ItemsSource = currentVersions;
                    }
                    CreateMcVersionCombo.SelectedItem = detected.MinecraftVersion;
                }

                if (!string.IsNullOrWhiteSpace(detected.Loader))
                {
                    var loaderLabel = detected.Loader.ToLowerInvariant() switch
                    {
                        "forge" => "Forge",
                        "neoforge" => "NeoForge",
                        "fabric" => "Fabric",
                        "quilt" => "Quilt",
                        _ => "Vanilla"
                    };
                    CreateLoaderCombo.SelectedItem = loaderLabel;
                }
            }
            finally
            {
                _suppressCreateEvents = false;
            }

            await LoadLoaderVersionsForCreateAsync(detected.LoaderVersion);
            var summary = string.IsNullOrWhiteSpace(detected.LoaderVersion)
                ? $"MC {detected.MinecraftVersion ?? "?"} · {detected.Loader ?? "vanilla"}"
                : $"MC {detected.MinecraftVersion ?? "?"} · {detected.Loader} {detected.LoaderVersion}";
            ShowToast($"Detectado automáticamente: {summary}");
        }
    }

    private async Task LoadCreateVersionsAsync()
    {
        try
        {
            _suppressCreateEvents = true;
            var versions = await CurseForgeService.GetMcVersionsAsync(CreateSnapshotsCheck.IsChecked == true);
            CreateMcVersionCombo.ItemsSource = versions;
            CreateMcVersionCombo.SelectedItem = versions.Contains("1.20.1") ? "1.20.1" : versions.FirstOrDefault();
            _suppressCreateEvents = false;
            await LoadLoaderVersionsForCreateAsync();
        }
        catch { _suppressCreateEvents = false; }
    }

    private async void CreateSnapshots_Changed(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCreateEvents) return;
        await LoadCreateVersionsAsync();
    }
    private async void CreateMcOrLoader_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCreateEvents) return;
        await LoadLoaderVersionsForCreateAsync();
    }

    private async Task LoadLoaderVersionsForCreateAsync(string? preferredVersion = null)
    {
        var mc = CreateMcVersionCombo.SelectedItem as string ?? "1.20.1";
        var loader = (CreateLoaderCombo.SelectedItem as string ?? "Vanilla").ToLowerInvariant();
        CreateLoaderVerPanel.IsVisible = loader != "vanilla";
        if (loader == "vanilla") return;
        try
        {
            var list = await CurseForgeService.GetLoaderVersionsAsync(mc, loader);
            if (!string.IsNullOrWhiteSpace(preferredVersion) && !list.Contains(preferredVersion))
                list.Insert(0, preferredVersion);
            CreateLoaderVerCombo.ItemsSource = list;
            if (!string.IsNullOrWhiteSpace(preferredVersion))
                CreateLoaderVerCombo.SelectedItem = preferredVersion;
            else
                CreateLoaderVerCombo.SelectedIndex = list.Count > 0 ? 0 : -1;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(preferredVersion))
            {
                CreateLoaderVerCombo.ItemsSource = new[] { preferredVersion };
                CreateLoaderVerCombo.SelectedIndex = 0;
            }
            else
            {
                CreateLoaderVerCombo.ItemsSource = Array.Empty<string>();
            }
        }
    }

    private void SubmitCreateModal_Click(object? sender, RoutedEventArgs e)
    {
        var name = CreateNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return;
        var mc = CreateMcVersionCombo.SelectedItem as string ?? "1.20.1";
        var loader = (CreateLoaderCombo.SelectedItem as string ?? "Vanilla").ToLowerInvariant();
        var loaderVer = loader == "vanilla" ? null : CreateLoaderVerCombo.SelectedItem as string;
        var id = Guid.NewGuid().ToString();

        string targetPath;
        if (_isImportFolderMode)
        {
            var src = ImportFolderPathBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(src)) return;
            if (ImportCopyFilesCheck.IsChecked == true)
            {
                targetPath = Path.Combine(_savedSettings.InstancesDir, $"{StoreService.SanitizeName(name)}-{id[..6]}");
                StoreService.CopyDirectoryRecursive(src, targetPath);
            }
            else targetPath = src;
        }
        else
        {
            targetPath = Path.Combine(_savedSettings.InstancesDir, $"{StoreService.SanitizeName(name)}-{id[..6]}");
            Directory.CreateDirectory(Path.Combine(targetPath, "mods"));
        }

        StoreService.UpsertInstance(new InstanceConfig
        {
            Id = id,
            Name = name,
            Path = targetPath,
            MinecraftVersion = mc,
            Loader = loader,
            LoaderVersion = loaderVer,
            IconUrl = _isImportFolderMode ? CurseForgeService.DetectInstanceIcon(targetPath) : null,
            Source = _isImportFolderMode ? "folder" : "manual",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            LinkedExternal = _isImportFolderMode && ImportCopyFilesCheck.IsChecked != true
        });

        CreateInstanceModal.IsVisible = false;
        RefreshInstances();
    }

    // Per-Instance Settings
    private void OpenInstSettings(InstanceConfig inst)
    {
        _editingInst = inst;
        _editIconUrl = !string.IsNullOrWhiteSpace(inst.IconUrl)
            ? inst.IconUrl
            : CurseForgeService.DetectInstanceIcon(inst.Path);
        EditInstSubtitle.Text = $"MC {inst.MinecraftVersion} · {inst.Loader} {inst.LoaderVersion}".Trim();
        EditInstNameBox.Text = inst.Name;
        UpdateEditIconPreview(inst.Name, _editIconUrl);
        EditInstPathText.Text = inst.Path;
        _editFullscreen = inst.Fullscreen == true;
        UpdateEditScreenModeUi();
        EditWidthBox.Text = inst.WindowWidth?.ToString() ?? "";
        EditHeightBox.Text = inst.WindowHeight?.ToString() ?? "";

        bool hasCustomRam = inst.MaxRamMb.HasValue || inst.MinRamMb.HasValue;
        EditCustomRamCheck.IsChecked = hasCustomRam;
        EditRamSlidersPanel.IsVisible = hasCustomRam;
        EditMaxRamSlider.Value = inst.MaxRamMb ?? 6144;
        EditMinRamSlider.Value = inst.MinRamMb ?? 2048;
        EditMaxRamLabel.Text = $"{(int)EditMaxRamSlider.Value} MB";
        EditMinRamLabel.Text = $"{(int)EditMinRamSlider.Value} MB";

        EditJavaBox.Text = inst.JavaPath ?? "";
        EditJvmArgsBox.Text = inst.JvmArgs ?? "";
        InstSettingsModal.IsVisible = true;
    }

    private void CloseInstSettings_Click(object? sender, RoutedEventArgs e) => InstSettingsModal.IsVisible = false;

    // Instance Details Modal
    private void OpenInstDetails(InstanceConfig inst)
    {
        _detailInst = inst;
        _detailTab = "general";
        DetailInstName.Text = inst.Name;
        DetailInstMcChip.Text = $"MC {inst.MinecraftVersion}";
        DetailInstLoaderChip.Text = string.IsNullOrWhiteSpace(inst.LoaderVersion) ? inst.Loader : $"{inst.Loader} {inst.LoaderVersion}";
        DetailInstSourceChip.Text = $"origen: {inst.Source}";
        DetailUpdateBtn.IsVisible = inst.CurseforgeModId.HasValue;

        DetailInstIconFallback.Text = GetMonogram(inst.Name);
        DetailInstIconPreview.Source = null;
        var effectiveIcon = !string.IsNullOrWhiteSpace(inst.IconUrl)
            ? inst.IconUrl
            : CurseForgeService.DetectInstanceIcon(inst.Path);
        if (!string.IsNullOrWhiteSpace(effectiveIcon))
        {
            if (_bitmapCache.TryGetValue(effectiveIcon, out var cached)) DetailInstIconPreview.Source = cached;
            else if (effectiveIcon.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _ = LoadBitmapAsync(effectiveIcon, DetailInstIconPreview);
            else if (File.Exists(effectiveIcon)) { try { var b = new Bitmap(effectiveIcon); _bitmapCache[effectiveIcon] = b; DetailInstIconPreview.Source = b; } catch { } }
        }

        DetailFilterBox.Text = "";
        RenderDetailTab();
        InstDetailsModal.IsVisible = true;
    }

    private void CloseInstDetails_Click(object? sender, RoutedEventArgs e) => InstDetailsModal.IsVisible = false;

    private void DetailOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_detailInst != null) StoreService.OpenFolder(_detailInst.Path);
    }

    private void DetailOpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (_detailInst == null) return;
        var inst = _detailInst;
        InstDetailsModal.IsVisible = false;
        OpenInstSettings(inst);
    }

    private void DetailUpdateModpack_Click(object? sender, RoutedEventArgs e)
    {
        if (_detailInst == null) return;
        var inst = _detailInst;
        InstDetailsModal.IsVisible = false;
        _ = UpdateModpackAsync(inst);
    }

    private async Task UpdateModpackAsync(InstanceConfig inst)
    {
        try
        {
            ShowToast($"Buscando actualización para '{inst.Name}'...");
            var latest = await CurseForgeService.CheckModpackUpdateAsync(inst);
            if (latest == null)
            {
                ShowToast($"'{inst.Name}' ya está en la última versión.");
                return;
            }

            ShowToast($"Actualizando a {latest.DisplayName}...");
            await CurseForgeService.UpdateClientModpackAsync(inst, latest, OnTaskProgress);
            RefreshInstances();
            ShowToast($"'{inst.Name}' actualizado a {latest.DisplayName}");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
    }

    private void DetailTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tab) return;
        _detailTab = tab;
        DetailFilterBox.Text = "";
        RenderDetailTab();
    }

    private void DetailFilterBox_Changed(object? sender, TextChangedEventArgs e) => RenderDetailItemsFiltered();

    private void RenderDetailTab()
    {
        if (_detailInst == null) return;
        SetActiveClass(DetailTabGeneral, _detailTab == "general");
        SetActiveClass(DetailTabMods, _detailTab == "mods");
        SetActiveClass(DetailTabResourcePacks, _detailTab == "resourcepacks");
        SetActiveClass(DetailTabShaders, _detailTab == "shaders");
        SetActiveClass(DetailTabDatapacks, _detailTab == "datapacks");

        var mods = ScanFolderItems(Path.Combine(_detailInst.Path, "mods"), ".jar", ".disabled");
        var resPacks = ScanFolderOrZipItems(Path.Combine(_detailInst.Path, "resourcepacks"));
        var shaders = ScanFolderOrZipItems(Path.Combine(_detailInst.Path, "shaderpacks"));
        var datapacks = ScanDatapacks(_detailInst.Path);

        DetailTabMods.Content = $"Mods ({mods.Count})";
        DetailTabResourcePacks.Content = $"Resource Packs ({resPacks.Count})";
        DetailTabShaders.Content = $"Shaders ({shaders.Count})";
        DetailTabDatapacks.Content = $"Datapacks ({datapacks.Count})";

        bool isGen = _detailTab == "general";
        bool isMods = _detailTab == "mods";
        DetailGeneralPanel.IsVisible = isGen;
        DetailItemsPanel.IsVisible = !isGen;
        DetailFilterBox.IsVisible = !isGen;
        DetailModDropZone.IsVisible = isMods;
        DetailCfSyncBanner.IsVisible = isMods && _detailInst.CurseforgeModId.HasValue;

        if (isGen)
        {
            DetailGeneralPanel.Children.Clear();
            var loaderFull = string.IsNullOrWhiteSpace(_detailInst.LoaderVersion) ? _detailInst.Loader : $"{_detailInst.Loader} {_detailInst.LoaderVersion}";
            var ramText = (_detailInst.MaxRamMb.HasValue || _detailInst.MinRamMb.HasValue)
                ? $"{_detailInst.MinRamMb ?? _savedSettings.MinRamMb} MB - {_detailInst.MaxRamMb ?? _savedSettings.MaxRamMb} MB (personalizada)"
                : $"{_savedSettings.MinRamMb} MB - {_savedSettings.MaxRamMb} MB (global)";
            var screenText = _detailInst.Fullscreen == true
                ? "Pantalla completa"
                : $"{_detailInst.WindowWidth ?? 1280}×{_detailInst.WindowHeight ?? 720} (Ventana)";

            DetailGeneralPanel.Children.Add(MakeDetailRow("Minecraft", _detailInst.MinecraftVersion));
            DetailGeneralPanel.Children.Add(MakeDetailRow("Mod Loader", loaderFull));
            DetailGeneralPanel.Children.Add(MakeDetailRow("Contenido", $"{mods.Count} mods · {resPacks.Count} resource packs · {shaders.Count} shaders · {datapacks.Count} datapacks"));
            DetailGeneralPanel.Children.Add(MakeDetailRow("Memoria RAM", ramText));
            DetailGeneralPanel.Children.Add(MakeDetailRow("Pantalla", screenText));
            DetailGeneralPanel.Children.Add(MakeDetailRow("Java", string.IsNullOrWhiteSpace(_detailInst.JavaPath) ? "Automático (Adoptium / Sistema)" : _detailInst.JavaPath));
            DetailGeneralPanel.Children.Add(MakeDetailRow("Carpeta", _detailInst.Path));
            return;
        }

        _detailCurrentItems = _detailTab switch
        {
            "mods" => mods.Select(x => (x.Name, x.Meta, (string?)Path.Combine(_detailInst.Path, "mods", x.Name))).ToList(),
            "resourcepacks" => resPacks.Select(x => (x.Name, x.Meta, (string?)null)).ToList(),
            "shaders" => shaders.Select(x => (x.Name, x.Meta, (string?)null)).ToList(),
            "datapacks" => datapacks.Select(x => (x.Name, x.Meta, (string?)null)).ToList(),
            _ => new()
        };
        RenderDetailItemsFiltered();
    }

    private void RenderDetailItemsFiltered()
    {
        DetailItemsPanel.Children.Clear();
        var q = DetailFilterBox.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(q)
            ? _detailCurrentItems
            : _detailCurrentItems.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Meta.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
        {
            DetailItemsPanel.Children.Add(new TextBlock
            {
                Text = _detailTab == "mods"
                    ? "No hay mods. Usa 'Añadir mod (.jar)' o arrastra archivos .jar aquí."
                    : "No se encontraron elementos en esta categoría.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9D96B8")),
                Margin = new Thickness(4, 16)
            });
            return;
        }

        foreach (var item in filtered)
        {
            if (_detailTab == "mods" && item.FullPath != null)
                DetailItemsPanel.Children.Add(MakeModManageRow(item.Name, item.Meta, item.FullPath));
            else
                DetailItemsPanel.Children.Add(MakeDetailRow(item.Name, item.Meta));
        }
    }

    private Border MakeModManageRow(string name, string meta, string fullPath)
    {
        bool isDisabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var displayName = isDisabled ? name[..^".disabled".Length] : name;

        var info = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = displayName,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = isDisabled ? new SolidColorBrush(Color.Parse("#9D96B8")) : Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = meta,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.Parse("#9D96B8"))
                }
            }
        };

        var track = new Border
        {
            Width = 36,
            Height = 14,
            CornerRadius = new CornerRadius(99),
            VerticalAlignment = VerticalAlignment.Center,
            Background = isDisabled
                ? new SolidColorBrush(Color.Parse("#3B3552"))
                : new SolidColorBrush(Color.Parse("#9D6BFF"))
        };
        var thumb = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(99),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = isDisabled ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Background = isDisabled
                ? new SolidColorBrush(Color.Parse("#9D96B8"))
                : new SolidColorBrush(Color.Parse("#6D28D9")),
            BoxShadow = BoxShadows.Parse("0 2 6 0 #66000000")
        };
        var toggleVisual = new Grid { Width = 38, Height = 22, Children = { track, thumb } };

        var toggleBtn = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = toggleVisual
        };
        ToolTip.SetTip(toggleBtn, isDisabled ? "Activar mod" : "Desactivar mod");
        toggleBtn.Click += (_, _) => ToggleModFile(fullPath);

        var delBtn = new Button
        {
            Padding = new Thickness(8, 5),
            Content = new PathIcon { Data = GetIcon("IconTrash"), Width = 12, Height = 12 }
        };
        delBtn.Classes.Add("danger");
        ToolTip.SetTip(delBtn, "Eliminar mod");
        delBtn.Click += (_, _) => DeleteModFile(fullPath, displayName);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { toggleBtn, delBtn } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(info, 0);
        actions.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(info);
        grid.Children.Add(actions);

        var row = new Border
        {
            Padding = new Thickness(12, 8),
            Opacity = isDisabled ? 0.72 : 1.0,
            Child = grid
        };
        row.Classes.Add("detail-row");
        return row;
    }

    private void ToggleModFile(string fullPath)
    {
        if (_detailInst == null || !File.Exists(fullPath)) return;
        try
        {
            bool isDisabled = fullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var target = isDisabled ? fullPath[..^".disabled".Length] : fullPath + ".disabled";
            File.Move(fullPath, target, true);
            if (_detailInst.CurseforgeModId.HasValue)
                ShowToast("Modpack sincronizado con CurseForge: puedes restaurar los mods originales cuando quieras.");
            RenderDetailTab();
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
    }

    private void DeleteModFile(string fullPath, string displayName)
    {
        if (_detailInst == null || !File.Exists(fullPath)) return;
        try
        {
            File.Delete(fullPath);
            ShowToast(_detailInst.CurseforgeModId.HasValue
                ? $"'{displayName}' eliminado. Usa 'Restaurar mods' para volver a la versión de CurseForge."
                : $"Mod '{displayName}' eliminado.");
            RenderDetailTab();
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
    }

    private async void DetailAddMod_Click(object? sender, RoutedEventArgs e)
    {
        if (_detailInst == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Añadir mods (.jar)",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Mods JAR") { Patterns = ["*.jar"] }]
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        AddModFilesToDetailInst(paths);
    }

    private void ResetDropZoneVisual()
    {
        DetailModDropZone.Background = new SolidColorBrush(Color.Parse("#12FFFFFF"));
        DetailModDropZone.BorderBrush = new SolidColorBrush(Color.Parse("#1CFFFFFF"));
        DetailDropZoneIcon.Foreground = new SolidColorBrush(Color.Parse("#C4B5FD"));
        DetailDropZoneText.Foreground = new SolidColorBrush(Color.Parse("#DDD6FE"));
        DetailDropZoneText.Text = "Arrastra y suelta archivos .jar aquí para añadir mods";
    }

    private void InstDetails_DragOver(object? sender, DragEventArgs e)
    {
        if (_detailInst == null || _detailTab != "mods")
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        var items = e.Data.GetFiles();
        bool hasJar = items != null && items.Any(i => i.TryGetLocalPath()?.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) == true);
        if (hasJar)
        {
            e.DragEffects = DragDropEffects.Copy;
            DetailModDropZone.Background = new SolidColorBrush(Color.Parse("#2610B981"));
            DetailModDropZone.BorderBrush = new SolidColorBrush(Color.Parse("#AA34D399"));
            DetailDropZoneIcon.Foreground = new SolidColorBrush(Color.Parse("#6EE7B7"));
            DetailDropZoneText.Foreground = new SolidColorBrush(Color.Parse("#6EE7B7"));
            DetailDropZoneText.Text = "Suelta aquí para añadir el mod (.jar)";
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            DetailModDropZone.Background = new SolidColorBrush(Color.Parse("#26E11D48"));
            DetailModDropZone.BorderBrush = new SolidColorBrush(Color.Parse("#AAFB7185"));
            DetailDropZoneIcon.Foreground = new SolidColorBrush(Color.Parse("#FDA4AF"));
            DetailDropZoneText.Foreground = new SolidColorBrush(Color.Parse("#FDA4AF"));
            DetailDropZoneText.Text = "Archivo no válido: solo se admiten archivos .jar";
        }
    }

    private void InstDetails_DragLeave(object? sender, DragEventArgs e) => ResetDropZoneVisual();

    private void InstDetails_Drop(object? sender, DragEventArgs e)
    {
        ResetDropZoneVisual();
        if (_detailInst == null || _detailTab != "mods") return;
        var items = e.Data.GetFiles();
        if (items == null) return;
        var paths = items.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p) && p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Cast<string>().ToList();
        if (paths.Count == 0)
        {
            ShowToast("Solo se admiten archivos .jar en la carpeta de mods.", true);
            return;
        }
        AddModFilesToDetailInst(paths);
    }

    private void AddModFilesToDetailInst(List<string> paths)
    {
        if (_detailInst == null || paths.Count == 0) return;
        var modsDir = Path.Combine(_detailInst.Path, "mods");
        Directory.CreateDirectory(modsDir);
        int added = 0, replaced = 0, sameFile = 0;
        foreach (var src in paths)
        {
            if (!File.Exists(src) || !src.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(modsDir, Path.GetFileName(src));
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                sameFile++;
                continue;
            }
            try
            {
                bool existed = File.Exists(dest);
                File.Copy(src, dest, true);
                if (existed) replaced++;
                else added++;
            }
            catch { }
        }

        if (added > 0 || replaced > 0)
        {
            var parts = new List<string>();
            if (added > 0) parts.Add($"{added} añadido(s)");
            if (replaced > 0) parts.Add($"{replaced} reemplazado(s)");
            if (sameFile > 0) parts.Add($"{sameFile} ya estaba(n)");
            var msg = $"Mods: {string.Join(", ", parts)}.";
            ShowToast(msg);
            RenderDetailTab();
            ShowDropZoneFeedback(msg, isWarning: false);
        }
        else if (sameFile > 0)
        {
            var msg = sameFile == 1 ? "Ese mod ya se encuentra en esta carpeta." : $"{sameFile} mods ya se encuentran en esta carpeta.";
            ShowToast(msg);
            ShowDropZoneFeedback(msg, isWarning: true);
        }
    }

    private void ShowDropZoneFeedback(string msg, bool isWarning)
    {
        DetailModDropZone.Background = new SolidColorBrush(Color.Parse(isWarning ? "#26F59E0B" : "#2610B981"));
        DetailModDropZone.BorderBrush = new SolidColorBrush(Color.Parse(isWarning ? "#AAFBBF24" : "#AA34D399"));
        DetailDropZoneIcon.Foreground = new SolidColorBrush(Color.Parse(isWarning ? "#FDE68A" : "#6EE7B7"));
        DetailDropZoneText.Foreground = new SolidColorBrush(Color.Parse(isWarning ? "#FDE68A" : "#6EE7B7"));
        DetailDropZoneText.Text = msg;

        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            Dispatcher.UIThread.Post(ResetDropZoneVisual);
        });
    }

    private async void DetailRestoreMods_Click(object? sender, RoutedEventArgs e)
    {
        if (_detailInst == null) return;
        var inst = _detailInst;
        InstDetailsModal.IsVisible = false;
        try
        {
            ShowToast($"Restaurando mods originales de '{inst.Name}'...");
            await CurseForgeService.RestoreCurseForgeModsAsync(inst, OnTaskProgress);
            RefreshInstances();
            ShowToast($"Mods de '{inst.Name}' restaurados según CurseForge.");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
    }

    private static Border MakeDetailRow(string title, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var t = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#B4AEC9")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(t, 0);
        Grid.SetColumn(v, 1);
        grid.Children.Add(t);
        grid.Children.Add(v);

        var row = new Border
        {
            Padding = new Thickness(12, 9),
            Child = grid
        };
        row.Classes.Add("detail-row");
        return row;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.1} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private static List<(string Name, string Meta)> ScanFolderItems(string dir, params string[] extensions)
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (extensions.Length > 0 && !extensions.Contains(ext)) continue;
            var fi = new FileInfo(f);
            var status = ext == ".disabled" ? "Desactivado · " : "";
            list.Add((Path.GetFileName(f), $"{status}{FormatBytes(fi.Length)}"));
        }
        return list;
    }

    private static List<(string Name, string Meta)> ScanFolderOrZipItems(string dir)
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(dir)) return list;
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName))
            list.Add((Path.GetFileName(d), "Carpeta"));
        foreach (var f in Directory.EnumerateFiles(dir, "*.zip").OrderBy(Path.GetFileName))
            list.Add((Path.GetFileName(f), FormatBytes(new FileInfo(f).Length)));
        return list;
    }

    private static List<(string Name, string Meta)> ScanDatapacks(string instDir)
    {
        var list = new List<(string, string)>();
        var rootDp = Path.Combine(instDir, "datapacks");
        if (Directory.Exists(rootDp))
            foreach (var item in ScanFolderOrZipItems(rootDp))
                list.Add((item.Name, $"global · {item.Meta}"));

        var savesDir = Path.Combine(instDir, "saves");
        if (Directory.Exists(savesDir))
        {
            foreach (var world in Directory.EnumerateDirectories(savesDir))
            {
                var dpDir = Path.Combine(world, "datapacks");
                if (!Directory.Exists(dpDir)) continue;
                var worldName = Path.GetFileName(world);
                foreach (var item in ScanFolderOrZipItems(dpDir))
                    list.Add((item.Name, $"Mundo: {worldName} · {item.Meta}"));
            }
        }
        return list;
    }

    private void EditScreenMode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t)
        {
            _editFullscreen = t == "full";
            UpdateEditScreenModeUi();
        }
    }

    private void UpdateEditScreenModeUi()
    {
        SetActiveClass(EditModeWindowBtn, !_editFullscreen);
        SetActiveClass(EditModeFullBtn, _editFullscreen);
        EditResolutionPanel.IsVisible = !_editFullscreen;
    }

    private void ResPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            var parts = tag.Split('x');
            if (parts.Length == 2)
            {
                EditWidthBox.Text = parts[0];
                EditHeightBox.Text = parts[1];
            }
        }
    }

    private void EditOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingInst != null) StoreService.OpenFolder(_editingInst.Path);
    }

    private async void EditMoveFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingInst == null) return;
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Seleccionar nueva carpeta destino" });
        var newParent = dirs.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(newParent)) return;

        try
        {
            var src = _editingInst.Path;
            var dest = Path.Combine(newParent, Path.GetFileName(src.TrimEnd('\\', '/')));
            if (src != dest)
            {
                if (Directory.Exists(dest)) throw new Exception("Ya existe una carpeta con ese nombre en el destino.");
                await Task.Run(() =>
                {
                    if (Directory.Exists(src))
                    {
                        try { Directory.Move(src, dest); }
                        catch
                        {
                            StoreService.CopyDirectoryRecursive(src, dest);
                            Directory.Delete(src, true);
                        }
                    }
                    else Directory.CreateDirectory(dest);
                });
                _editingInst.Path = dest;
                _editingInst.LinkedExternal = false;
                StoreService.UpsertInstance(_editingInst);
                EditInstPathText.Text = dest;
                RefreshInstances();
                ShowToast("Carpeta movida correctamente");
            }
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
    }

    private void EditCustomRam_Changed(object? sender, RoutedEventArgs e) =>
        EditRamSlidersPanel.IsVisible = EditCustomRamCheck.IsChecked == true;

    private void EditRamSlider_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (EditMinRamSlider == null || EditMaxRamSlider == null || EditMaxRamLabel == null || EditMinRamLabel == null) return;
        if (EditMinRamSlider.Value > EditMaxRamSlider.Value) EditMinRamSlider.Value = EditMaxRamSlider.Value;
        EditMaxRamLabel.Text = $"{(int)EditMaxRamSlider.Value} MB";
        EditMinRamLabel.Text = $"{(int)EditMinRamSlider.Value} MB";
    }

    private async void PickEditJava_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleccionar java.exe",
            FileTypeFilter = [new FilePickerFileType("Ejecutable") { Patterns = ["*.exe"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is string p) EditJavaBox.Text = p;
    }

    private void UpdateEditIconPreview(string name, string? iconUrl)
    {
        EditInstIconFallback.Text = GetMonogram(name);
        EditInstIconPreview.Source = null;
        ClearEditIconBtn.IsVisible = !string.IsNullOrWhiteSpace(iconUrl);
        if (string.IsNullOrWhiteSpace(iconUrl)) return;

        if (iconUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            iconUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadBitmapAsync(iconUrl, EditInstIconPreview);
        }
        else if (File.Exists(iconUrl))
        {
            try { EditInstIconPreview.Source = new Bitmap(iconUrl); } catch { }
        }
    }

    private async void PickEditIcon_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleccionar imagen de instancia",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Imágenes") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is string p)
        {
            _editIconUrl = p;
            UpdateEditIconPreview(EditInstNameBox.Text ?? _editingInst?.Name ?? "MC", _editIconUrl);
        }
    }

    private void ClearEditIcon_Click(object? sender, RoutedEventArgs e)
    {
        _editIconUrl = null;
        UpdateEditIconPreview(EditInstNameBox.Text ?? _editingInst?.Name ?? "MC", null);
    }

    private void SaveInstSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingInst == null) return;
        if (!string.IsNullOrWhiteSpace(EditInstNameBox.Text)) _editingInst.Name = EditInstNameBox.Text.Trim();
        _editingInst.IconUrl = _editIconUrl;
        if (EditCustomRamCheck.IsChecked == true)
        {
            _editingInst.MaxRamMb = (int)EditMaxRamSlider.Value;
            _editingInst.MinRamMb = (int)EditMinRamSlider.Value;
        }
        else { _editingInst.MaxRamMb = null; _editingInst.MinRamMb = null; }

        _editingInst.JavaPath = string.IsNullOrWhiteSpace(EditJavaBox.Text) ? null : EditJavaBox.Text.Trim();
        _editingInst.JvmArgs = string.IsNullOrWhiteSpace(EditJvmArgsBox.Text) ? null : EditJvmArgsBox.Text.Trim();
        _editingInst.Fullscreen = _editFullscreen;
        if (_editFullscreen) { _editingInst.WindowWidth = null; _editingInst.WindowHeight = null; }
        else
        {
            _editingInst.WindowWidth = int.TryParse(EditWidthBox.Text, out var w) && w > 0 ? w : null;
            _editingInst.WindowHeight = int.TryParse(EditHeightBox.Text, out var h) && h > 0 ? h : null;
        }
        StoreService.UpsertInstance(_editingInst);
        InstSettingsModal.IsVisible = false;
        RefreshInstances();
    }

    // CurseForge
    private void CfSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchCurseForgeAsync();
    }
    private void CfSearch_Click(object? sender, RoutedEventArgs e) => _ = SearchCurseForgeAsync();
    private void CfFilter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) _ = SearchCurseForgeAsync();
    }

    private async Task SearchCurseForgeAsync()
    {
        CfLoadingText.IsVisible = true;
        CfWrap.Children.Clear();
        try
        {
            var gv = CfVersionCombo.SelectedItem as string ?? "Todas";
            int loaderId = (CfLoaderCombo.SelectedItem as string) switch
            {
                "Forge" => 1,
                "Fabric" => 4,
                "NeoForge" => 6,
                "Quilt" => 5,
                _ => 0
            };
            int sortId = (CfSortCombo.SelectedItem as string) switch
            {
                "Más descargados" => 6,
                "Actualizados" => 3,
                "Destacados" => 1,
                _ => 2
            };
            var list = await CurseForgeService.SearchModpacksAsync(CfSearchBox.Text ?? "", gv, loaderId, sortId);
            RenderModpacks(list);
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
        finally { CfLoadingText.IsVisible = false; }
    }

    private void RenderModpacks(List<CurseForgeModpack> list)
    {
        CfWrap.Children.Clear();
        foreach (var pack in list)
        {
            var imgCtrl = new Image { Width = 52, Height = 52, Stretch = Stretch.UniformToFill };
            if (!string.IsNullOrEmpty(pack.LogoUrl)) _ = LoadBitmapAsync(pack.LogoUrl, imgCtrl);

            var imgBorder = new Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(12),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.Parse("#1AFFFFFF")),
                Child = imgCtrl
            };

            var info = new StackPanel
            {
                Spacing = 3,
                Margin = new Thickness(12, 0, 0, 0),
                Children =
                {
                    new TextBlock { Text = pack.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = pack.AuthorLabel, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9BA0B8")) },
                    new TextBlock { Text = pack.Summary, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#CBD5E1")), TextWrapping = TextWrapping.Wrap, MaxLines = 2 }
                }
            };

            var topGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(imgBorder, 0);
            Grid.SetColumn(info, 1);
            topGrid.Children.Add(imgBorder);
            topGrid.Children.Add(info);

            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
            var dlTxt = new TextBlock { Text = pack.DownloadsLabel, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9BA0B8")), VerticalAlignment = VerticalAlignment.Center };
            var rightStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (pack.HasServerPack) rightStack.Children.Add(MakeChip("Server Pack", "#38BDF8"));
            rightStack.Children.Add(new TextBlock { Text = "Ver →", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#6EE7B7")), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(dlTxt, 0);
            Grid.SetColumn(rightStack, 1);
            footer.Children.Add(dlTxt);
            footer.Children.Add(rightStack);

            var card = new Border
            {
                Width = 365,
                Margin = new Thickness(0, 0, 14, 14),
                Padding = new Thickness(14),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel { Children = { topGrid, footer } }
            };
            card.Classes.Add("card");
            card.Classes.Add("interactive");
            var captured = pack;
            card.PointerPressed += async (_, _) => await OpenModpackModalAsync(captured);
            CfWrap.Children.Add(card);
        }
    }

    private static async Task LoadBitmapAsync(string url, Image target)
    {
        try
        {
            if (_bitmapCache.TryGetValue(url, out var cached))
            {
                Dispatcher.UIThread.Post(() => target.Source = cached);
                return;
            }
            var bytes = await ImgHttp.GetByteArrayAsync(url);
            await using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            _bitmapCache[url] = bmp;
            Dispatcher.UIThread.Post(() => target.Source = bmp);
        }
        catch { }
    }

    private async Task OpenModpackModalAsync(CurseForgeModpack pack)
    {
        _selectedModpack = pack;
        CfModalTitle.Text = pack.Name;
        CfModalSummary.Text = pack.Summary;
        CfModalAuthor.Text = pack.AuthorLabel;
        CfModalDownloads.Text = pack.DownloadsLabel;
        CfModalWebBtn.IsVisible = !string.IsNullOrWhiteSpace(pack.WebsiteUrl);
        CfModalLogo.Source = null;
        if (!string.IsNullOrEmpty(pack.LogoUrl)) _ = LoadBitmapAsync(pack.LogoUrl, CfModalLogo);

        SetActiveClass(CfTabFilesBtn, true);
        SetActiveClass(CfTabDescBtn, false);
        CfFilesList.IsVisible = true;
        CfDescPanel.IsVisible = false;
        CfModalDescText.Text = "Cargando descripción...";

        CfFilesList.Children.Clear();
        CfFilesList.Children.Add(new TextBlock { Text = "Cargando versiones disponibles...", Foreground = new SolidColorBrush(Color.Parse("#9BA0B8")), Margin = new Thickness(0, 20) });
        CfModal.IsVisible = true;

        _ = LoadModpackDescriptionAsync(pack.Id);

        try
        {
            var gv = CfVersionCombo.SelectedItem as string;
            var files = await CurseForgeService.GetModFilesAsync(pack.Id, gv);
            CfFilesList.Children.Clear();
            foreach (var f in files)
            {
                var info = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = f.DisplayName, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = $"{f.SizeLabel} · {f.VersionsLine}", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9BA0B8")) }
                    }
                };

                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                var clientBtn = new Button { Padding = new Thickness(12, 7) };
                clientBtn.Classes.Add("primary");
                clientBtn.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new PathIcon { Data = GetIcon("IconDownload"), Width = 13, Height = 13 },
                        new TextBlock { Text = "Descargar Cliente", VerticalAlignment = VerticalAlignment.Center }
                    }
                };
                var fileCap = f;
                clientBtn.Click += async (_, _) =>
                {
                    CfModal.IsVisible = false;
                    try
                    {
                        var inst = await CurseForgeService.InstallClientModpackAsync(pack.Id, fileCap.Id, pack.Name, pack.LogoUrl, OnTaskProgress);
                        RefreshInstances();
                        ShowToast($"Cliente '{inst.Name}' instalado");
                    }
                    catch (Exception ex) { ShowToast(ex.Message, true); }
                };
                btns.Children.Add(clientBtn);

                if (f.ServerPackFileId is long serverPackId)
                {
                    var spBtn = new Button { Padding = new Thickness(12, 7) };
                    spBtn.Classes.Add("glass-btn");
                    spBtn.Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new PathIcon { Data = GetIcon("IconServer"), Width = 13, Height = 13, Foreground = new SolidColorBrush(Color.Parse("#38BDF8")) },
                            new TextBlock { Text = "Server Pack", VerticalAlignment = VerticalAlignment.Center }
                        }
                    };
                    spBtn.Click += async (_, _) =>
                    {
                        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Carpeta destino para Server Pack" });
                        var target = dirs.FirstOrDefault()?.TryGetLocalPath();
                        CfModal.IsVisible = false;
                        try
                        {
                            var outDir = await CurseForgeService.DownloadServerPackAsync(pack.Id, serverPackId, pack.Name, target, OnTaskProgress);
                            StoreService.OpenFolder(outDir);
                            ShowToast($"Server Pack listo en {outDir}");
                        }
                        catch (Exception ex) { ShowToast(ex.Message, true); }
                    };
                    btns.Children.Add(spBtn);
                }

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(info, 0);
                btns.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(btns, 1);
                row.Children.Add(info);
                row.Children.Add(btns);

                CfFilesList.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#FF131024")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#2A2958")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(13, 10),
                    Child = row
                });
            }
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
    }

    private async Task LoadModpackDescriptionAsync(long modId)
    {
        try
        {
            var desc = await CurseForgeService.GetModDescriptionAsync(modId);
            Dispatcher.UIThread.Post(() =>
            {
                if (_selectedModpack?.Id == modId)
                    CfModalDescText.Text = desc;
            });
        }
        catch
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_selectedModpack?.Id == modId)
                    CfModalDescText.Text = _selectedModpack?.Summary ?? "No se pudo cargar la descripción.";
            });
        }
    }

    private void CfModalTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tab) return;
        bool isFiles = tab == "files";
        SetActiveClass(CfTabFilesBtn, isFiles);
        SetActiveClass(CfTabDescBtn, !isFiles);
        CfFilesList.IsVisible = isFiles;
        CfDescPanel.IsVisible = !isFiles;
    }

    private void CfModalOpenWeb_Click(object? sender, RoutedEventArgs e)
    {
        var url = _selectedModpack?.WebsiteUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void CloseCfModal_Click(object? sender, RoutedEventArgs e) => CfModal.IsVisible = false;

    // Settings
    private void SettingsTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string t) return;
        SettingsGeneralPanel.IsVisible = t == "general";
        SettingsAppearancePanel.IsVisible = t == "appearance";
        SetActiveClass(SettingsGeneralBtn, t == "general");
        SetActiveClass(SettingsAppearanceBtn, t == "appearance");
    }

    private void SyncSettingsControlsFromDraft()
    {
        _suppressSettingsEvents = true;
        SettingsJavaBox.Text = _draftSettings.JavaPath;
        SettingsMaxRamSlider.Value = _draftSettings.MaxRamMb;
        SettingsMinRamSlider.Value = _draftSettings.MinRamMb;
        SettingsMaxRamLabel.Text = $"{_draftSettings.MaxRamMb} MB";
        SettingsMinRamLabel.Text = $"{_draftSettings.MinRamMb} MB";
        SettingsInstancesDirBox.Text = _draftSettings.InstancesDir;
        SettingsServersDirBox.Text = _draftSettings.ServersDir;

        SettingsBgPathLabel.Text = _draftSettings.BackgroundImage ?? "Sin imagen personalizada (fondo ambiental por defecto)";
        ClearBgBtn.IsVisible = !string.IsNullOrEmpty(_draftSettings.BackgroundImage);
        SettingsBgBrightSlider.Value = Math.Round(_draftSettings.BackgroundBrightness * 100);
        SettingsBgBrightLabel.Text = $"{(int)SettingsBgBrightSlider.Value}%";
        SettingsBgBlurSlider.Value = _draftSettings.BackgroundBlur;
        SettingsBgBlurLabel.Text = $"{_draftSettings.BackgroundBlur} px";
        _suppressSettingsEvents = false;
        UpdateUnsavedBarVisibility();
    }

    private void UpdateUnsavedBarVisibility()
    {
        UnsavedBar.IsVisible = ViewSettings.IsVisible && !_draftSettings.ValueEquals(_savedSettings);
    }

    private void SettingsDraft_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        _draftSettings.JavaPath = SettingsJavaBox.Text ?? "java";
        _draftSettings.InstancesDir = SettingsInstancesDirBox.Text ?? _savedSettings.InstancesDir;
        _draftSettings.ServersDir = SettingsServersDirBox.Text ?? _savedSettings.ServersDir;
        UpdateUnsavedBarVisibility();
    }

    private void SettingsRam_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (SettingsMinRamSlider.Value > SettingsMaxRamSlider.Value)
            SettingsMinRamSlider.Value = SettingsMaxRamSlider.Value;
        _draftSettings.MaxRamMb = (int)SettingsMaxRamSlider.Value;
        _draftSettings.MinRamMb = (int)SettingsMinRamSlider.Value;
        SettingsMaxRamLabel.Text = $"{_draftSettings.MaxRamMb} MB";
        SettingsMinRamLabel.Text = $"{_draftSettings.MinRamMb} MB";
        UpdateUnsavedBarVisibility();
    }

    private void DetectJava_Click(object? sender, RoutedEventArgs e)
    {
        var first = StoreService.DetectJavaInstallations().FirstOrDefault();
        if (first != null)
        {
            _draftSettings.JavaPath = first;
            SyncSettingsControlsFromDraft();
        }
    }

    private async void PickJava_Click(object? sender, RoutedEventArgs e)
    {
        var f = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleccionar java.exe",
            FileTypeFilter = [new FilePickerFileType("Ejecutable") { Patterns = ["*.exe"] }]
        });
        if (f.FirstOrDefault()?.TryGetLocalPath() is string p)
        {
            _draftSettings.JavaPath = p;
            SyncSettingsControlsFromDraft();
        }
    }

    private async void PickInstancesDir_Click(object? sender, RoutedEventArgs e)
    {
        var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Carpeta de instancias" });
        if (d.FirstOrDefault()?.TryGetLocalPath() is string p)
        {
            _draftSettings.InstancesDir = p;
            SyncSettingsControlsFromDraft();
        }
    }

    private async void PickServersDir_Click(object? sender, RoutedEventArgs e)
    {
        var d = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Carpeta de Server Packs" });
        if (d.FirstOrDefault()?.TryGetLocalPath() is string p)
        {
            _draftSettings.ServersDir = p;
            SyncSettingsControlsFromDraft();
        }
    }

    private async void PickBg_Click(object? sender, RoutedEventArgs e)
    {
        var f = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleccionar imagen de fondo",
            FileTypeFilter = [new FilePickerFileType("Imágenes") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }]
        });
        if (f.FirstOrDefault()?.TryGetLocalPath() is string p)
        {
            _draftSettings.BackgroundImage = p;
            SyncSettingsControlsFromDraft();
            ApplyBackgroundPreview(_draftSettings);
        }
    }

    private void ClearBg_Click(object? sender, RoutedEventArgs e)
    {
        _draftSettings.BackgroundImage = null;
        SyncSettingsControlsFromDraft();
        ApplyBackgroundPreview(_draftSettings);
    }

    private void SettingsBgSlider_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        _draftSettings.BackgroundBrightness = SettingsBgBrightSlider.Value / 100.0;
        _draftSettings.BackgroundBlur = (int)SettingsBgBlurSlider.Value;
        SettingsBgBrightLabel.Text = $"{(int)SettingsBgBrightSlider.Value}%";
        SettingsBgBlurLabel.Text = $"{_draftSettings.BackgroundBlur} px";
        ApplyBackgroundPreview(_draftSettings);
        UpdateUnsavedBarVisibility();
    }

    private void ApplyBackgroundPreview(LauncherSettings s)
    {
        if (string.IsNullOrEmpty(s.BackgroundImage) || !File.Exists(s.BackgroundImage))
        {
            BgImageHost.IsVisible = false;
            BgImage.Source = null;
            return;
        }
        try
        {
            BgImage.Source = new Bitmap(s.BackgroundImage);
            BgImage.Effect = s.BackgroundBlur > 0 ? new BlurEffect { Radius = s.BackgroundBlur * 3.0 } : null;
            double dimAlpha = Math.Clamp(1.0 - (s.BackgroundBrightness * 0.85), 0.05, 0.96);
            BgDimOverlay.Background = new SolidColorBrush(Color.FromArgb((byte)(dimAlpha * 255), 0, 0, 0));
            BgImageHost.IsVisible = true;
        }
        catch
        {
            BgImageHost.IsVisible = false;
        }
    }

    private void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        StoreService.SaveSettings(_draftSettings);
        _savedSettings = _draftSettings.Clone();
        UpdateUnsavedBarVisibility();
        ShowToast("Ajustes guardados");
    }

    private void CancelSettings_Click(object? sender, RoutedEventArgs e)
    {
        _draftSettings = _savedSettings.Clone();
        SyncSettingsControlsFromDraft();
        ApplyBackgroundPreview(_draftSettings);
    }

    private LauncherUpdateInfo? _pendingLauncherUpdate;

    private async void CheckLauncherUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_pendingLauncherUpdate != null)
        {
            CheckUpdateBtn.IsEnabled = false;
            CheckUpdateBtnText.Text = "Descargando...";
            try
            {
                await UpdateService.DownloadAndApplyUpdateAsync(_pendingLauncherUpdate);
            }
            catch (Exception ex)
            {
                ShowToast($"Error al actualizar: {ex.Message}", true);
                CheckUpdateBtn.IsEnabled = true;
                CheckUpdateBtnText.Text = $"Actualizar a v{_pendingLauncherUpdate.Version}";
            }
            return;
        }

        CheckUpdateBtn.IsEnabled = false;
        CheckUpdateBtnText.Text = "Buscando...";
        try
        {
            var info = await UpdateService.CheckForUpdateAsync();
            if (info == null)
            {
                UpdateStatusText.Text = $"Tienes la última versión (v{UpdateService.CurrentVersion}).";
                ShowToast("Kuro Launcher está actualizado.");
                CheckUpdateBtnText.Text = "Buscar actualizaciones";
            }
            else
            {
                _pendingLauncherUpdate = info;
                UpdateStatusText.Text = $"Nueva versión disponible: v{info.Version} (actual: v{UpdateService.CurrentVersion})";
                CheckUpdateBtn.Classes.Remove("glass-btn");
                CheckUpdateBtn.Classes.Add("primary");
                CheckUpdateBtnText.Text = $"Actualizar a v{info.Version}";
                ShowToast($"Nueva versión v{info.Version} disponible.");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"No se pudo comprobar actualizaciones: {ex.Message}", true);
            CheckUpdateBtnText.Text = "Buscar actualizaciones";
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }
}

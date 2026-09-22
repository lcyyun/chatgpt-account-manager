using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GptPlusManager.Wpf.Infrastructure;
using GptPlusManager.Wpf.Services;
using GptPlusManager.Wpf.ViewModels;

namespace GptPlusManager.Wpf;

public partial class MainWindow : Window
{
    private Point _dragStart;
    private Point _lastDragPoint;
    private AccountViewModel? _dragged;
    private AccountViewModel[]? _originalOrder;
    private ContentPresenter? _sourceContainer;
    private DragGhostAdorner? _dragGhost;
    private AdornerLayer? _adornerLayer;
    private DispatcherTimer? _autoScrollTimer;
    private bool _isDragging;
    private bool _dropAccepted;
    private bool _previewQueued;
    private ProviderPage? _providerPage;
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();
        var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dataRoot = System.IO.Path.Combine(root, "gptplus");
        var providerService = new CoreProviderManagerService();
        var viewModel = new MainViewModel(new CoreAccountManagerService(dataRoot), providerService, new WpfUiService());
        DataContext = viewModel;

        _providerPage = new ProviderPage(providerService);
        _providerPage.StatusChanged += (_, message) => viewModel.ShowToast(message, false);
        _providerPage.CodexRestarted += async (_, _) => await viewModel.RefreshCurrentAccountAsync();
        viewModel.ProviderPage = _providerPage;
        ThirdPartyPageHost.Content = _providerPage;

        Loaded += async (_, _) => await viewModel.InitializeCommand.ExecuteAsync(null);
        Loaded += async (_, _) => await RefreshThirdPartyHintAsync();
        Closed += (_, _) => viewModel.Dispose();
    }

    /// <summary>
    /// 在「官方账号」与「第三方」两个界面之间切换。
    ///
    /// <para>注意：XAML 里的 <c>IsChecked="True"</c> 会在 <see cref="Window.InitializeComponent"/>
    /// 期间就触发本处理器，那时字段还是 null。所以这里必须容忍"页面尚未装配"这一次调用，
    /// 真正的初始化在构造函数末尾完成。</para>
    /// </summary>
    private async void PageTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_providerPage is null || ThirdPartyPageHost is null || OfficialPage is null) return;

        var thirdParty = ThirdPartyPageTab.IsChecked == true;

        OfficialPage.Visibility = thirdParty ? Visibility.Collapsed : Visibility.Visible;
        OfficialToolbar.Visibility = thirdParty ? Visibility.Collapsed : Visibility.Visible;
        ThirdPartyPageHost.Visibility = thirdParty ? Visibility.Visible : Visibility.Collapsed;
        ThirdPartyToolbar.Visibility = thirdParty ? Visibility.Visible : Visibility.Collapsed;

        // 验证码与搜索只对官方账号页有意义，第三方页让出位置。
        TotpBadge.Visibility = thirdParty ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = thirdParty ? Visibility.Collapsed : Visibility.Visible;

        if (thirdParty)
        {
            await _providerPage.ActivateAsync();
            ThirdPartyTabHint.Text = string.Empty;
        }
        else
        {
            await RefreshThirdPartyHintAsync();
        }
    }

    /// <summary>在标签上提示第三方模式是否生效，避免用户忘记自己切过。</summary>
    private async Task RefreshThirdPartyHintAsync()
    {
        if (_providerPage is null || ThirdPartyTabHint is null) return;

        try
        {
            var snapshot = await _providerPage.GetSnapshotAsync();
            ThirdPartyTabHint.Text = snapshot.Mode == Core.Codex.CodexRoutingMode.ThirdParty
                ? $"● 第三方模式生效中（{snapshot.Model}）"
                : string.Empty;
        }
        catch (Exception)
        {
            ThirdPartyTabHint.Text = string.Empty;
        }
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.CanReorder != true || sender is not FrameworkElement { DataContext: AccountViewModel account }) return;
        _dragStart = e.GetPosition(this);
        _dragged = account;
        _sourceContainer = FindAncestor<ContentPresenter>((DependencyObject)sender);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragged is null || e.LeftButton != MouseButtonState.Pressed || ViewModel?.CanReorder != true) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var dragged = _dragged;
        var handle = (UIElement)sender;
        var source = _sourceContainer;
        var grabPoint = source is null ? new Point() : e.GetPosition(source);
        handle.ReleaseMouseCapture();
        if (source is null) { _dragged = null; return; }

        _isDragging = true;
        _dropAccepted = false;
        _originalOrder = ViewModel.CaptureOrder();
        source.Opacity = 0.22;
        _adornerLayer = AdornerLayer.GetAdornerLayer(CardsScroll);
        if (_adornerLayer is not null)
        {
            _dragGhost = new DragGhostAdorner(CardsScroll, source, grabPoint);
            _adornerLayer.Add(_dragGhost);
            _dragGhost.MoveTo(Mouse.GetPosition(CardsScroll));
        }
        StartAutoScroll();

        try
        {
            var effect = DragDrop.DoDragDrop(handle, dragged, DragDropEffects.Move);
            _dropAccepted &= effect == DragDropEffects.Move;
        }
        catch (Exception ex)
        {
            ViewModel.ShowToast("拖动失败: " + ex.Message, true);
            _dropAccepted = false;
        }
        finally
        {
            StopAutoScroll();
            RemoveDragGhost();
            source.Opacity = 1;
            _isDragging = false;
            _previewQueued = false;
            _dragged = null;
            _sourceContainer = null;
            var original = _originalOrder;
            _originalOrder = null;
            if (original is not null)
            {
                if (_dropAccepted)
                    Dispatcher.BeginInvoke(new Action(async () => await ViewModel.CommitOrderAsync(original)), DispatcherPriority.Background);
                else
                    ViewModel.RestoreOrder(original);
            }
        }
        e.Handled = true;
    }

    private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).ReleaseMouseCapture();
        if (!_isDragging) _dragged = null;
        e.Handled = true;
    }

    private void Cards_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!_isDragging || _dragged is null || ViewModel?.CanReorder != true)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        _lastDragPoint = e.GetPosition(CardsScroll);
        _dragGhost?.MoveTo(_lastDragPoint);

        var boundary = CalculateInsertionBoundary(e.GetPosition(AccountsItems), _dragged);
        if (boundary < 0)
        {
            e.Effects = DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.Move;
            QueuePreviewMove(boundary);
        }
        e.Handled = true;
    }

    private void Cards_PreviewDrop(object sender, DragEventArgs e)
    {
        var boundary = _dragged is null ? -1 : CalculateInsertionBoundary(e.GetPosition(AccountsItems), _dragged);
        if (boundary >= 0 && _dragged is not null)
        {
            ViewModel?.PreviewMove(_dragged, boundary);
            _dropAccepted = true;
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Cards_PreviewDragLeave(object sender, DragEventArgs e)
    {
        // Do not cancel immediately: WPF raises DragLeave while crossing child visuals.
        e.Handled = true;
    }

    private void QueuePreviewMove(int boundary)
    {
        if (_previewQueued) return;
        _previewQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _previewQueued = false;
            if (!_isDragging || _dragged is null || ViewModel is null) return;
            var before = CapturePositions();
            ViewModel.PreviewMove(_dragged, boundary);
            AccountsItems.UpdateLayout();
            AnimateLayoutChanges(before);
            var current = AccountsItems.ItemContainerGenerator.ContainerFromItem(_dragged) as ContentPresenter;
            if (current is not null) current.Opacity = 0.22;
        }), DispatcherPriority.Render);
    }

    private int CalculateInsertionBoundary(Point point, AccountViewModel source)
    {
        var items = ViewModel?.Accounts;
        if (items is null || items.Count == 0) return 0;
        var entries = new List<(int Index, AccountViewModel Item, Rect Bounds)>();
        for (var i = 0; i < items.Count; i++)
        {
            if (AccountsItems.ItemContainerGenerator.ContainerFromItem(items[i]) is not FrameworkElement container) continue;
            try
            {
                var topLeft = container.TransformToAncestor(AccountsItems).Transform(new Point());
                entries.Add((i, items[i], new Rect(topLeft, container.RenderSize)));
            }
            catch (InvalidOperationException) { }
        }

        var group = entries.Where(x => x.Item.IsInvalid == source.IsInvalid).ToList();
        if (group.Count == 0) return ViewModel!.GetGroupStart(source.IsInvalid);

        // If the pointer is visibly over the other validity group, crossing is forbidden.
        var hit = entries.FirstOrDefault(x => x.Bounds.Contains(point));
        if (hit.Item is not null && hit.Item.IsInvalid != source.IsInvalid) return -1;

        var rows = new List<List<(int Index, AccountViewModel Item, Rect Bounds)>>();
        foreach (var entry in group.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].Bounds.Top - entry.Bounds.Top) <= 2);
            if (row is null) { row = []; rows.Add(row); }
            row.Add(entry);
        }
        foreach (var row in rows) row.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));

        if (point.Y < rows[0].Min(x => x.Bounds.Top)) return rows[0][0].Index;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var top = row.Min(x => x.Bounds.Top);
            var bottom = row.Max(x => x.Bounds.Bottom);
            var nextTop = rowIndex + 1 < rows.Count ? rows[rowIndex + 1].Min(x => x.Bounds.Top) : double.PositiveInfinity;
            if (point.Y <= bottom || point.Y < nextTop)
            {
                if (point.Y > bottom && rowIndex + 1 < rows.Count) return rows[rowIndex + 1][0].Index;
                foreach (var entry in row)
                    if (point.X < entry.Bounds.Left + entry.Bounds.Width / 2) return entry.Index;
                return row[^1].Index + 1;
            }
        }
        return ViewModel!.GetGroupEnd(source.IsInvalid);
    }

    private Dictionary<AccountViewModel, Point> CapturePositions()
    {
        var result = new Dictionary<AccountViewModel, Point>();
        if (ViewModel is null) return result;
        foreach (var item in ViewModel.Accounts)
        {
            if (AccountsItems.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement element)
                try { result[item] = element.TransformToAncestor(AccountsItems).Transform(new Point()); } catch { }
        }
        return result;
    }

    private void AnimateLayoutChanges(Dictionary<AccountViewModel, Point> before)
    {
        foreach (var pair in before)
        {
            if (ReferenceEquals(pair.Key, _dragged)) continue;
            if (AccountsItems.ItemContainerGenerator.ContainerFromItem(pair.Key) is not FrameworkElement element) continue;
            Point after;
            try { after = element.TransformToAncestor(AccountsItems).Transform(new Point()); } catch { continue; }
            var dx = pair.Value.X - after.X;
            var dy = pair.Value.Y - after.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;
            var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
            element.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(140))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(140))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void StartAutoScroll()
    {
        _autoScrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            if (!_isDragging) return;
            const double edge = 48;
            var y = _lastDragPoint.Y;
            var delta = y < edge ? -Math.Max(4, (edge - y) / 4)
                : y > CardsScroll.ActualHeight - edge ? Math.Max(4, (y - (CardsScroll.ActualHeight - edge)) / 4) : 0;
            if (delta != 0) CardsScroll.ScrollToVerticalOffset(Math.Clamp(CardsScroll.VerticalOffset + delta, 0, CardsScroll.ScrollableHeight));
        }, Dispatcher);
        _autoScrollTimer.Start();
    }

    private void StopAutoScroll() { _autoScrollTimer?.Stop(); _autoScrollTimer = null; }
    private void RemoveDragGhost()
    {
        if (_dragGhost is not null && _adornerLayer is not null) _adornerLayer.Remove(_dragGhost);
        _dragGhost = null; _adornerLayer = null;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}

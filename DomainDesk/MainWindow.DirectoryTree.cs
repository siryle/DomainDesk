using System.Windows;
using System.Windows.Controls;
using DomainDesk.Services;

namespace DomainDesk;

public partial class MainWindow
{
    private IDirectoryService? _treeOwner;
    private DirectoryNode? _directoryRoot;
    private DirectoryContainer? _selectedDirectory;
    private bool _settingTree;
    private string _loadedScopeDescription = "";

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDirectoryLayout();

    private void UpdateDirectoryLayout()
    {
        if (OuColumn is null) return;
        var width = WorkspaceRoot.ActualWidth > 0 ? WorkspaceRoot.ActualWidth : ActualWidth;
        OuColumn.Width = new GridLength(width >= 1700 ? 330 : width >= 1350 ? 315 : 280);
        OuGap.Width = new GridLength(16);
        InspectorColumn.Width = new GridLength(width >= 1700 ? 330 : width >= 1350 ? 290 : 270);
    }

    private void ClearDirectoryResult(string message = "正在查询当前范围…")
    {
        _items = [];
        _loadedScopeDescription = "";
        ObjectsGrid.ItemsSource = null;
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
        UserCounts.Text = "查询结果 —";
        UpdateDetails();
    }

    private async Task EnsureDirectoryTreeAsync()
    {
        if (ReferenceEquals(_treeOwner, _directory)) return;
        _settingTree = true;
        try
        {
            _treeOwner = _directory;
            _directoryRoot = null;
            _selectedDirectory = null;
            OuTree.ItemsSource = null;
            OuFilterBox.Clear();
            IncludeDescendantsBox.IsChecked = true;
        }
        finally { _settingTree = false; }
        UpdateScopeHeading();
        TreeNotice.Text = "正在读取当前域目录…";
        try
        {
            var result = await _directory.GetContainersAsync();
            ApplyTree(result, DirectoryNode.Build(result, _directory.Session.BaseDn), null, true);
        }
        catch
        {
            TreeNotice.Text = "目录读取失败。请点“刷新目录”重试；不会自动扩大查询范围。";
            throw;
        }
    }

    private void ApplyTree(ContainerResult result, DirectoryNode root, Guid? selectedId, bool initial)
    {
        var expanded = _directoryRoot?.All().Where(x => x.IsExpanded && x.Container is not null).Select(x => x.Container!.Id).ToHashSet() ?? [];
        var known = _directoryRoot?.All().Where(x => x.Container is not null).Select(x => x.Container!.Id).ToHashSet() ?? [];
        var selected = initial ? root : root.All().FirstOrDefault(x => x.Container?.Id == selectedId);
        _settingTree = true;
        try
        {
            _directoryRoot = root;
            _treeOwner = _directory;
            _selectedDirectory = selected?.Container;
            foreach (var node in root.All()) if (node.Container is not null && known.Contains(node.Container.Id)) node.IsExpanded = expanded.Contains(node.Container.Id);
            if (selected is not null) { root.ExpandPath(selected); selected.IsSelected = true; }
            root.Filter(OuFilterBox.Text);
            OuTree.ItemsSource = new[] { root };
            TreeNotice.Text = string.Join("\n", new[] {
                result.Truncated ? "目录超过 5000 项，列表已截断。" : "",
                !initial && selected is null ? "原目录已不可见或不存在，请重新选择；未切换到全域。" : ""
            }.Where(x => x.Length > 0));
        }
        finally { _settingTree = false; }
        UpdateScopeHeading();
    }

    private async Task RefreshDirectoryTreeAsync(Guid? preferredId = null)
    {
        var selected = preferredId ?? _selectedDirectory?.Id;
        ClearDirectoryResult("目录刷新后将重新查询。请勿使用旧结果。");
        _selectedDirectory = null;
        UpdateScopeHeading();
        try
        {
            var result = await _directory.GetContainersAsync();
            ApplyTree(result, DirectoryNode.Build(result, _directory.Session.BaseDn), selected, false);
        }
        catch
        {
            _settingTree = true;
            try { OuTree.ItemsSource = null; _directoryRoot = null; _treeOwner = _directory; }
            finally { _settingTree = false; }
            TreeNotice.Text = "目录刷新失败，请重试。旧对象结果已清空。";
            throw;
        }
    }

    private void UpdateScopeHeading()
    {
        ScopeBreadcrumb.Text = _selectedDirectory is null ? "DIRECTORY / 选择可访问的目录" : DistinguishedNames.Breadcrumb(_selectedDirectory.DistinguishedName, _directory.Session.BaseDn);
        ScopeTitle.Text = _selectedDirectory?.Name ?? "请选择目录";
    }

    private async void OuTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_settingTree || _busy || e.NewValue is not DirectoryNode node) return;
        if (node.Container is not null && node.Container == _selectedDirectory) return;
        _selectedDirectory = node.Container;
        ClearDirectoryResult(node.Container is null ? "此节点未被服务器列出，请选择可访问的子目录。" : "正在查询所选目录…");
        UpdateScopeHeading();
        if (node.Container is not null) await RunBusyAsync(LoadDirectoryAsync);
        else StatusText.Text = "占位节点不可查询或作为新建目标；不会自动查询全域。";
    }

    private void OuFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (_settingTree) return;
        _settingTree = true;
        try { _directoryRoot?.Filter(OuFilterBox.Text); }
        finally { _settingTree = false; }
    }

    private async void ScopeDepth_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _settingTree || _busy) return;
        await RunBusyAsync(LoadDirectoryAsync);
    }

    private async void ObjectType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _settingTree || _busy || ObjectTypeBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _kind = Enum.Parse<DirectoryKind>(tag);
        await RunBusyAsync(LoadDirectoryAsync);
    }

    private async void RefreshTree_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(_directory.Session.Server))
        {
            await LoadDirectoryAsync();
            return;
        }
        await EnsureDirectoryTreeAsync();
        await RefreshDirectoryTreeAsync();
        await LoadDirectoryAsync();
    });
}

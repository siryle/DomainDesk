using System.ComponentModel;
using System.DirectoryServices.Protocols;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DomainDesk.Dialogs;
using DomainDesk.Services;
using Microsoft.Win32;

namespace DomainDesk;

public partial class MainWindow : Window
{
    private IDirectoryService _directory = new DisconnectedDirectoryService();
    private readonly AuditLog _audit = new();
    private IReadOnlyList<DirectoryItem> _items = [];
    private DirectoryKind _kind = DirectoryKind.All;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _preferredMinimum = new Size(MinWidth, MinHeight);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void UpdateConnectionHeader()
    {
        var connected = !string.IsNullOrWhiteSpace(_directory.Session.Server);
        ConnectionLabel.Text = connected ? "已连接 · " + _directory.Session.Server : "尚未连接";
        ConnectionLabel.ToolTip = connected ? _directory.Session.Server + "\n" + _directory.Session.Identity : "连接 AD 域后读取目录";
        ConnectionDot.Fill = new SolidColorBrush(connected ? Color.FromRgb(37, 156, 96) : Color.FromRgb(129, 146, 152));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(LoadDirectoryAsync);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new ConnectDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Settings is null) return;
        using var settings = dialog.Settings;
        await RunBusyAsync(async () =>
        {
            var candidate = await LdapDirectoryService.ConnectAsync(settings);
            QueryResult initial;
            ContainerResult containers;
            DirectoryNode root;
            try
            {
                containers = await candidate.GetContainersAsync();
                root = DirectoryNode.Build(containers, candidate.Session.BaseDn);
                initial = await candidate.QueryAsync(_kind, SearchBox.Text, new(root.Container!, true));
            }
            catch { candidate.Dispose(); throw; }
            var old = _directory;
            _directory = candidate;
            old.Dispose();
            _directoryRoot = null;
            _settingTree = true;
            try { OuFilterBox.Clear(); IncludeDescendantsBox.IsChecked = true; }
            finally { _settingTree = false; }
            ApplyTree(containers, root, null, true);
            ApplyDirectory(initial);
            ConnectButton.ToolTip = $"{candidate.Session.Server}\n{candidate.Session.Identity}\n{(candidate.Session.AllowWrites ? "可修改，每次须确认" : "只读连接")}\n点击可重新连接或切换服务器。";
            LogBestEffort(candidate.Session, "连接", candidate.Session.Server, "成功", "已验证 LDAP 连接");
        });
    }

    private async Task LoadDirectoryAsync()
    {
        UpdateConnectionHeader();
        var selected = (ObjectsGrid.SelectedItem as DirectoryItem)?.Id;
        UpdateDirectoryLayout();
        ClearDirectoryResult();
        if (string.IsNullOrWhiteSpace(_directory.Session.Server))
        {
            UpdateScopeHeading();
            EmptyText.Text = "请先连接 AD 域。";
            StatusText.Text = "尚未连接 AD 域。";
            return;
        }
        await EnsureDirectoryTreeAsync();
        if (_selectedDirectory is null)
        {
            EmptyText.Text = "请选择左侧有效目录。当前不会查询全域。";
            StatusText.Text = "未选中有效目录，请选择目录或刷新目录树。";
            return;
        }
        var scope = new DirectoryQueryScope(_selectedDirectory, IncludeDescendantsBox.IsChecked == true);
        UpdateScopeHeading();
        var result = await _directory.QueryAsync(_kind, SearchBox.Text, scope);
        ApplyDirectory(result);
        if (selected.HasValue) ObjectsGrid.SelectedItem = _items.FirstOrDefault(x => x.Id == selected.Value) ?? _items.FirstOrDefault();
    }

    private void ApplyDirectory(QueryResult result)
    {
        UpdateConnectionHeader();
        UpdateDirectoryLayout();
        _items = result.Items;
        ObjectsGrid.ItemsSource = _items;
        ObjectsGrid.SelectedItem = _items.FirstOrDefault();
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = "当前目录范围暂无匹配对象。可调整搜索、对象类型或包含下级目录。";
        UserCounts.Text = $"{_items.Count:N0}{(result.Truncated ? "+" : "")} 个对象 · 用户 {_items.Count(x => x.Kind == DirectoryKind.Users)} · 用户组 {_items.Count(x => x.Kind == DirectoryKind.Groups)} · 计算机 {_items.Count(x => x.Kind == DirectoryKind.Computers)}";
        var typeLabel = (ObjectTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        _loadedScopeDescription = $"{_selectedDirectory?.DistinguishedName} / {(IncludeDescendantsBox.IsChecked == true ? "包含下级目录" : "仅直接对象")} / {typeLabel}";
        StatusText.Text = result.Truncated ? "结果超过 5000 条，仅显示前 5000 条。请缩小查询范围；统计与导出仅包含当前结果。" : $"查询完成 · {_items.Count} 个对象 · {DateTime.Now:HH:mm:ss} · 统计仅针对当前查询";
        StatusText.Text += IncludeDescendantsBox.IsChecked == true ? " · 包含下级目录" : " · 仅当前目录直接对象";
        StatusText.Text += $" · {_directory.Session.Server} · {(_directory.Session.AllowWrites ? "可修改" : "只读")}";
        UpdateDetails();
    }

    private void Objects_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();
    private void UpdateDetails()
    {
        var item = ObjectsGrid.SelectedItem as DirectoryItem;
        DetailName.Text = item?.DisplayName ?? "选择一个对象";
        DetailType.Text = item is { Kind: DirectoryKind.Groups, GroupType: not 0 } ? $"用户组 · {item.GroupTypeLabel}" : item?.KindLabel ?? "—";
        DetailAccount.Text = item?.Kind == DirectoryKind.Computers ? $"真实 AD 账号：{item.Account}\n（末尾 $ 是机器账号的标准格式）\nDNS：{item.DnsHostName}\n位置：{item.Location}" : item?.Account ?? "";
        DetailStatus.Text = item?.Status ?? "—";
        DetailEmail.Text = item is null ? "—" : string.IsNullOrEmpty(item.Email) ? item.Description : item.Email;
        DetailLogon.Text = item?.LastLogonText ?? "—";
        DetailContainer.Text = item?.ContainerName ?? "—";
        DetailDepartment.Text = item?.Department ?? "—";
        DetailGroups.Text = item is null ? "—" : item.MemberOf.Count == 0 ? "无可显示的直接组信息" : string.Join("\n", item.MemberOf.Select(DistinguishedNames.Name));
        AccountActions.Visibility = item?.Kind == DirectoryKind.Users ? Visibility.Visible : Visibility.Collapsed;
        GroupActions.Visibility = item?.Kind == DirectoryKind.Groups ? Visibility.Visible : Visibility.Collapsed;
        ComputerActions.Visibility = item?.Kind == DirectoryKind.Computers ? Visibility.Visible : Visibility.Collapsed;
        UserDepartmentPanel.Visibility = item?.Kind == DirectoryKind.Users ? Visibility.Visible : Visibility.Collapsed;
        LogonPanel.Visibility = item is not null && item.Kind != DirectoryKind.Groups ? Visibility.Visible : Visibility.Collapsed;
        WriteHint.Text = _directory.Session.AllowWrites ? "操作逐次确认，由域控检查权限。" : "只读连接：当前不能修改域对象。";
        UpdateActions();
    }

    private void UpdateActions()
    {
        var item = ObjectsGrid.SelectedItem as DirectoryItem;
        var allowed = !_busy && _directory.Session.AllowWrites && item?.Kind == DirectoryKind.Users;
        UnlockButton.IsEnabled = allowed;
        EnableButton.IsEnabled = allowed && !item!.Enabled;
        DisableButton.IsEnabled = allowed && item!.Enabled;
        PasswordButton.IsEnabled = allowed;
        UnlockButton.ToolTip = "将 lockoutTime 清零；不依赖可能延迟的锁定状态查询。";
        PasswordButton.ToolTip = "支持 LDAPS 或 LDAP 389 的 SASL 加密；最终由域控验证加密强度和账号权限。";
        NewUserButton.IsEnabled = NewComputerButton.IsEnabled = NewGroupButton.IsEnabled = !_busy && _directory.Session.AllowWrites && _selectedDirectory is not null;
        ExportButton.IsEnabled = !_busy && _items.Count > 0;
        MembersButton.IsEnabled = !_busy && item?.Kind == DirectoryKind.Groups;
        EditComputerButton.IsEnabled = DeleteComputerButton.IsEnabled = !_busy && _directory.Session.AllowWrites && item?.CanManageComputer == true;
    }

    private async void AccountAction_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_directory.Session.AllowWrites || ObjectsGrid.SelectedItem is not DirectoryItem { Kind: DirectoryKind.Users } item || sender is not Button { Tag: string tag }) return;
        var action = Enum.Parse<AccountAction>(tag);
        var label = action switch { AccountAction.Unlock => "解锁用户", AccountAction.Enable => "启用用户", AccountAction.Disable => "停用用户", _ => "重置密码" };
        SecureString? password = null;
        var mustChange = true;
        if (action == AccountAction.ResetPassword)
        {
            var dialog = new PasswordDialog(item, false) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            password = dialog.NewPassword;
            mustChange = dialog.MustChangePassword;
        }
        try
        {
            if (!Confirm(_directory.Session, label, $"{item.Name} / {item.Account}\n{item.DistinguishedName}", action == AccountAction.Disable ? "该用户将无法登录。" : "将修改所选用户账号。")) return;
            await RunBusyAsync(() => WriteAndRefreshAsync(_directory.Session, label, item.DistinguishedName,
                () => _directory.ExecuteAsync(item, action, password, mustChange), LoadDirectoryAsync));
        }
        finally { password?.Dispose(); }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(LoadDirectoryAsync);

    private async void NewUser_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_directory.Session.AllowWrites) return;
        ContainerResult? containers = null;
        await RunBusyAsync(async () => containers = await _directory.GetContainersAsync());
        if (containers is null) return;
        var dialog = new NewUserDialog(containers, _directory.Session, _selectedDirectory?.Id) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Spec is not NewUserSpec spec || dialog.Password is null) return;
        using var password = dialog.Password;
        var dn = DirectoryMutations.NewUserDn(spec);
        if (!Confirm(_directory.Session, "新建用户", $"{dn}\n{spec.Account} / {spec.UserPrincipalName}", $"将创建{(spec.Enable ? "启用" : "停用")}账号。密码必须符合域策略；中途失败可能留下已创建账号，请先刷新核实，勿重复新建。")) return;
        await RunBusyAsync(() => WriteAndRefreshAsync(_directory.Session, "新建用户", dn,
            () => _directory.CreateUserAsync(spec, password), () => RefreshAfterCreationAsync(spec.Container, dn)));
    }

    private async void Members_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ObjectsGrid.SelectedItem is not DirectoryItem { Kind: DirectoryKind.Groups } group) return;
        var dialog = new GroupMembersDialog(_directory, group, async (user, add, refresh) =>
        {
            var label = add ? "添加组成员" : "移除组成员";
            var target = $"组：{group.DistinguishedName}\n用户：{user.DistinguishedName}";
            if (!Confirm(_directory.Session, label, target, "修改组成员可能增加或撤销用户的访问权限，尤其是特权组。只修改此用户的直接成员关系，不删除用户。")) return;
            await WriteAndRefreshAsync(_directory.Session, label, target, () => _directory.ChangeGroupUserAsync(group, user, add), refresh);
        }) { Owner = this };
        dialog.ShowDialog();
        await RunBusyAsync(LoadDirectoryAsync);
    }

    private async void ComputerEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_directory.Session.AllowWrites || ObjectsGrid.SelectedItem is not DirectoryItem { CanManageComputer: true } item) return;
        var dialog = new ComputerEditDialog(item) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Changes is not ComputerChanges changes) return;
        if (!Confirm(_directory.Session, "编辑计算机", item.DistinguishedName, $"描述：{changes.Description}\n位置：{changes.Location}\n仅修改这两个 AD 属性，不重命名机器。")) return;
        await RunBusyAsync(() => WriteAndRefreshAsync(_directory.Session, "编辑计算机", item.DistinguishedName,
            () => _directory.UpdateComputerAsync(item, changes), LoadDirectoryAsync));
    }

    private async void ComputerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_directory.Session.AllowWrites || ObjectsGrid.SelectedItem is not DirectoryItem { CanManageComputer: true } item) return;
        if (new ComputerDeleteDialog(item, _directory.Session) { Owner = this }.ShowDialog() != true) return;
        await RunBusyAsync(() => WriteAndRefreshAsync(_directory.Session, "删除计算机", item.DistinguishedName,
            () => _directory.DeleteComputerAsync(item), LoadDirectoryAsync));
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(LoadDirectoryAsync);
    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunBusyAsync(LoadDirectoryAsync);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _items.Count == 0) return;
        if (MessageBox.Show(this, $"导出包含域对象名称、邮箱、DN 等内部信息，请妥善保管。\n范围：{_loadedScopeDescription}\n仅导出当前 {_items.Count} 个查询结果。继续？", "导出数据", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = $"DomainDesk-{_kind}-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            CsvExporter.Export(dialog.FileName, _items, _directory.Session);
            LogBestEffort(_directory.Session, "导出 CSV", dialog.FileName, "成功", $"{_items.Count} 个对象 / {_loadedScopeDescription}");
            StatusText.Text = $"已导出 {_items.Count} 个对象至 {dialog.FileName}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task WriteAndRefreshAsync(DirectorySession session, string action, string target, Func<Task> write, Func<Task> refresh)
    {
        // Fail closed before mutation if the local request record cannot be persisted.
        AddAudit(session, action, target, "已请求", "等待远端结果");
        try { await write(); }
        catch (Exception ex)
        {
            LogBestEffort(session, action, target, "失败/待核实", ErrorMessage(ex));
            throw new InvalidOperationException("操作失败或结果未确认。连接中断/超时时，远端可能已经执行；请刷新核实后再决定是否重试。\n" + ErrorMessage(ex), ex);
        }
        var logged = LogBestEffort(session, action, target, "成功", "服务器已返回成功");
        try { await refresh(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("操作已成功，但刷新失败；请勿重复提交写操作。\n" + ErrorMessage(ex), ex);
        }
        StatusText.Text = $"{action}成功，列表已刷新。" + (logged ? "" : "警告：成功结果未能写入本地日志。");
    }

    private bool Confirm(DirectorySession session, string action, string target, string warning) =>
        MessageBox.Show(this, $"[真实远端写操作]\n服务器：{session.Server}\n操作者：{session.Identity}\n\n{target}\n\n{warning}\n\n确定执行“{action}”？",
            "确认远程操作", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void AddAudit(DirectorySession session, string action, string target, string result, string message)
    {
        var entry = new AuditEntry(DateTimeOffset.Now, "真实", session.Identity, session.Server, action, target, result, message);
        _audit.Append(entry);
    }

    private bool LogBestEffort(DirectorySession session, string action, string target, string result, string message)
    {
        try { AddAudit(session, action, target, result, message); return true; }
        catch (Exception ex)
        {
            MessageBox.Show(this, "本地日志写入失败。当前操作结果：" + result + "\n" + ex.Message, "日志警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = "正在处理远程请求，请稍候…";
        try { await operation(); }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ConnectButton.IsEnabled = DirectoryPanel.IsEnabled = !busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }

    private void ShowError(Exception ex)
    {
        var message = ErrorMessage(ex);
        StatusText.Text = "请求未完成：" + message.Replace('\n', ' ');
        MessageBox.Show(this, message, "请求未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string ErrorMessage(Exception ex) => ex switch
    {
        DirectoryOperationException ldap when !string.IsNullOrWhiteSpace(ldap.Response?.ErrorMessage) => ex.Message + "\n" + ldap.Response.ErrorMessage,
        LdapException ldap => $"LDAP 错误 {ldap.ErrorCode}：{ldap.Message}\n请检查服务器名、证书、网络、域信任及账号权限。",
        _ => ex.Message
    };

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "请求正在执行，请等待返回结果，避免无法确认远端写操作状态。", "请稍候");
            return;
        }
        _directory.Dispose();
    }

    private sealed class DisconnectedDirectoryService : IDirectoryService
    {
        public DirectorySession Session { get; } = new("", "", "", false, false, true);
        public void Dispose() { }
        private static Task<T> NotConnected<T>() => Task.FromException<T>(new InvalidOperationException("请先连接 AD 域。"));
        private static Task NotConnected() => Task.FromException(new InvalidOperationException("请先连接 AD 域。"));
        public Task<QueryResult> QueryAsync(DirectoryKind kind, string search, DirectoryQueryScope? scope = null) => NotConnected<QueryResult>();
        public Task ExecuteAsync(DirectoryItem target, AccountAction action, SecureString? password = null, bool mustChangePassword = true) => NotConnected();
        public Task<ContainerResult> GetContainersAsync() => NotConnected<ContainerResult>();
        public Task CreateUserAsync(NewUserSpec spec, SecureString password) => NotConnected();
        public Task CreateComputerAsync(NewComputerSpec spec) => NotConnected();
        public Task CreateGroupAsync(NewGroupSpec spec) => NotConnected();
        public Task<QueryResult> GetGroupUsersAsync(DirectoryItem group) => NotConnected<QueryResult>();
        public Task ChangeGroupUserAsync(DirectoryItem group, DirectoryItem user, bool add) => NotConnected();
        public Task UpdateComputerAsync(DirectoryItem computer, ComputerChanges changes) => NotConnected();
        public Task DeleteComputerAsync(DirectoryItem computer) => NotConnected();
    }
}



using System.ComponentModel;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DomainDesk.Services;

namespace DomainDesk.Dialogs;

public class DirectoryForm : Window
{
    protected readonly StackPanel Form = new() { Margin = new Thickness(26) };
    protected readonly TextBlock Error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    protected DirectoryForm(string title, double height = 620)
    {
        Title = title;
        Style = (Style)FindResource(typeof(Window));
        Width = 560;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 60);
        Height = height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = new ScrollViewer { Content = Form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Form.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold });
    }

    protected void Note(string text) => Form.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 13, 0, 3), FontSize = 12 });
    protected void Field(string caption, Control input)
    {
        Form.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 12, 0, 6), FontSize = 12 });
        Form.Children.Add(input);
    }
    protected void Finish(string label, Action submit)
    {
        Form.Children.Add(Error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true });
        var button = new Button { Content = label, IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        button.Click += (_, _) => { try { submit(); } catch (ArgumentException ex) { Error.Text = ex.Message; } };
        buttons.Children.Add(button);
        Form.Children.Add(buttons);
    }
}

public sealed class NewUserDialog : DirectoryForm
{
    public NewUserSpec? Spec { get; private set; }
    public SecureString? Password { get; private set; }
    public NewUserDialog(ContainerResult containers, DirectorySession session, Guid? preferredContainer = null) : base("新建域用户", 830)
    {
        Note($"{(session.IsDemo ? "[演示]" : "[真实域]")} {session.Server}\n先创建停用账号，再设置初始密码，最后按选项启用。中途失败时不会自动删除账号，请刷新核实。");
        var targets = containers.Items.Where(x => x.CanCreateUser).OrderBy(x => x.DistinguishedName).ToArray();
        var container = new ComboBox { Name = "ContainerBox", ItemsSource = targets, DisplayMemberPath = "DistinguishedName", SelectedIndex = -1, Padding = new Thickness(8) };
        container.SelectedItem = targets.FirstOrDefault(x => x.Id == preferredContainer);
        Field("目标 OU / Users 容器（从域内读取）", container);
        if (container.SelectedItem is null) Note("当前目录不能用于默认新建，或已不可见。请明确选择目标 OU / Users 容器；不会自动改选其他目录。");
        if (containers.Truncated) Note("容器超过 5000 项，列表已截断。");
        var name = new TextBox();
        var account = new TextBox();
        var upn = new TextBox();
        var email = new TextBox();
        var department = new TextBox();
        var password = new PasswordBox();
        var confirm = new PasswordBox();
        var enable = new CheckBox { Content = "设置密码成功后启用账号", IsChecked = true };
        var mustChange = new CheckBox { Content = "下次登录必须更改密码", IsChecked = true };
        var suffix = string.Join(".", session.BaseDn.Split(',').Where(x => x.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(x => x[3..]));
        var lastSuggested = "";
        account.TextChanged += (_, _) =>
        {
            if (upn.Text.Length == 0 || upn.Text == lastSuggested) upn.Text = lastSuggested = account.Text.Trim() + "@" + suffix;
        };
        Field("姓名 / 显示名 / CN", name);
        Field("登录名 sAMAccountName（最长 20 字符）", account);
        Field("用户登录名 UPN（可填写域已配置的其他后缀）", upn);
        Field("邮箱（可选）", email);
        Field("部门（可选）", department);
        Field("初始密码", password);
        Field("确认初始密码", confirm);
        Form.Children.Add(enable);
        Form.Children.Add(mustChange);
        Finish("下一步：确认新建", () =>
        {
            if (container.SelectedItem is not DirectoryContainer parent) throw new ArgumentException("请先选择目标容器。");
            var spec = new NewUserSpec(parent, name.Text.Trim(), account.Text.Trim(), upn.Text.Trim(), email.Text.Trim(), department.Text.Trim(), enable.IsChecked == true, mustChange.IsChecked == true);
            DirectoryMutations.ValidateUser(spec);
            using var a = password.SecurePassword;
            using var b = confirm.SecurePassword;
            if (a.Length == 0 || !PasswordDialog.Equal(a, b)) throw new ArgumentException("密码不能为空，两次输入必须一致。");
            Password = a.Copy();
            Password.MakeReadOnly();
            Spec = spec;
            password.Clear();
            confirm.Clear();
            DialogResult = true;
        });
    }
}

public sealed class ComputerEditDialog : DirectoryForm
{
    public ComputerChanges? Changes { get; private set; }
    public ComputerEditDialog(DirectoryItem computer) : base("编辑计算机属性", 495)
    {
        Note($"计算机：{computer.DisplayName}\n真实 AD 账号：{computer.Account}\n{computer.DistinguishedName}\n此处修改 AD 描述与位置，不执行远端主机重命名，不更改机器账号/SPN/DNS。");
        var description = new TextBox { Text = computer.Description };
        var location = new TextBox { Text = computer.Location };
        Field("描述（清空表示移除属性）", description);
        Field("位置（清空表示移除属性）", location);
        Finish("下一步：确认保存", () =>
        {
            var changes = new ComputerChanges(description.Text.Trim(), location.Text.Trim());
            DirectoryMutations.Computer(computer.DistinguishedName, changes);
            Changes = changes;
            DialogResult = true;
        });
    }
}

public sealed class ComputerDeleteDialog : DirectoryForm
{
    public ComputerDeleteDialog(DirectoryItem computer, DirectorySession session) : base("删除 AD 计算机对象", 445)
    {
        Note($"{(session.IsDemo ? "[演示模拟]" : "[真实域删除]")} 服务器：{session.Server}\n{computer.DisplayName}\n{computer.DistinguishedName}\n\n删除会破坏该计算机的域信任关系，可能需要重新加域。本工具不自动恢复，也不会清除 DNS 记录或删除远端主机文件。仅删除无子对象的普通计算机；域控与关键对象禁止删除。");
        var input = new TextBox();
        Field($"请输入计算机名 {computer.DisplayName} 以确认", input);
        Finish("确认删除", () =>
        {
            if (!input.Text.Trim().Equals(computer.DisplayName, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("输入的计算机名不一致。");
            DialogResult = true;
        });
    }
}

public sealed class GroupMembersDialog : Window
{
    private readonly IDirectoryService _service;
    private readonly DirectoryItem _group;
    private readonly Func<DirectoryItem, bool, Func<Task>, Task> _change;
    private readonly DataGrid _members = GridForUsers();
    private readonly DataGrid _users = GridForUsers();
    private readonly TextBox _search = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _add = new() { Content = "添加选中用户 →" };
    private readonly Button _remove = new() { Content = "移除选中成员", Foreground = Brushes.Firebrick };
    private readonly Grid _body = new();
    private bool _working;

    public GroupMembersDialog(IDirectoryService service, DirectoryItem group, Func<DirectoryItem, bool, Func<Task>, Task> change)
    {
        _service = service;
        _group = group;
        _change = change;
        Style = (Style)FindResource(typeof(Window));
        Title = $"组成员 · {group.Name}";
        Width = 1080;
        Height = 730;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 60);
        MinWidth = 850;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var heading = new TextBlock { Text = $"{group.Name} / 直接用户成员\n{service.Session.Server} · {(service.Session.AllowWrites ? "允许修改，每次确认" : "只读")}\n{group.DistinguishedName}\n仅显示本域直接用户成员，不含主组成员、嵌套组、计算机及外域成员；移除仅解除此组关系，不删除用户。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_body);
        _body.ColumnDefinitions.Add(new ColumnDefinition());
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        _body.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel();
        var right = new DockPanel();
        Grid.SetColumn(right, 2);
        _body.Children.Add(left);
        _body.Children.Add(right);
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var find = new Button { Content = "查找用户", Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(find, Dock.Right);
        toolbar.Children.Add(find);
        toolbar.Children.Add(_search);
        DockPanel.SetDock(toolbar, Dock.Top);
        left.Children.Add(toolbar);
        _add.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(_add, Dock.Bottom);
        left.Children.Add(_add);
        left.Children.Add(_users);
        var refresh = new Button { Content = "刷新直接成员", Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(refresh, Dock.Top);
        right.Children.Add(refresh);
        _remove.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(_remove, Dock.Bottom);
        right.Children.Add(_remove);
        right.Children.Add(_members);
        find.Click += async (_, _) => await Busy(SearchAsync);
        _search.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await Busy(SearchAsync); } };
        refresh.Click += async (_, _) => await Busy(RefreshMembersAsync);
        _add.Click += async (_, _) => { if (_users.SelectedItem is DirectoryItem user) await Busy(() => _change(user, true, RefreshMembersAsync)); };
        _remove.Click += async (_, _) => { if (_members.SelectedItem is DirectoryItem user) await Busy(() => _change(user, false, RefreshMembersAsync)); };
        _users.SelectionChanged += (_, _) => UpdateButtons();
        _members.SelectionChanged += (_, _) => UpdateButtons();
        Loaded += async (_, _) => await Busy(async () => { await SearchAsync(); await RefreshMembersAsync(); });
        Closing += (_, e) => { if (_working) { e.Cancel = true; _status.Text = "请求进行中，请等待结果。"; } };
        UpdateButtons();
    }

    private async Task SearchAsync()
    {
        _users.ItemsSource = null;
        var result = await _service.QueryAsync(DirectoryKind.Users, _search.Text);
        _users.ItemsSource = result.Items;
        _status.Text = $"找到 {result.Items.Count} 个用户{(result.Truncated ? "（已截断，请缩小关键词范围）" : "")}";
    }
    private async Task RefreshMembersAsync()
    {
        _members.ItemsSource = null;
        var result = await _service.GetGroupUsersAsync(_group);
        _members.ItemsSource = result.Items;
        _status.Text = $"当前 {result.Items.Count} 个直接用户成员{(result.Truncated ? "（超过 5000，已截断）" : "")}。左侧查找用户最多返回 5000 条，可输入关键词缩小范围。";
    }
    private async Task Busy(Func<Task> work)
    {
        if (_working) return;
        _working = true;
        _body.IsEnabled = false;
        _status.Text = "正在请求域控制器…";
        try { await work(); }
        catch (Exception ex) { _status.Text = "请求未完成：" + ex.Message; MessageBox.Show(this, ex.Message, "组成员操作", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _working = false; _body.IsEnabled = true; UpdateButtons(); }
    }
    private void UpdateButtons()
    {
        _add.IsEnabled = !_working && _service.Session.AllowWrites && _users.SelectedItem is DirectoryItem;
        _remove.IsEnabled = !_working && _service.Session.AllowWrites && _members.SelectedItem is DirectoryItem;
    }
    private static DataGrid GridForUsers()
    {
        var grid = new DataGrid();
        grid.Columns.Add(new DataGridTextColumn { Header = "显示名", Binding = new Binding("DisplayName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "登录名", Binding = new Binding("Account"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        return grid;
    }
}

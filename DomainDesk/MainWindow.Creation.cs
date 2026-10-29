using System.Windows;
using DomainDesk.Dialogs;
using DomainDesk.Services;

namespace DomainDesk;

public partial class MainWindow
{
    private async void NewComputer_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_directory.Session.AllowWrites || _selectedDirectory is null) return;
        ContainerResult? containers = null;
        await RunBusyAsync(async () => containers = await _directory.GetContainersAsync());
        if (containers is null) return;
        var dialog = new NewComputerDialog(containers, _directory.Session, _selectedDirectory.Id) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Spec is not NewComputerSpec spec) return;
        var dn = DirectoryMutations.NewComputerDn(spec);
        if (!Confirm(_directory.Session, "新建计算机", $"{dn}\n实际机器账号：{spec.Account}\n描述：{spec.Description}\n位置：{spec.Location}",
            "只预创建停用的域成员计算机账号，不加域、不重命名远端电脑、不创建 DNS/SPN、不设置可预测机器密码。正式加域由授权管理员另行完成，账号重用可能受到服务器策略限制。")) return;
        await RunBusyAsync(() => WriteAndRefreshAsync(_directory.Session, "新建计算机", dn,
            () => _directory.CreateComputerAsync(spec), () => RefreshAfterCreationAsync(spec.Container, dn)));
    }

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_directory.Session.AllowWrites || _selectedDirectory is null) return;
        ContainerResult? containers = null;
        await RunBusyAsync(async () => containers = await _directory.GetContainersAsync());
        if (containers is null) return;
        var dialog = new NewGroupDialog(containers, _directory.Session, _selectedDirectory.Id) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Spec is not NewGroupSpec spec) return;
        var dn = DirectoryMutations.NewGroupDn(spec);
        if (!Confirm(_directory.Session, "新建组", $"{dn}\n组账号：{spec.Account}\n{DirectoryMutations.GroupScopeLabel(spec.Scope)} / {(spec.SecurityEnabled ? "安全组" : "通讯组")}\n描述：{spec.Description}",
            "将创建空组，不添加成员、不修改其他对象的成员关系、不自动授予资源权限。通讯组不会自动开通邮件服务。")) return;
        await RunBusyAsync(() => WriteAndRefreshAsync(_directory.Session, "新建组", dn,
            () => _directory.CreateGroupAsync(spec), () => RefreshAfterCreationAsync(spec.Container, dn)));
    }

    private async Task RefreshAfterCreationAsync(DirectoryContainer parent, string dn)
    {
        _settingTree = true;
        try { _kind = DirectoryKind.All; ObjectTypeBox.SelectedIndex = 0; OuFilterBox.Clear(); }
        finally { _settingTree = false; }
        SearchBox.Clear();
        await RefreshDirectoryTreeAsync(parent.Id);
        if (_selectedDirectory is null) throw new InvalidOperationException("目标目录已不可见，请刷新并查找已创建对象。");
        await LoadDirectoryAsync();
        var created = _items.FirstOrDefault(x => x.DistinguishedName.Equals(dn, StringComparison.OrdinalIgnoreCase));
        ObjectsGrid.SelectedItem = created;
        if (created is null) throw new InvalidOperationException($"新对象未出现在当前返回结果中，可能因结果截断或读取权限变化。请按名称查询后核实，不要重复新建。\n{dn}");
        ObjectsGrid.ScrollIntoView(created);
    }
}

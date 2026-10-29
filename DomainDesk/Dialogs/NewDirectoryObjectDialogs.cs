using System.Windows;
using System.Windows.Controls;
using DomainDesk.Services;

namespace DomainDesk.Dialogs;

public abstract class NewDirectoryObjectDialog : DirectoryForm
{
    protected NewDirectoryObjectDialog(string title, ContainerResult containers, DirectorySession session, Guid? preferredContainer,
        Func<DirectoryContainer, bool> allowed, string targetLabel, double height) : base(title, height)
    {
        Note($"{(session.IsDemo ? "[演示模拟]" : "[真实域]")} {session.Server}");
        var targets = containers.Items.Where(allowed).OrderBy(x => x.DistinguishedName).ToArray();
        TargetBox = new ComboBox { Name = "ContainerBox", ItemsSource = targets, DisplayMemberPath = "DistinguishedName", Padding = new Thickness(8), SelectedIndex = -1 };
        TargetBox.SelectedItem = targets.FirstOrDefault(x => x.Id == preferredContainer);
        Field(targetLabel, TargetBox);
        if (TargetBox.SelectedItem is null) Note("当前目录不支持默认新建，或已不可见。请明确选择目标目录；不会自动改选其他目录。");
        if (containers.Truncated) Note("目录超过 5000 项，列表已截断；未列出的目录不可作为新建目标。");
    }

    protected ComboBox TargetBox { get; }
    protected DirectoryContainer Target => TargetBox.SelectedItem as DirectoryContainer ?? throw new ArgumentException("请先选择目标目录。");
}

public sealed class NewComputerDialog : NewDirectoryObjectDialog
{
    public NewComputerSpec? Spec { get; private set; }

    public NewComputerDialog(ContainerResult containers, DirectorySession session, Guid? preferredContainer = null)
        : base("新建 AD 计算机", containers, session, preferredContainer, x => x.CanCreateComputer, "目标 OU / Computers 容器", 680)
    {
        Note("仅预创建停用的域成员计算机账号，不会将远端电脑加入域、重命名电脑或创建 DNS/SPN。之后需由授权管理员通过正式加域流程建立机器密码与域信任；服务器可能限制预创建账号的重用。");
        var name = new TextBox { Name = "ComputerNameBox", MaxLength = 15 };
        var account = new TextBox { Name = "ComputerAccountBox", IsReadOnly = true };
        name.TextChanged += (_, _) => account.Text = name.Text.Trim().Length == 0 ? "" : name.Text.Trim() + "$";
        var description = new TextBox { Name = "DescriptionBox", MaxLength = 1024 };
        var location = new TextBox { Name = "LocationBox", MaxLength = 1024 };
        Field("计算机名（1–15 个字母、数字或连字符；不含 $ / 域名后缀）", name);
        Field("实际机器账号（自动添加 $，无需手工输入）", account);
        Field("描述（可选）", description);
        Field("位置（可选）", location);
        Finish("下一步：确认新建", () =>
        {
            var spec = new NewComputerSpec(Target, name.Text.Trim(), description.Text.Trim(), location.Text.Trim());
            DirectoryMutations.ValidateComputer(spec);
            DirectoryMutations.ValidateCreationParent(session, spec.Container);
            Spec = spec;
            DialogResult = true;
        });
    }
}

public sealed class NewGroupDialog : NewDirectoryObjectDialog
{
    public NewGroupSpec? Spec { get; private set; }

    public NewGroupDialog(ContainerResult containers, DirectorySession session, Guid? preferredContainer = null)
        : base("新建域用户组", containers, session, preferredContainer, x => x.CanCreateGroup, "目标 OU / Users 容器（不含 Builtin）", 735)
    {
        Note("新建空组，不自动添加成员或授予资源权限。安全组可用于权限分配；通讯组不用于访问控制，也不会自动配置邮件服务。组作用域最终由域功能级别及服务器策略验证。");
        var name = new TextBox { Name = "GroupNameBox", MaxLength = 64 };
        var account = new TextBox { Name = "GroupAccountBox", MaxLength = 20 };
        var lastSuggested = "";
        name.TextChanged += (_, _) =>
        {
            if (account.Text.Length == 0 || account.Text == lastSuggested)
            {
                var suggestion = name.Text.Trim();
                account.Text = lastSuggested = suggestion.Length <= 20 ? suggestion : "";
            }
        };
        var scope = new ComboBox { Name = "GroupScopeBox", Padding = new Thickness(8), SelectedIndex = 0 };
        foreach (var value in Enum.GetValues<AdGroupScope>()) scope.Items.Add(new ComboBoxItem { Content = DirectoryMutations.GroupScopeLabel(value), Tag = value });
        scope.SelectedIndex = 0;
        var category = new ComboBox { Name = "GroupCategoryBox", Padding = new Thickness(8) };
        category.Items.Add(new ComboBoxItem { Content = "安全组", Tag = true });
        category.Items.Add(new ComboBoxItem { Content = "通讯组", Tag = false });
        category.SelectedIndex = 0;
        var description = new TextBox { Name = "DescriptionBox", MaxLength = 1024 };
        Field("组名称 / CN（最长 64 字符）", name);
        Field("组账号 sAMAccountName（兼容长度：最长 20 字符）", account);
        Field("组作用域", scope);
        Field("组类型", category);
        Field("描述（可选）", description);
        Finish("下一步：确认新建", () =>
        {
            if (scope.SelectedItem is not ComboBoxItem { Tag: AdGroupScope selectedScope } || category.SelectedItem is not ComboBoxItem { Tag: bool security })
                throw new ArgumentException("请选择组作用域和类型。");
            var spec = new NewGroupSpec(Target, name.Text.Trim(), account.Text.Trim(), selectedScope, security, description.Text.Trim());
            DirectoryMutations.ValidateGroup(spec);
            DirectoryMutations.ValidateCreationParent(session, spec.Container);
            Spec = spec;
            DialogResult = true;
        });
    }
}

using System.DirectoryServices.Protocols;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DomainDesk;
using DomainDesk.Dialogs;
using DomainDesk.Services;

internal static partial class Program
{
    private static async Task TestObjectCreationAsync()
    {
        using var service = new DemoDirectoryService();
        var containers = await service.GetContainersAsync();
        var parent = containers.Items.Single(x => x.Name == "待入职");
        var root = containers.Items.Single(x => x.IsDomainRoot);
        var builtin = containers.Items.Single(x => x.Name == "Builtin");
        var computers = containers.Items.Single(x => x.Name == "Computers");
        var users = containers.Items.Single(x => x.Name == "Users");
        Assert(computers.CanCreateComputer && !computers.CanCreateGroup && users.CanCreateGroup && !users.CanCreateComputer, "Standard creation targets differ by object type");
        Assert(!root.CanCreateComputer && !root.CanCreateGroup && !builtin.CanCreateComputer && !builtin.CanCreateGroup, "Domain root and Builtin cannot silently become creation targets");
        Assert(!(computers with { ObjectClass = "unsupported" }).CanCreateComputer && !(users with { ObjectClass = "builtinDomain" }).CanCreateGroup, "Creation target requires real compatible object class");
        var pc = new NewComputerSpec(parent, "PRE-PC01", "Staged computer", "Room 2");
        var pcAdd = DirectoryMutations.CreateComputer(pc);
        string Attribute(AddRequest request, string name) => (string)request.Attributes.Cast<DirectoryAttribute>().Single(x => x.Name == name)[0];
        Assert(pcAdd.DistinguishedName == "CN=PRE-PC01," + parent.DistinguishedName && Attribute(pcAdd, "sAMAccountName") == "PRE-PC01$", "Computer Add uses selected parent and exactly one automatic dollar suffix");
        Assert(Attribute(pcAdd, "userAccountControl") == "4098" && pcAdd.Attributes.Cast<DirectoryAttribute>().Single(x => x.Name == "objectClass").GetValues(typeof(string)).Cast<string>().Last() == "computer", "Computer is a disabled workstation trust account, not user or domain controller");
        Assert(Attribute(pcAdd, "description") == pc.Description && Attribute(pcAdd, "location") == pc.Location, "Computer optional attributes mapped correctly");
        Assert(!pcAdd.Attributes.Cast<DirectoryAttribute>().Any(x => x.Name.Contains("pwd", StringComparison.OrdinalIgnoreCase) || x.Name is "dNSHostName" or "servicePrincipalName" or "msDS-AllowedToDelegateTo"), "Computer creation sets no predictable password, DNS/SPN or delegation");
        foreach (var bad in new[] { "", "12345", "abcdefghijklmnop", "bad.name", "PC$", "-PC", "PC-", "PC_1", "PC 1", "PC\n1", "计算机" })
            Reject(() => DirectoryMutations.CreateComputer(pc with { Name = bad }), "Invalid staged computer name rejected: " + bad);
        Reject(() => DirectoryMutations.CreateComputer(pc with { Description = new string('x', 1025) }), "Computer description limit enforced");
        Reject(() => DirectoryMutations.CreateComputer(pc with { Location = "one\ntwo" }), "Computer location controls rejected");
        Reject(() => DirectoryMutations.CreateComputer(pc with { Container = builtin }), "Builtin cannot host a new computer");
        Reject(() => DirectoryMutations.CreateComputer(pc with { Container = root }), "Computer root target requires explicit alternative");
        Reject(() => DirectoryMutations.CreateComputer(pc with { Container = users }), "Users container not offered as computer target");
        var group = new NewGroupSpec(parent, "#Ops, +Group", "Ops Group", AdGroupScope.Global, true, "Created empty");
        foreach (var scope in Enum.GetValues<AdGroupScope>())
        foreach (var security in new[] { true, false })
        {
            var spec = group with { Scope = scope, SecurityEnabled = security };
            var add = DirectoryMutations.CreateGroup(spec);
            var expected = (int)scope | (security ? int.MinValue : 0);
            Assert(Attribute(add, "groupType") == expected.ToString(System.Globalization.CultureInfo.InvariantCulture), "Signed groupType encoding: " + scope + " / " + security);
            Assert(!add.Attributes.Cast<DirectoryAttribute>().Any(x => x.Name is "member" or "memberOf" or "userAccountControl"), "New group has no members, memberships or account-control flags");
        }
        var groupAdd = DirectoryMutations.CreateGroup(group);
        Assert(groupAdd.DistinguishedName == @"CN=\#Ops\, \+Group," + parent.DistinguishedName && Attribute(groupAdd, "sAMAccountName") == "Ops Group", "Group CN is escaped separately from domain account name");
        foreach (var bad in new[] { "", "group$", "a/b", "a\\b", "a*", "a\0b", " account", "account ", "a.", new string('x', 21) })
            Reject(() => DirectoryMutations.CreateGroup(group with { Account = bad }), "Invalid group account rejected");
        Reject(() => DirectoryMutations.CreateGroup(group with { Name = new string('x', 65) }), "Group CN length enforced");
        Reject(() => DirectoryMutations.CreateGroup(group with { Description = new string('x', 1025) }), "Group description length enforced");
        foreach (var value in new[] { 0, 1, 6, 14, 256 })
            Reject(() => DirectoryMutations.CreateGroup(group with { Scope = (AdGroupScope)value }), "Builtin or combined group scope cannot be submitted");
        Reject(() => DirectoryMutations.CreateGroup(group with { Container = builtin }), "Builtin group creation forbidden");
        Reject(() => DirectoryMutations.CreateGroup(group with { Container = root }), "Group root target requires explicit alternative");

        await service.CreateComputerAsync(pc);
        var createdPc = (await service.QueryAsync(DirectoryKind.Computers, "", new(parent, false))).Items.Single();
        Assert(createdPc.Name == pc.Name && createdPc.Account == pc.Account && !createdPc.Enabled && createdPc.CanManageComputer && createdPc.Location == pc.Location, "Demo pre-staged computer has accurate disabled state, attributes and operations");
        await Deny(() => service.CreateComputerAsync(pc with { Name = pc.Name.ToLowerInvariant() }), "Computer account duplicate rejected case-insensitively");
        await Deny(() => service.CreateComputerAsync(pc with { Name = "WS-ENG-008" }), "Computer duplicate account detected across other directories");
        await Deny(() => service.CreateGroupAsync(group with { Name = pc.Name, Account = "OtherAccount" }), "Same target DN collision rejected even across object types");
        await Deny(() => service.CreateGroupAsync(group with { Account = "chen.sy" }), "Group account cannot collide with an existing user");
        foreach (var scope in Enum.GetValues<AdGroupScope>())
        foreach (var security in new[] { true, false })
        {
            var account = "New-" + scope + (security ? "-S" : "-D");
            var spec = group with { Name = account, Account = account, Scope = scope, SecurityEnabled = security };
            await service.CreateGroupAsync(spec);
            var created = (await service.QueryAsync(DirectoryKind.Groups, account, new(parent, false))).Items.Single();
            Assert(created.GroupType == spec.GroupType && created.GroupTypeLabel.Contains(security ? "安全组" : "通讯组") && created.GroupTypeLabel.Contains(DirectoryMutations.GroupScopeLabel(scope)), "Created group retains scope and security category");
            Assert((await service.GetGroupUsersAsync(created)).Items.Count == 0 && created.MemberOf.Count == 0, "Created group is empty and not added to another group");
            await Deny(() => service.CreateGroupAsync(spec with { Account = account.ToUpperInvariant() }), "Group duplicate rejected without overwriting original");
        }
        var newGroup = (await service.QueryAsync(DirectoryKind.Groups, "New-Global-S", new(parent, false))).Items.Single();
        var user = (await service.QueryAsync(DirectoryKind.Users, "chen.sy")).Items.Single();
        await service.ChangeGroupUserAsync(newGroup, user, true);
        Assert((await service.GetGroupUsersAsync(newGroup)).Items.Single().Id == user.Id, "New group integrates with existing member management");
        await service.ChangeGroupUserAsync(newGroup, user, false);
        Assert((await service.GetGroupUsersAsync(newGroup)).Items.Count == 0, "New group member can be removed without deleting user");
        await Deny(() => service.CreateComputerAsync(pc with { Name = "MOVED-PC", Container = parent with { DistinguishedName = "OU=Moved," + service.Session.BaseDn } }), "Moved computer parent snapshot rejected");
        await Deny(() => service.CreateGroupAsync(group with { Container = parent with { Id = Guid.NewGuid() } }), "Stale group parent GUID rejected");
        Reject(() => DirectoryMutations.ValidateCreationParent(service.Session, parent with { DistinguishedName = "OU=Other,DC=evil,DC=test" }), "Creation cannot target a foreign domain");
        Reject(() => DirectoryMutations.ValidateCreationParent(service.Session, parent with { Id = Guid.Empty }), "Creation parent must have a GUID");
        using var ro = new DemoDirectoryService(allowWrites: false);
        await Deny(() => ro.CreateComputerAsync(pc), "Readonly service rejects computer creation before parent lookup");
        await Deny(() => ro.CreateGroupAsync(group), "Readonly service rejects group creation before parent lookup");
        // Construct only, never bind. Invalid/readonly requests must fail before any LDAP operation.
        var constructor = typeof(LdapDirectoryService).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        using var ldap = (LdapDirectoryService)constructor.Invoke([new LdapConnection(new LdapDirectoryIdentifier("do-not-contact.invalid")), service.Session with { AllowWrites = false, IsDemo = false }]);
        await Deny(() => ldap.CreateComputerAsync(pc), "LDAP readonly guard precedes computer networking");
        await Deny(() => ldap.CreateGroupAsync(group), "LDAP readonly guard precedes group networking");
        using var writable = (LdapDirectoryService)constructor.Invoke([new LdapConnection(new LdapDirectoryIdentifier("do-not-contact.invalid")), service.Session with { IsDemo = false }]);
        Reject(() => writable.CreateComputerAsync(pc with { Container = parent with { DistinguishedName = "OU=Other,DC=evil,DC=test" } }), "LDAP computer foreign target rejected before networking");
        Reject(() => writable.CreateGroupAsync(group with { Scope = (AdGroupScope)1 }), "LDAP group builtin scope rejected before networking");
    }

    private static void TestCreationWindows(MainWindow main, DataGrid objects, FieldInfo directoryField)
    {
        ((IDirectoryService)directoryField.GetValue(main)!).Dispose();
        var service = new DemoDirectoryService();
        directoryField.SetValue(main, service);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        ((Task)typeof(MainWindow).GetMethod("LoadDirectoryAsync", flags)!.Invoke(main, null)!).GetAwaiter().GetResult();
        var containers = service.GetContainersAsync().GetAwaiter().GetResult();
        var parent = containers.Items.Single(x => x.Name == "待入职");
        var root = containers.Items.Single(x => x.IsDomainRoot);
        var pcDialog = new NewComputerDialog(containers, service.Session, parent.Id);
        var groupDialog = new NewGroupDialog(containers, service.Session, parent.Id);
        T Control<T>(Window window, string name) where T : FrameworkElement => Descendants((DependencyObject)window.Content).OfType<T>().Single(x => x.Name == name);
        Assert(Control<ComboBox>(pcDialog, "ContainerBox").SelectedItem is DirectoryContainer p && p.Id == parent.Id, "Computer dialog defaults to selected OU GUID");
        Assert(Control<ComboBox>(groupDialog, "ContainerBox").SelectedItem is DirectoryContainer g && g.Id == parent.Id, "Group dialog defaults to selected OU GUID");
        Assert(Control<ComboBox>(pcDialog, "ContainerBox").Items.Cast<DirectoryContainer>().All(x => x.CanCreateComputer) && Control<ComboBox>(groupDialog, "ContainerBox").Items.Cast<DirectoryContainer>().All(x => x.CanCreateGroup), "New dialogs show only compatible targets");
        Control<TextBox>(pcDialog, "ComputerNameBox").Text = "PREVIEW-PC";
        Assert(Control<TextBox>(pcDialog, "ComputerAccountBox").Text == "PREVIEW-PC$" && Control<TextBox>(pcDialog, "ComputerAccountBox").IsReadOnly, "Computer dialog previews automatic dollar suffix read-only");
        Control<TextBox>(groupDialog, "GroupNameBox").Text = "New-Operations";
        Assert(Control<TextBox>(groupDialog, "GroupAccountBox").Text == "New-Operations", "Group dialog suggests account name");
        Control<TextBox>(groupDialog, "GroupAccountBox").Text = "custom.ops";
        Control<TextBox>(groupDialog, "GroupNameBox").Text = "Changed Group";
        Assert(Control<TextBox>(groupDialog, "GroupAccountBox").Text == "custom.ops", "Editing group name does not overwrite manually entered account");
        Assert(Control<ComboBox>(groupDialog, "GroupScopeBox").Items.Count == 3 && Control<ComboBox>(groupDialog, "GroupCategoryBox").Items.Count == 2, "Group dialog exposes three scopes and two categories");
        Render(pcDialog, "new-computer-dialog.png", 560, 680);
        Render(groupDialog, "new-group-dialog.png", 560, 735);
        pcDialog.Close(); groupDialog.Close();
        foreach (var dialog in new Window[] { new NewComputerDialog(containers, service.Session, root.Id), new NewGroupDialog(containers, service.Session, root.Id) })
        {
            Assert(Control<ComboBox>(dialog, "ContainerBox").SelectedIndex == -1, "Root does not silently choose another creation target");
            Descendants((DependencyObject)dialog.Content).OfType<Button>().Single(x => x.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(Descendants((DependencyObject)dialog.Content).OfType<TextBlock>().Any(x => x.Text == "请先选择目标目录。"), "Submitting an unselected creation target shows validation error without closing form");
            dialog.Close();
        }
        var defaultComputers = new NewComputerDialog(containers, service.Session, containers.Items.Single(x => x.Name == "Computers").Id);
        Assert(Control<ComboBox>(defaultComputers, "ContainerBox").SelectedItem is DirectoryContainer { Name: "Computers" }, "Standard Computers container can be the explicit default target");
        defaultComputers.Close();
        var pc = new NewComputerSpec(parent, "UI-STAGED-PC", "Created in demo", "Office");
        var group = new NewGroupSpec(parent, "UI-Operations", "UI-Operations", AdGroupScope.DomainLocal, true, "Created in demo");
        var types = (ComboBox)main.FindName("ObjectTypeBox");
        var search = (TextBox)main.FindName("SearchBox");
        void Refresh(string dn) => ((Task)typeof(MainWindow).GetMethod("RefreshAfterCreationAsync", flags)!.Invoke(main, [parent, dn])!).GetAwaiter().GetResult();
        types.SelectedIndex = 2;
        search.Text = "unrelated-old-filter";
        ((TextBox)main.FindName("OuFilterBox")).Text = "技术研发部";
        service.CreateComputerAsync(pc).GetAwaiter().GetResult();
        Refresh(DirectoryMutations.NewComputerDn(pc));
        Assert(types.SelectedIndex == 0 && search.Text == "" && objects.SelectedItem is DirectoryItem { Kind: DirectoryKind.Computers, Enabled: false } createdPc && createdPc.Account == pc.Account, "Computer creation clears hiding filters and selects the exact new disabled object");
        Assert(((TextBox)main.FindName("OuFilterBox")).Text == "", "Creation clears tree filter so chosen target directory remains visible");
        Assert(((TextBlock)main.FindName("ScopeTitle")).Text == parent.Name && ((Button)main.FindName("EditComputerButton")).IsEnabled, "Computer creation refresh remains in chosen directory with correct actions");
        service.CreateGroupAsync(group).GetAwaiter().GetResult();
        Refresh(DirectoryMutations.NewGroupDn(group));
        Assert(objects.Items.Count == 2 && objects.SelectedItem is DirectoryItem { Kind: DirectoryKind.Groups } createdGroup && createdGroup.GroupType == group.GroupType && ((Button)main.FindName("MembersButton")).IsEnabled, "Group creation refresh selects new group and enables member management");
        Assert(((TextBlock)main.FindName("DetailType")).Text.Contains("域本地 / 安全组"), "Group details expose actual category and scope after creation");
        Render(main, "creation-toolbar-1390.png");
        main.Width = 1920; Render(main, "creation-toolbar-1920.png", 1920, 1080);
        main.Width = 1120; Render(main, "creation-toolbar-1120.png", 1120, 740);
        main.Width = 1390;
    }
}

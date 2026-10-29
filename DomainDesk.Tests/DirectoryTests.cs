using System.DirectoryServices.Protocols;
using System.Reflection;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using DomainDesk;
using DomainDesk.Dialogs;
using DomainDesk.Services;

internal static partial class Program
{
    private static void TestDirectoryNamesAndTree()
    {
        const string domain = "DC=example,DC=test";
        var escaped = @"CN=Last\, First,OU=Dev\, Team," + domain;
        Assert(DistinguishedNames.Parts(escaped).Length == 4, "Escaped DN commas do not split components");
        Assert(DistinguishedNames.Parent(escaped) == @"OU=Dev\, Team," + domain, "Parent retains escaped OU name");
        Assert(DistinguishedNames.Name(escaped) == "Last, First", "Display name decodes escaped comma");
        Assert(DistinguishedNames.Name(@"OU=Space\ ," + domain) == "Space ", "Escaped trailing space preserved");
        Assert(DistinguishedNames.Name(@"OU=Back\\slash," + domain) == @"Back\slash", "Escaped backslash decoded");
        Assert(DistinguishedNames.Name(@"OU=\E6\8A\80\E6\9C\AF," + domain) == "技术", "Hex-escaped UTF8 DN decoded");
        Assert(DistinguishedNames.IsWithin(escaped, domain.ToUpperInvariant()), "Scope suffix comparison case insensitive");
        Assert(!DistinguishedNames.IsWithin("OU=DevOps," + domain, "OU=Dev," + domain), "Scope suffix respects RDN boundaries");
        Assert(!DistinguishedNames.IsWithin("OU=Other,DC=notexample,DC=test", domain), "Similar domain suffix rejected");
        Assert(!DistinguishedNames.IsWithin(escaped, ""), "Empty domain rejected");
        Assert(DistinguishedNames.Breadcrumb("OU=Team,OU=People," + domain, domain) == "example.test  /  People  /  Team", "Breadcrumb ordered root to leaf");
        var root = new DirectoryContainer(Guid.NewGuid(), domain, true);
        var ou = new DirectoryContainer(Guid.NewGuid(), "OU=People," + domain);
        var child = new DirectoryContainer(Guid.NewGuid(), "OU=Team," + ou.DistinguishedName);
        var orphan = new DirectoryContainer(Guid.NewGuid(), "OU=Child,OU=Hidden," + domain);
        var tree = DirectoryNode.Build(new([root, ou, child, orphan], true), domain);
        Assert(tree.IsExpanded && tree.Children.All(x => !x.IsExpanded), "Compact normal tree expands only domain root by default");
        Assert(tree.Children.Single(x => x.Container?.Id == ou.Id).Children.Single().Container == child, "Tree follows real OU parent hierarchy");
        var placeholder = tree.Children.Single(x => x.Container is null);
        Assert(placeholder.Children.Single().Container == orphan && placeholder.Name.Contains("未列出"), "Missing parent is explicit non-queryable placeholder");
        var selected = tree.All().Single(x => x.Container == child);
        selected.IsSelected = true;
        tree.Filter("Team");
        Assert(selected.IsVisible && tree.IsVisible && !placeholder.IsVisible, "Tree filter retains ancestors and hides unrelated nodes");
        Assert(selected.IsSelected, "Filtering tree does not change selected scope");
        tree.Filter("People");
        Assert(selected.IsVisible, "Matching parent keeps its children visible");
        tree.Filter("");
        Assert(tree.All().All(x => x.IsVisible), "Clearing tree filter restores all nodes");
        Assert(tree.Children.Count(x => x.IsLastVisibleSibling) == 1 && tree.Children.Last().IsLastVisibleSibling, "Connector stops at last visible sibling");
        tree.Filter("People");
        Assert(tree.Children.Single(x => x.IsVisible).IsLastVisibleSibling, "Connector updates after tree filtering");
        tree.Filter("");
        var builtin = new DirectoryContainer(Guid.NewGuid(), "CN=Builtin," + domain) { ObjectClass = "builtinDomain" };
        var advanced = new DirectoryContainer(Guid.NewGuid(), "CN=Program Data," + domain) { IsAdvancedViewOnly = true };
        var advancedChild = new DirectoryContainer(Guid.NewGuid(), "CN=Microsoft," + advanced.DistinguishedName);
        var namedSystem = new DirectoryContainer(Guid.NewGuid(), "OU=System," + domain) { ObjectClass = "organizationalUnit" };
        var normal = DirectoryNode.Build(new([root, builtin, advanced, advancedChild, namedSystem], false), domain);
        Assert(normal.All().Count() == 3 && normal.Children.Any(x => x.Container == builtin), "Normal view includes Builtin and removes entire advanced-only branch");
        Assert(normal.Children.Any(x => x.Container == namedSystem && x.IsOu), "Normal view does not blacklist custom OU names such as System");
        Assert(normal.Children.Single(x => x.Container == builtin).TypeLabel == "容器" && !builtin.CanCreateUser, "Builtin has container icon/type and is not a create-user target");
        Assert(normal.TypeLabel == "域" && normal.Children.Single(x => x.IsOu).TypeLabel == "组织单位 (OU)", "Domain and OU have distinct icon classifications and tooltips");
        Assert(DirectoryQueries.ContainerFilter.Contains("(objectClass=builtinDomain)") && DirectoryQueries.ScopeFilter.Contains("(objectClass=builtinDomain)"), "LDAP listing and GUID scope resolution both support Builtin");
        Assert(!root.CanCreateUser && ou.CanCreateUser && new DirectoryContainer(Guid.NewGuid(), "CN=Users," + domain).CanCreateUser, "Create targets limited to OU and Users container, not domain root");
        Assert(!new DirectoryContainer(Guid.NewGuid(), "CN=System," + domain).CanCreateUser, "Arbitrary system containers not user create targets");
        void BadTree(ContainerResult result, string label)
        {
            try { DirectoryNode.Build(result, domain); }
            catch (InvalidOperationException) { Assert(true, label); return; }
            throw new Exception("FAIL: " + label);
        }
        BadTree(new([ou], false), "Tree requires a real domain root");
        BadTree(new([root, ou, ou with { DistinguishedName = "OU=Other," + domain }], false), "Duplicate directory GUID rejected");
        BadTree(new([root, ou, ou with { Id = Guid.NewGuid() }], false), "Duplicate directory DN rejected");
        BadTree(new([root, ou with { Id = Guid.Empty }], false), "Missing directory GUID rejected");
        BadTree(new([root, ou with { DistinguishedName = "OU=Other,DC=evil,DC=test" }], false), "Out-of-domain directory rejected");
    }

    private static async Task TestDirectoryScopesAsync()
    {
        using var service = new DemoDirectoryService();
        var containers = await service.GetContainersAsync();
        var root = containers.Items.Single(x => x.IsDomainRoot);
        var ou = containers.Items.Single(x => x.Name == "技术研发部");
        var scope = new DirectoryQueryScope(ou, true);
        var request = DirectoryQueries.Request(service.Session, DirectoryKind.Users, "*)(objectClass=*)", scope, "objectGUID", "name");
        Assert(request.DistinguishedName == ou.DistinguishedName && request.Scope == SearchScope.Subtree, "Selected OU used as real LDAP search base");
        Assert(request.Attributes.Count == 2 && !request.Filter.ToString()!.Contains("(objectClass=*)"), "Scoped query keeps requested attributes and escapes LDAP search");
        Assert(DirectoryQueries.Request(service.Session, DirectoryKind.Users, "", scope with { IncludeDescendants = false }).Scope == SearchScope.OneLevel, "Direct-only uses LDAP OneLevel");
        Assert(DirectoryQueries.Request(service.Session, DirectoryKind.Groups, "", null).DistinguishedName == service.Session.BaseDn, "Group queries remain domain-wide");
        Assert(DirectoryQueries.Request(service.Session, DirectoryKind.Computers, "", scope).DistinguishedName == ou.DistinguishedName, "Computer query uses selected directory scope");
        Assert(DirectoryQueries.Request(service.Session, DirectoryKind.Groups, "", scope with { IncludeDescendants = false }).Scope == SearchScope.OneLevel, "Group direct-only uses selected directory scope");
        var mixedRequest = DirectoryQueries.Request(service.Session, DirectoryKind.All, "*)(objectClass=*)", scope);
        Assert(mixedRequest.DistinguishedName == ou.DistinguishedName && mixedRequest.Filter.ToString()!.Contains("(objectCategory=group)") && mixedRequest.Filter.ToString()!.Contains("(objectCategory=computer)") && !mixedRequest.Filter.ToString()!.Contains("(objectClass=*)"), "Unified LDAP request includes all concrete types and escapes search within scope");
        Reject(() => DirectoryQueries.Request(service.Session, DirectoryKind.Users, "", scope with { Container = ou with { Id = Guid.Empty } }), "Scoped query requires immutable GUID");
        Reject(() => DirectoryQueries.Request(service.Session, DirectoryKind.Users, "", scope with { Container = ou with { DistinguishedName = "OU=Other,DC=evil,DC=test" } }), "Scoped query rejects foreign DN");
        Assert((await service.QueryAsync(DirectoryKind.Users, "", new(root, true))).Items.Count == 12, "Domain root subtree shows all demo users");
        var mixed = await service.QueryAsync(DirectoryKind.All, "", scope);
        Assert(mixed.Items.Count == 5 && mixed.Items.Select(x => x.Kind).Distinct().Count() == 3 && mixed.Items.All(x => x.Kind != DirectoryKind.All), "Mixed OU includes actual users, groups and computers, never selector pseudo-type");
        Assert((await service.QueryAsync(DirectoryKind.All, "", scope with { IncludeDescendants = false })).Items.Count == 3, "Unified direct-only excludes nested users but includes direct groups and computers");
        Assert((await service.QueryAsync(DirectoryKind.Computers, "", scope)).Items.Count == 1 && (await service.QueryAsync(DirectoryKind.Groups, "", scope)).Items.Count == 1, "Type filters restrict computers and groups to selected OU");
        Assert((await service.QueryAsync(DirectoryKind.All, "All-Staff", scope)).Items.Count == 0, "Mixed scoped search never leaks sibling group");
        Assert((await service.QueryAsync(DirectoryKind.All, "", new(root, true))).Items.Count == 26, "Unified domain query returns each demo object exactly once");
        Assert(LdapFilters.Classify(["top", "person", "user", "COMPUTER"]) == DirectoryKind.Computers && LdapFilters.Classify(["top", "group"]) == DirectoryKind.Groups && LdapFilters.Classify(["top", "person", "user"]) == DirectoryKind.Users, "LDAP concrete types are case insensitive and computer takes precedence over user");
        try { LdapFilters.Classify(["top", "container"]); throw new Exception("FAIL: unknown object class accepted"); }
        catch (InvalidOperationException) { Assert(true, "Unknown concrete type fails closed"); }
        Assert((await service.QueryAsync(DirectoryKind.Users, "", new(root, false))).Items.Count == 0, "Root direct-only does not silently include descendants");
        var descendants = await service.QueryAsync(DirectoryKind.Users, "", scope);
        var direct = await service.QueryAsync(DirectoryKind.Users, "", scope with { IncludeDescendants = false });
        Assert(descendants.Items.Count == 3 && direct.Items.Count == 1, "Nested OU subtree and direct-only produce different accurate counts");
        Assert(descendants.Items.All(x => DirectoryQueries.ContainsObject(x.DistinguishedName, scope)), "OU query excludes siblings");
        Assert((await service.QueryAsync(DirectoryKind.Users, "liu.kx", scope)).Items.Count == 1 && (await service.QueryAsync(DirectoryKind.Users, "lin.zx", scope)).Items.Count == 0, "Search remains constrained to selected OU");
        Assert((await service.QueryAsync(DirectoryKind.Users, "", new(containers.Items.Single(x => x.Name == "待入职"), true))).Items.Count == 0, "Empty OU returns an empty result");
        await Deny(() => service.QueryAsync(DirectoryKind.Users, "", scope with { Container = ou with { Id = Guid.NewGuid() } }), "Stale directory GUID rejected");
        await Deny(() => service.QueryAsync(DirectoryKind.Users, "", scope with { Container = ou with { DistinguishedName = "OU=Changed," + service.Session.BaseDn } }), "Moved directory snapshot rejected");
        using var password = new SecureString();
        foreach (var c in "Test!Password123") password.AppendChar(c);
        var child = containers.Items.Single(x => x.Name == "软件开发组");
        var spec = new NewUserSpec(child, "OU Test", "ou.test", "ou.test@example.test", "", "", false, true);
        await service.CreateUserAsync(spec, password);
        Assert((await service.QueryAsync(DirectoryKind.Users, "ou.test", new(child, false))).Items.Count == 1, "Created user belongs to chosen nested OU");
        Assert((await service.QueryAsync(DirectoryKind.Users, "ou.test", scope with { IncludeDescendants = false })).Items.Count == 0, "New nested user absent from parent's direct-only result");
        using var readOnly = new DemoDirectoryService(allowWrites: false);
        var roRoot = (await readOnly.GetContainersAsync()).Items.Single(x => x.IsDomainRoot);
        Assert((await readOnly.QueryAsync(DirectoryKind.Users, "", new(roRoot, true))).Items.Count == 12, "Read-only connection can browse scoped directories");
    }

    private static void TestOuWindow(MainWindow main, DataGrid objects, FieldInfo directoryField)
    {
        ((IDirectoryService)directoryField.GetValue(main)!).Dispose();
        var controlled = new ControlledDirectoryService();
        directoryField.SetValue(main, controlled);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Load() => ((Task)typeof(MainWindow).GetMethod("LoadDirectoryAsync", flags)!.Invoke(main, null)!).GetAwaiter().GetResult();
        void RefreshTree() => ((Task)typeof(MainWindow).GetMethod("RefreshDirectoryTreeAsync", flags)!.Invoke(main, new object?[] { null })!).GetAwaiter().GetResult();
        var types = (ComboBox)main.FindName("ObjectTypeBox");
        types.SelectedIndex = 0;
        Load();
        var tree = (TreeView)main.FindName("OuTree");
        var root = ((IEnumerable<DirectoryNode>)tree.ItemsSource).Single();
        Assert(((TextBlock)main.FindName("TreeNotice")).Text == "" && ((TextBlock)main.FindName("TreeNotice")).Visibility == Visibility.Collapsed, "Normal tree has no directory-count or explanatory footer");
        Assert(root.All().All(x => x.Name is not "System" and not "Program Data" and not "Microsoft" and not "Keys"), "Advanced demo branches are absent in rendered normal tree");
        Assert(root.Children.Any(x => x.Name == "Builtin"), "Builtin appears in application directory tree");
        void Select(DirectoryNode node)
        {
            root.ExpandPath(node);
            foreach (var item in root.All()) item.IsSelected = item == node;
            typeof(MainWindow).GetMethod("OuTree_SelectedItemChanged", flags)!.Invoke(main, [tree, new RoutedPropertyChangedEventArgs<object>(null!, node)]);
        }
        Assert(objects.Items.Count == 26 && ((TextBlock)main.FindName("ScopeTitle")).Text == "example.test", "New directory connection resets old scope to its own root");
        var tech = root.All().Single(x => x.Name == "技术研发部");
        Select(tech);
        Assert(objects.Items.Count == 5 && objects.Items.Cast<DirectoryItem>().Select(x => x.Kind).Distinct().Count() == 3 && ((TextBlock)main.FindName("ScopeTitle")).Text == tech.Name, "Tree selection queries users, groups and computers inside real selected OU");
        Assert(objects.Items.Cast<DirectoryItem>().All(x => DirectoryQueries.ContainsObject(x.DistinguishedName, new(tech.Container!, true))), "UI only shows objects inside selected scope");
        var calls = controlled.QueryCalls;
        ((TextBox)main.FindName("OuFilterBox")).Text = "软件开发组";
        Assert(controlled.QueryCalls == calls && objects.Items.Count == 5 && ((TextBlock)main.FindName("ScopeTitle")).Text == tech.Name, "Tree-name filtering neither re-queries nor broadens scope");
        ((TextBox)main.FindName("OuFilterBox")).Text = "产品设计部";
        main.UpdateLayout();
        main.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(controlled.QueryCalls == calls && objects.Items.Count == 5 && ((TextBlock)main.FindName("ScopeTitle")).Text == tech.Name, "Hiding selected OU with tree filter does not trigger domain-root fallback");
        ((TextBox)main.FindName("OuFilterBox")).Clear();
        var include = (CheckBox)main.FindName("IncludeDescendantsBox");
        include.IsChecked = false;
        Assert(objects.Items.Count == 3 && ((TextBlock)main.FindName("UserCounts")).Text.StartsWith("3 个对象"), "Direct-only checkbox includes only direct users, groups and computers");
        include.IsChecked = true;
        types.SelectedIndex = 2;
        Assert(objects.Items.Count == 1 && objects.Items.Cast<DirectoryItem>().All(x => x.Kind == DirectoryKind.Groups) && ((Button)main.FindName("MembersButton")).IsEnabled && ((StackPanel)main.FindName("AccountActions")).Visibility == Visibility.Collapsed, "In-tree group filter shows only local groups and their member action");
        Render(main, "directory-unified-group.png");
        types.SelectedIndex = 3;
        Assert(objects.Items.Count == 1 && ((Button)main.FindName("EditComputerButton")).IsEnabled && !((Button)main.FindName("MembersButton")).IsEnabled && !((Button)main.FindName("UnlockButton")).IsEnabled, "In-tree computer filter shows scoped computer operations only");
        Assert(((FrameworkElement)main.FindName("OuPanel")).Visibility == Visibility.Visible && objects.Items.Cast<DirectoryItem>().All(x => !x.DisplayAccount.EndsWith('$') && x.Account.EndsWith('$')), "Computer filter keeps same directory tree and preserves real dollar-suffixed account internally");
        Render(main, "directory-unified-computer.png");
        types.SelectedIndex = 1;
        Assert(objects.Items.Count == 3 && objects.Items.Cast<DirectoryItem>().All(x => x.Kind == DirectoryKind.Users) && ((Button)main.FindName("PasswordButton")).IsEnabled, "User filter stays within same selected directory");
        include.IsChecked = false;
        Assert(objects.Items.Count == 1, "User direct-only remains supported in unified tree");
        include.IsChecked = true;
        types.SelectedIndex = 0;
        var search = (TextBox)main.FindName("SearchBox");
        search.Text = "liu.kx"; Load();
        Assert(objects.Items.Count == 1, "User UI search stays within current OU subtree");
        search.Text = "lin.zx"; Load();
        Assert(objects.Items.Count == 0 && !((Button)main.FindName("ExportButton")).IsEnabled, "Out-of-scope search yields no exportable users");
        search.Clear(); Load();
        objects.SelectedItem = objects.Items.Cast<DirectoryItem>().First(x => x.Kind == DirectoryKind.Users);
        Render(main, "directory-unified-1390.png");
        main.Width = 1920;
        Render(main, "directory-unified-1920.png", 1920, 1080);
        main.Width = 1120;
        Render(main, "directory-unified-1120.png", 1120, 740);
        main.Width = 1390;
        var containers = controlled.GetContainersAsync().GetAwaiter().GetResult();
        var preferred = new NewUserDialog(containers, controlled.Session, tech.Container!.Id);
        var target = Descendants((DependencyObject)preferred.Content).OfType<ComboBox>().Single(x => x.Name == "ContainerBox");
        Assert(target.SelectedItem is DirectoryContainer selected && selected.Id == tech.Container.Id, "New-user dialog defaults to selected OU GUID");
        Assert(target.Items.Cast<DirectoryContainer>().All(x => x.CanCreateUser), "New-user choices exclude domain/system containers");
        preferred.Close();
        var rootDialog = new NewUserDialog(containers, controlled.Session, root.Container!.Id);
        Assert(Descendants((DependencyObject)rootDialog.Content).OfType<ComboBox>().Single(x => x.Name == "ContainerBox").SelectedIndex == -1, "Domain-root create requires explicit target instead of silent fallback");
        rootDialog.Close();
        var empty = root.All().Single(x => x.Name == "待入职"); Select(empty);
        Assert(objects.Items.Count == 0 && ((TextBlock)main.FindName("DetailName")).Text == "选择一个对象", "Empty OU clears old user details");
        Assert(!((Button)main.FindName("UnlockButton")).IsEnabled && !((Button)main.FindName("ExportButton")).IsEnabled, "Empty OU cannot mutate/export a stale user");
        Select(tech);
        types.SelectedIndex = 3;
        search.Text = "WS-ENGINEERING-008"; Load();
        include.IsChecked = false;
        Assert(objects.Items.Count == 1 && types.SelectedIndex == 3 && include.IsChecked == false && search.Text == "WS-ENGINEERING-008" && ((TextBlock)main.FindName("ScopeTitle")).Text == tech.Name, "Directory-only workspace preserves the selected scope and filters");
        types.SelectedIndex = 0;
        search.Clear(); include.IsChecked = true; Load();
        controlled.OverrideContainers = containers with { Truncated = true };
        RefreshTree(); Load();
        Assert(((TextBlock)main.FindName("TreeNotice")).Text.Contains("截断") && ((TextBlock)main.FindName("TreeNotice")).Visibility == Visibility.Visible, "Truncated normal tree still shows important warning");
        controlled.OverrideContainers = new(containers.Items.Select(x => x.Id == tech.Container!.Id ? x with { IsAdvancedViewOnly = true } : x).ToArray(), false);
        calls = controlled.QueryCalls;
        RefreshTree(); Load();
        Assert(objects.Items.Count == 0 && controlled.QueryCalls == calls && ((TextBlock)main.FindName("TreeNotice")).Text.Contains("重新选择"), "Selected advanced-only branch becoming hidden never broadens query to root");
        controlled.OverrideContainers = null;
        RefreshTree();
        root = ((IEnumerable<DirectoryNode>)tree.ItemsSource).Single();
        tech = root.All().Single(x => x.Name == "技术研发部");
        Select(tech);
        controlled.FailQueries = true;
        try { Load(); throw new Exception("FAIL: query failure not propagated"); }
        catch (InvalidOperationException) { Assert(true, "Query failure propagated without stale success"); }
        Assert(objects.Items.Count == 0 && !((Button)main.FindName("ExportButton")).IsEnabled && !((Button)main.FindName("DisableButton")).IsEnabled && !((Button)main.FindName("MembersButton")).IsEnabled && !((Button)main.FindName("EditComputerButton")).IsEnabled, "Failed scoped query clears rows, export, user/group/computer actions");
        controlled.FailQueries = false;
        controlled.OverrideContainers = new(containers.Items.Where(x => x.Id != tech.Container!.Id).ToArray(), false);
        calls = controlled.QueryCalls;
        RefreshTree(); Load();
        Assert(objects.Items.Count == 0 && controlled.QueryCalls == calls && ((TextBlock)main.FindName("ScopeTitle")).Text == "请选择目录", "Missing selected OU on refresh never silently falls back to domain-wide query");
        controlled.OverrideContainers = null;
        RefreshTree();
        root = ((IEnumerable<DirectoryNode>)tree.ItemsSource).Single();
        Select(root);
        Assert(objects.Items.Count == 26, "Explicit domain-root selection restores domain-wide mixed query");

    }

    private sealed class ControlledDirectoryService : IDirectoryService
    {
        private readonly DemoDirectoryService _inner = new();
        public DirectorySession Session => _inner.Session;
        public ContainerResult? OverrideContainers { get; set; }
        public bool FailQueries { get; set; }
        public int QueryCalls { get; private set; }
        public Task<QueryResult> QueryAsync(DirectoryKind kind, string search, DirectoryQueryScope? scope = null)
        {
            QueryCalls++;
            return FailQueries ? Task.FromException<QueryResult>(new InvalidOperationException("Test query failure")) : _inner.QueryAsync(kind, search, scope);
        }
        public Task<ContainerResult> GetContainersAsync() => OverrideContainers is null ? _inner.GetContainersAsync() : Task.FromResult(OverrideContainers);
        public Task ExecuteAsync(DirectoryItem target, AccountAction action, SecureString? password = null, bool mustChangePassword = true) => _inner.ExecuteAsync(target, action, password, mustChangePassword);
        public Task CreateUserAsync(NewUserSpec spec, SecureString password) => _inner.CreateUserAsync(spec, password);
        public Task CreateComputerAsync(NewComputerSpec spec) => _inner.CreateComputerAsync(spec);
        public Task CreateGroupAsync(NewGroupSpec spec) => _inner.CreateGroupAsync(spec);
        public Task<QueryResult> GetGroupUsersAsync(DirectoryItem group) => _inner.GetGroupUsersAsync(group);
        public Task ChangeGroupUserAsync(DirectoryItem group, DirectoryItem user, bool add) => _inner.ChangeGroupUserAsync(group, user, add);
        public Task UpdateComputerAsync(DirectoryItem computer, ComputerChanges changes) => _inner.UpdateComputerAsync(computer, changes);
        public Task DeleteComputerAsync(DirectoryItem computer) => _inner.DeleteComputerAsync(computer);
        public void Dispose() => _inner.Dispose();
    }
}


using System.IO;
using System.DirectoryServices.Protocols;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DomainDesk;
using DomainDesk.Dialogs;
using DomainDesk.Services;

internal static partial class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        try
        {
            TestFilters();
            TestValidation();
            TestMutationRequests();
            TestPreferences();
            TestDirectoryNamesAndTree();
            TestDirectoryScopesAsync().GetAwaiter().GetResult();
            TestDemoAsync().GetAwaiter().GetResult();
            TestDirectoryLifecycleAsync().GetAwaiter().GetResult();
            TestObjectCreationAsync().GetAwaiter().GetResult();
            TestCsv();
            TestWindows();
            Console.WriteLine($"PASS: {_assertions} assertions. No live servers contacted.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Assert(bool value, string name)
    {
        _assertions++;
        if (!value) throw new Exception("FAIL: " + name);
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (ArgumentException) { Assert(true, name); return; }
        throw new Exception("FAIL: " + name);
    }

    private static void TestFilters()
    {
        Assert(LdapFilters.Escape("a*)(x=\\\0") == "a\\2a\\29\\28x=\\5c\\00", "LDAP special characters escaped");
        Assert(LdapFilters.Escape("张三") == "张三", "Unicode search preserved");
        Assert(LdapFilters.Build(DirectoryKind.Users, "").Contains("(objectCategory=person)"), "Computer accounts excluded from users");
        Assert(!LdapFilters.Build(DirectoryKind.Users, "*)(objectClass=*)").Contains("(objectClass=*)"), "LDAP injection escaped");
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert(LdapFilters.GuidFilter(id) == "(objectGUID=\\33\\22\\11\\00\\55\\44\\77\\66\\88\\99\\aa\\bb\\cc\\dd\\ee\\ff)", "AD binary GUID byte ordering");
        Assert(!LdapFilters.Build(DirectoryKind.Groups, "   ").Contains("sAMAccountName"), "Whitespace search is unfiltered");
    }

    private static void TestValidation()
    {
        RemoteValidation.ValidateServer("dc01.corp.example.test");
        Assert(true, "Remote FQDN accepted");
        RemoteValidation.ValidateServer("192.168.10.10");
        Assert(true, "Remote IPv4 accepted");
        RemoteValidation.ValidateServer("fd00::10");
        Assert(true, "Remote IPv6 accepted");
        foreach (var server in new[] { "localhost", "localhost.example.test", "127.0.0.1", "https://dc.test", "dc.test:636", "dc.test/path", Environment.MachineName + ".test", "dc.test;whoami" })
            Reject(() => RemoteValidation.ValidateServer(server), "Unsafe/local server rejected: " + server);
    }

    private static async Task TestDemoAsync()
    {
        using var directory = new DemoDirectoryService();
        Assert(directory.Session.IsDemo, "Demo mode explicit");
        var result = await directory.QueryAsync(DirectoryKind.Users, "");
        Assert(result.Items.Count == 12, "Demo users");
        var locked = result.Items.First(x => x.Locked);
        await directory.ExecuteAsync(locked, AccountAction.Unlock);
        result = await directory.QueryAsync(DirectoryKind.Users, locked.Account);
        Assert(result.Items.Count == 1 && !result.Items[0].Locked, "Unlock updates only selected demo account");
        await directory.ExecuteAsync(locked, AccountAction.Disable);
        Assert(!(await directory.QueryAsync(DirectoryKind.Users, locked.Account)).Items[0].Enabled, "Demo disable");
        await directory.ExecuteAsync(locked, AccountAction.Enable);
        Assert((await directory.QueryAsync(DirectoryKind.Users, locked.Account)).Items[0].Enabled, "Demo enable");
        using var password = new SecureString();
        foreach (var c in "Test!Password123") password.AppendChar(c);
        await directory.ExecuteAsync(locked, AccountAction.ResetPassword, password);
        Assert((await directory.QueryAsync(DirectoryKind.Users, "not-found")).Items.Count == 0, "Empty results");
        Assert((await directory.QueryAsync(DirectoryKind.Computers, "")).Items.Count == 8, "Demo computers");
        Assert((await directory.QueryAsync(DirectoryKind.Groups, "")).Items.Count == 6, "Demo groups");
    }

    private static void TestCsv()
    {
        Assert(CsvExporter.Cell("=1+1") == "\"'=1+1\"", "CSV formula escaped");
        Assert(CsvExporter.Cell("  @SUM(A1)").StartsWith("\"'"), "Whitespace formula escaped");
        Assert(CsvExporter.Cell("a\"b,c") == "\"a\"\"b,c\"", "CSV quote and delimiter");
        Assert(CsvExporter.Cell("\tformula").StartsWith("\"'"), "CSV tab escaped");
        using var directory = new DemoDirectoryService();
        var mixed = directory.QueryAsync(DirectoryKind.All, "").GetAwaiter().GetResult().Items;
        var path = Path.Combine(Path.GetTempPath(), "DomainDesk-mixed-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            CsvExporter.Export(path, mixed, directory.Session);
            var lines = File.ReadAllLines(path);
            Assert(lines.Length == mixed.Count + 1 && lines[0].Contains("服务器,类型,名称"), "Unified CSV includes type column and exactly current results");
            Assert(new[] { "用户", "用户组", "计算机" }.All(type => lines.Any(line => line.Contains(",\"" + type + "\","))), "Unified CSV identifies each concrete object type");
            Assert(mixed.Where(x => x.Kind == DirectoryKind.Computers).All(x => lines.Any(line => line.Contains(x.Account))), "CSV preserves actual dollar-suffixed machine account for audit");
        }
        finally { File.Delete(path); }
    }

    private static void TestMutationRequests()
    {
        var parent = new DirectoryContainer(Guid.NewGuid(), "OU=People,DC=example,DC=test");
        var spec = new NewUserSpec(parent, "#Test, +CN=Injected", "test.user", "test.user@example.test", "test@example.test", "IT", true, true);
        var request = DirectoryMutations.CreateDisabledUser(spec);
        Assert(request.DistinguishedName == @"CN=\#Test\, \+CN\=Injected,OU=People,DC=example,DC=test", "New user DN injection escaped");
        Assert(DirectoryMutations.EscapeRdn(" a ") == @"\ a\ ", "Leading/trailing DN spaces escaped");
        Assert(DirectoryMutations.EscapeRdn("a\0b") == @"a\00b", "DN NUL escaped");
        Assert(request.Attributes.Cast<DirectoryAttribute>().Single(x => x.Name == "userAccountControl")[0].Equals("514"), "Provision starts disabled even when Enable requested");
        Assert(!request.Attributes.Cast<DirectoryAttribute>().Any(x => x.Name.Contains("Pwd", StringComparison.OrdinalIgnoreCase)), "Initial Add never contains password");
        Assert(request.Attributes.Cast<DirectoryAttribute>().Single(x => x.Name == "objectClass").Count == 4, "New user objectClass chain");
        foreach (var invalid in new[] { "x$", "a/b", "a b", "a,OU=evil", "a@b", new string('a', 21), "x." })
            Reject(() => DirectoryMutations.ValidateUser(spec with { Account = invalid }), "Reject unsafe SAM " + invalid);
        foreach (var invalid in new[] { "abc", "@domain", "x@", "x@y@z", "x y@domain" })
            Reject(() => DirectoryMutations.ValidateUser(spec with { UserPrincipalName = invalid }), "Reject malformed UPN " + invalid);
        Reject(() => DirectoryMutations.ValidateUser(spec with { Name = "a\nb" }), "Reject CN control character");
        Reject(() => DirectoryMutations.ValidateUser(spec with { Container = parent with { Id = Guid.Empty } }), "Reject missing parent GUID");
        var empty = DirectoryMutations.CreateDisabledUser(spec with { Email = "", Department = "" });
        Assert(!empty.Attributes.Cast<DirectoryAttribute>().Any(x => x.Name is "mail" or "department"), "Optional attributes omitted when blank");
        foreach (var add in new[] { true, false })
        {
            var change = DirectoryMutations.GroupUser("CN=Group,DC=test", "CN=User,DC=test", add);
            Assert(change.Modifications.Count == 1 && change.Modifications[0].Name == "member", "Membership modifies one attribute");
            Assert(change.Modifications[0].Operation == (add ? DirectoryAttributeOperation.Add : DirectoryAttributeOperation.Delete), "Membership never replaces whole member list");
            Assert(change.Modifications[0].Count == 1 && change.Modifications[0][0].Equals("CN=User,DC=test"), "Membership targets exactly one user DN");
        }
        var computer = DirectoryMutations.Computer("CN=PC,DC=test", new("description", ""));
        Assert(computer.Modifications.Count == 2 && computer.Modifications[0].Name == "description" && computer.Modifications[1].Name == "location", "Computer edit restricted to description/location");
        Assert(computer.Modifications[1].Operation == DirectoryAttributeOperation.Replace && computer.Modifications[1].Count == 0, "Blank computer property removes attribute");
        Reject(() => DirectoryMutations.Computer("CN=PC,DC=test", new("bad\nvalue", "")), "Computer control characters rejected");
        var item = new DirectoryItem { Kind = DirectoryKind.Computers, Name = "PC$", Account = "PC$" };
        Assert(item.DisplayName == "PC" && item.DisplayAccount == "PC" && item.Account == "PC$", "Machine dollar sign hidden without changing account");
        Assert(!(item with { IsDomainController = true }).CanManageComputer && !(item with { IsCriticalSystemObject = true }).CanManageComputer, "DC/critical computer actions unavailable");
        Assert((item with { Kind = DirectoryKind.Users }).DisplayAccount == "PC$", "User account display is not trimmed");
    }

    private static void TestPreferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "DomainDesk-test-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ConnectionPreferences(path);
            Assert(store.Load("AD") == new ConnectionProfile(), "Missing preference file defaults without connection");
            var profiles = new Dictionary<string, ConnectionProfile> { ["AD"] = new("192.168.10.10", "admin@example.test", false, false, true),
                ["DNS"] = new("dns.example.test", "EXAMPLE\\dnsadmin", true, true, false), ["SMB"] = new("192.168.10.20", "EXAMPLE\\filesadmin", false, true, true) };
            foreach (var pair in profiles) store.Save(pair.Key, pair.Value);
            var restored = new ConnectionPreferences(path);
            foreach (var pair in profiles) Assert(restored.Load(pair.Key) == pair.Value, "All non-secret settings roundtrip independently: " + pair.Key);
            var json = File.ReadAllText(path);
            Assert(!json.Contains("password", StringComparison.OrdinalIgnoreCase) && !json.Contains("SecureString"), "Persisted settings contain no password fields");
            Assert(typeof(ConnectionProfile).GetProperties().All(x => x.PropertyType == typeof(string) || x.PropertyType == typeof(bool)), "Preference DTO cannot hold credential objects");
            File.WriteAllText(path, "{corrupt");
            Assert(restored.Load("AD") == new ConnectionProfile() && restored.Warning is not null, "Corrupt settings return defaults with warning");
            restored.Save("AD", profiles["AD"]);
            Assert(restored.Load("AD") == profiles["AD"], "Can recover from corrupt settings");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static async Task Deny(Func<Task> action, string label)
    {
        try { await action(); }
        catch (InvalidOperationException) { Assert(true, label); return; }
        throw new Exception("FAIL: " + label);
    }

    private static async Task TestDirectoryLifecycleAsync()
    {
        using var directory = new DemoDirectoryService(useTls: false);
        using var password = new SecureString();
        foreach (var c in "Test!Password123") password.AppendChar(c);
        var container = (await directory.GetContainersAsync()).Items.First(x => x.CanCreateUser);
        var spec = new NewUserSpec(container, "Test, User", "test.new", "test.new@example.test", "new@example.test", "IT", true, true);
        await directory.CreateUserAsync(spec, password);
        var user = (await directory.QueryAsync(DirectoryKind.Users, "test.new")).Items.Single();
        Assert(user.Enabled && user.Email == spec.Email && user.Department == spec.Department, "Create user with attributes");
        Assert(user.DistinguishedName.Contains(@"Test\, User"), "Demo DN escaping matches live request");
        await Deny(() => directory.CreateUserAsync(spec, password), "Duplicate user rejected");
        await Deny(() => directory.CreateUserAsync(spec with { Account = "other", Name = "other" }, password), "Duplicate UPN rejected");
        await directory.ExecuteAsync(user, AccountAction.Unlock);
        await directory.ExecuteAsync(user, AccountAction.ResetPassword, password);
        Assert(true, "Unlock unlocked user and reset password over sealed LDAP 389 supported");
        var group = (await directory.QueryAsync(DirectoryKind.Groups, "Engineering")).Items.Single();
        await directory.ChangeGroupUserAsync(group, user, true);
        Assert((await directory.GetGroupUsersAsync(group)).Items.Any(x => x.Id == user.Id), "Group add user");
        await Deny(() => directory.ChangeGroupUserAsync(group, user, true), "Duplicate direct membership rejected");
        await directory.ChangeGroupUserAsync(group, user, false);
        Assert((await directory.GetGroupUsersAsync(group)).Items.All(x => x.Id != user.Id), "Group remove user");
        Assert((await directory.QueryAsync(DirectoryKind.Users, "test.new")).Items.Count == 1, "Removing membership does not delete user");
        var computers = (await directory.QueryAsync(DirectoryKind.Computers, "")).Items;
        var computer = computers[0];
        await directory.UpdateComputerAsync(computer, new("new description", "Room 2"));
        var updated = (await directory.QueryAsync(DirectoryKind.Computers, computer.Account)).Items.Single();
        Assert(updated.Description == "new description" && updated.Location == "Room 2" && updated.Account == computer.Account && updated.Id == computer.Id, "Computer edits preserve identity and machine account");
        await Deny(() => directory.ExecuteAsync(computer with { Kind = DirectoryKind.Users }, AccountAction.Disable), "Type spoof cannot mutate a computer as user");
        await directory.DeleteComputerAsync(updated);
        Assert((await directory.QueryAsync(DirectoryKind.Computers, "")).Items.Count == computers.Count - 1, "Computer deletion targets one object");
        await Deny(() => directory.DeleteComputerAsync(updated), "Stale deleted computer rejected");
        using var readOnly = new DemoDirectoryService(allowWrites: false);
        var roUser = (await readOnly.QueryAsync(DirectoryKind.Users, "")).Items[0];
        var roGroup = (await readOnly.QueryAsync(DirectoryKind.Groups, "")).Items[0];
        var roComputer = (await readOnly.QueryAsync(DirectoryKind.Computers, "")).Items[0];
        var roSpec = spec with { Container = (await readOnly.GetContainersAsync()).Items.First(x => x.CanCreateUser) };
        await Deny(() => readOnly.ExecuteAsync(roUser, AccountAction.Unlock), "Readonly blocks unlock");
        await Deny(() => readOnly.ExecuteAsync(roUser, AccountAction.ResetPassword, password), "Readonly blocks password reset");
        await Deny(() => readOnly.CreateUserAsync(roSpec, password), "Readonly blocks create user");
        await Deny(() => readOnly.ChangeGroupUserAsync(roGroup, roUser, true), "Readonly blocks membership add");
        await Deny(() => readOnly.ChangeGroupUserAsync(roGroup, roUser, false), "Readonly blocks membership remove");
        await Deny(() => readOnly.UpdateComputerAsync(roComputer, new("", "")), "Readonly blocks computer edit");
        await Deny(() => readOnly.DeleteComputerAsync(roComputer), "Readonly blocks computer delete");
        Assert((await readOnly.GetGroupUsersAsync(roGroup)).Items.Count > 0, "Readonly still permits browsing group users");
    }

    private static void TestWindows()
    {
        var app = new App();
        app.InitializeComponent();
        var main = new MainWindow();
        main.ShowActivated = false;
        main.ShowInTaskbar = false;
        main.WindowState = WindowState.Minimized;
        main.Show();
        main.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(app.Windows.Count == 1, "Tests do not open a second startup window");
        Assert(((DataGrid)main.FindName("ObjectsGrid")).Items.Count == 0 && ((Button)main.FindName("ConnectButton")).Content?.ToString() == "连接 AD 域", "Main window starts disconnected without demo data");
        Assert(main.FindName("ComputersNav") is null && main.FindName("GroupsNav") is null && main.FindName("DirectoryNav") is null && main.FindName("DnsNav") is null && main.FindName("SmbNav") is null && main.FindName("AuditNav") is null, "Navigation and removed service entries are absent");
        Assert(main.FindName("ScopeDn") is null && main.FindName("DetailDn") is null && main.FindName("SessionBanner") is null, "Removed DN fields and demo banner are absent, not hidden");
        Assert(!((Button)main.FindName("NewUserButton")).IsEnabled && !((Button)main.FindName("NewComputerButton")).IsEnabled && !((Button)main.FindName("NewGroupButton")).IsEnabled && !((Button)main.FindName("ExportButton")).IsEnabled, "Disconnected workspace cannot create objects or export stale data");
        Assert(typeof(MainWindow).Assembly.GetType("DomainDesk.Services.DemoDirectoryService") is null, "Demo directory fixture is not shipped in the application assembly");
        Assert(typeof(MainWindow).Assembly.GetType("DomainDesk.Services.RemoteWindowsService") is null && typeof(MainWindow).Assembly.GetType("DomainDesk.Dialogs.RemoteConnectDialog") is null && typeof(MainWindow).Assembly.GetReferencedAssemblies().All(x => x.Name != "System.Management"), "Removed DNS/SMB services, dialogs and WMI dependency are not shipped");
        Render(main, "directory-disconnected.png");
        var directoryField = typeof(MainWindow).GetField("_directory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((IDirectoryService)directoryField.GetValue(main)!).Dispose();
        directoryField.SetValue(main, new DemoDirectoryService(useTls: false));
        typeof(MainWindow).GetMethod("Refresh_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs()]);
        var objects = (DataGrid)main.FindName("ObjectsGrid");
        objects.SelectedItem = objects.Items.Cast<DirectoryItem>().First(x => x.Kind == DirectoryKind.Users && !x.Locked);
        Assert(((TextBlock)main.FindName("DetailGroups")).Text == "All-Staff\nTeam-1", "Object details show direct group names rather than full DNs");
        var user = (DirectoryItem)objects.SelectedItem;
        objects.SelectedItem = null;
        objects.ItemsSource = new[] { user with { MemberOf = [@"CN=Ops\, West,OU=Groups,DC=example,DC=test", @"CN=\E7\A0\94\E5\8F\91,OU=Groups,DC=example,DC=test"] } };
        objects.SelectedIndex = 0;
        Assert(((TextBlock)main.FindName("DetailGroups")).Text == "Ops, West\n研发", "Group display decodes escaped commas and UTF-8 names");
        typeof(MainWindow).GetMethod("Refresh_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs()]);
        objects.SelectedItem = objects.Items.Cast<DirectoryItem>().First(x => x.Id == user.Id);
        Assert(((Button)main.FindName("PasswordButton")).IsEnabled, "Reset password button enabled on SASL-sealed LDAP 389");
        Assert(((Button)main.FindName("UnlockButton")).IsEnabled, "Unlock available even if displayed lock state is stale/unlocked");
        Assert(((Button)main.FindName("NewUserButton")).IsEnabled, "Create user action available");
        Assert(((Button)main.FindName("NewComputerButton")).IsEnabled && ((Button)main.FindName("NewGroupButton")).IsEnabled, "Computer/group creation actions available with write permission");
        var creationButtons = ((Panel)((Button)main.FindName("NewUserButton")).Parent).Children.OfType<Button>().Take(3).Select(x => x.Name).ToArray();
        Assert(creationButtons.SequenceEqual(new[] { "NewUserButton", "NewComputerButton", "NewGroupButton" }), "New computer and group buttons immediately follow new user in requested order");
        Assert(main.Icon is not null, "Application icon loaded");
        using (var icon = Application.GetResourceStream(new Uri("/DomainDesk;component/Assets/DomainDesk.ico", UriKind.Relative)).Stream)
        using (var reader = new BinaryReader(icon))
        {
            Assert(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 8, "ICO resource has eight icon frames");
            foreach (var size in new[] { 16, 20, 24, 32, 48, 64, 128, 256 })
            {
                var width = reader.ReadByte(); var height = reader.ReadByte();
                Assert((width == 0 ? 256 : width) == size && width == height, "Icon frame size " + size);
                reader.ReadBytes(14);
            }
        }
        Render(main, "directory-preview.png");
        objects.SelectedItem = objects.Items.Cast<DirectoryItem>().First(x => x.Kind == DirectoryKind.Computers);
        Assert(((Button)main.FindName("EditComputerButton")).IsEnabled && ((Button)main.FindName("DeleteComputerButton")).IsEnabled, "Selecting a computer in unified list enables computer operations");
        Assert(((StackPanel)main.FindName("ComputerActions")).Visibility == Visibility.Visible && ((StackPanel)main.FindName("AccountActions")).Visibility == Visibility.Collapsed && !((Button)main.FindName("PasswordButton")).IsEnabled, "Computer selection never exposes user operations");
        objects.SelectedItem = objects.Items.Cast<DirectoryItem>().First(x => x.Kind == DirectoryKind.Groups);
        Assert(((Button)main.FindName("MembersButton")).IsEnabled && ((StackPanel)main.FindName("GroupActions")).Visibility == Visibility.Visible && !((Button)main.FindName("EditComputerButton")).IsEnabled, "Group operations follow selected object, not a separate page");
        Assert(main.FindName("DnsGrid") is null && main.FindName("SessionsGrid") is null && main.FindName("FilesGrid") is null, "DNS and file-sharing management surfaces are removed");
        TestOuWindow(main, objects, directoryField);
        TestCreationWindows(main, objects, directoryField);
        var profilePath = Path.Combine(Path.GetTempPath(), "DomainDesk-ui-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var preferences = new ConnectionPreferences(profilePath);
        var adProfile = new ConnectionProfile("dc.example.test", "admin@example.test", false, false, false);
        var dnsProfile = new ConnectionProfile("192.168.10.10", "EXAMPLE\\dnsadmin", false, true, true);
        var smbProfile = new ConnectionProfile("192.168.10.20", "EXAMPLE\\filesadmin", true, true, false);
        preferences.Save("AD", adProfile);
        preferences.Save("DNS", dnsProfile);
        preferences.Save("SMB", smbProfile);
        var connect = new ConnectDialog(preferences);
        Assert(((TextBox)connect.FindName("ServerBox")).Text == adProfile.Server && ((TextBox)connect.FindName("UsernameBox")).Text == adProfile.Username, "AD dialog restores saved server and account");
        Assert(((CheckBox)connect.FindName("TlsBox")).IsChecked == false && ((CheckBox)connect.FindName("CurrentIdentityBox")).IsChecked == false, "AD dialog restores transport and identity independently of legacy readonly preference");
        Assert(connect.FindName("WritesBox") is null, "AD dialog no longer displays a write-permission option");
        Assert(((PasswordBox)connect.FindName("PasswordInput")).SecurePassword.Length == 0 && connect.Settings is null, "AD restore never fills password or auto-connects");
        Render(connect, "connect-dialog.png", 510, 530);
        TestDefaultWritableConnections();
        File.Delete(profilePath);
        var service = (IDirectoryService)directoryField.GetValue(main)!;
        var parent = service.GetContainersAsync().GetAwaiter().GetResult();
        var pc = service.QueryAsync(DirectoryKind.Computers, "").GetAwaiter().GetResult().Items[0];
        var group = service.QueryAsync(DirectoryKind.Groups, "").GetAwaiter().GetResult().Items[0];
        Window[] dialogs = [connect,
            new PasswordDialog(new() { Name = "Test", Account = "test" }, true), new NewUserDialog(parent, service.Session),
            new ComputerEditDialog(pc), new ComputerDeleteDialog(pc, service.Session), new GroupMembersDialog(service, group, (_, _, _) => Task.CompletedTask)];
        foreach (var dialog in dialogs)
        {
            dialog.Measure(new Size(dialog.Width, dialog.Height));
            dialog.Arrange(new Rect(0, 0, dialog.Width, dialog.Height));
            dialog.UpdateLayout();
            Assert(dialog.Content is not null, "Dialog XAML loaded: " + dialog.Title);
            dialog.Close();
        }
        ((IDirectoryService)directoryField.GetValue(main)!).Dispose();
        directoryField.SetValue(main, new DemoDirectoryService(allowWrites: false, useTls: false));
        typeof(MainWindow).GetMethod("Refresh_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs()]);
        ((ComboBox)main.FindName("ObjectTypeBox")).SelectedIndex = 0;
        objects.SelectedItem = objects.Items.Cast<DirectoryItem>().First(x => x.Kind == DirectoryKind.Groups);
        Assert(((Button)main.FindName("MembersButton")).IsEnabled, "Readonly can open group member browser");
        objects.SelectedItem = objects.Items.Cast<DirectoryItem>().First(x => x.Kind == DirectoryKind.Computers);
        Assert(!((Button)main.FindName("EditComputerButton")).IsEnabled && !((Button)main.FindName("DeleteComputerButton")).IsEnabled, "Readonly unified list disables computer mutation UI");
        objects.SelectedItem = objects.Items.Cast<DirectoryItem>().First(x => x.Kind == DirectoryKind.Users);
        Assert(!((Button)main.FindName("PasswordButton")).IsEnabled && !((Button)main.FindName("UnlockButton")).IsEnabled && !((Button)main.FindName("NewUserButton")).IsEnabled, "Readonly disables all user mutation UI");
        Assert(!((Button)main.FindName("NewComputerButton")).IsEnabled && !((Button)main.FindName("NewGroupButton")).IsEnabled, "Readonly disables both new creation buttons");
        main.Close();
        app.Shutdown();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static void TestDefaultWritableConnections()
    {
        var path = Path.Combine(Path.GetTempPath(), "DomainDesk-write-default-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var preferences = new ConnectionPreferences(path);
            foreach (bool? savedWrites in new bool?[] { null, false, true })
            {
                if (savedWrites.HasValue) preferences.Save("AD", new("dc.example.test", AllowWrites: savedWrites.Value));
                var dialog = new ConnectDialog(preferences)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000, Top = -32000, ShowActivated = false
                };
                ((TextBox)dialog.FindName("ServerBox")).Text = "dc.example.test";
                ((CheckBox)dialog.FindName("CurrentIdentityBox")).IsChecked = true;
                dialog.SourceInitialized += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
                    Descendants((DependencyObject)dialog.Content).OfType<Button>().Single(x => x.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                Assert(dialog.ShowDialog() == true && dialog.Settings is { AllowWrites: true }, "AD connection allows writes without an extra enable prompt for saved preference: " + savedWrites);
                using var settings = dialog.Settings!;
                Assert(settings.Server == "dc.example.test" && settings.UseCurrentIdentity && settings.Password is null, "Writable connection preserves validation and current identity without storing a password");
                Assert(preferences.Load("AD").AllowWrites, "Legacy readonly preference cannot silently disable writes on the next connection");
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void Render(Window window, string name, int width = 1390, int height = 840)
    {
        // Render off-screen; tests do not show windows or send input to the user's desktop.
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        window.UpdateLayout();
        using var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("DomainDesk offscreen test")
        { Width = width, Height = height, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        var surface = new Border { Background = window.Background, Child = root };
        System.Windows.Documents.TextElement.SetForeground(surface, window.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(surface, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(surface, window.FontSize);
        host.RootVisual = surface;
        root.Width = width;
        root.Height = height;
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        Assert(root.ActualWidth == width && root.ActualHeight == height, $"Offscreen preview uses intended desktop dimensions: {root.ActualWidth}x{root.ActualHeight}");
        root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        if (name == "directory-unified-1390.png" && window is MainWindow directoryWindow)
            TestAutomaticScrollbars(directoryWindow, surface);
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        if (name == "directory-unified-1920.png")
        {
            void CheckTreeLayout(DependencyObject v)
            {
                if (v is TreeViewItem t && t.DataContext is DirectoryNode n)
                {
                    var h = (FrameworkElement)t.Template.FindName("HeaderContent", t);
                    var exp = (FrameworkElement)t.Template.FindName("Expander", t);
                    Assert(Math.Abs(h.TranslatePoint(new Point(), t).X - 22) < 0.1 &&
                        Math.Abs(exp.TranslatePoint(new Point(), t).X - 2) < 0.1,
                        "Tree header/chevron alignment stays fixed even with expanded children: " + n.Name);
                    Assert(h.ActualHeight == 26, "Compact tree row height: " + n.Name);
                }
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(v); i++) CheckTreeLayout(VisualTreeHelper.GetChild(v, i));
            }
            CheckTreeLayout(root);
        }
        if (name == "directory-unified-1120.png" && window is MainWindow m)
        {
            var g = (DataGrid)m.FindName("ObjectsGrid");
            Assert(g.Columns[1].ActualWidth >= 70 && g.FrozenColumnCount == 2, "Narrow unified grid keeps object name/type readable and frozen while other columns scroll");
        }
        image.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts", name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
        host.RootVisual = null;
        surface.Child = null;
        root.ClearValue(FrameworkElement.WidthProperty);
        root.ClearValue(FrameworkElement.HeightProperty);
        window.Content = root;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject node)
    {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(node, i))) yield return child;
    }

    private static void TestAutomaticScrollbars(MainWindow window, FrameworkElement surface)
    {
        var tree = (TreeView)window.FindName("OuTree");
        var treeScroll = VisualDescendants(tree).OfType<ScrollViewer>().First();
        var details = (ScrollViewer)window.FindName("DetailsScroll");
        Assert(treeScroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed && treeScroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, "Tree hides both scrollbars when its labels and rows fit");
        Assert(details.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, "Details hide the scrollbar when all fields and group names fit");
        var groups = (TextBlock)window.FindName("DetailGroups");
        var originalGroups = groups.Text;
        groups.Text = string.Join("\n", Enumerable.Repeat("Additional group", 100));
        surface.UpdateLayout();
        Assert(details.ComputedVerticalScrollBarVisibility == Visibility.Visible, "Overflowing group details remain accessible by scrolling");
        groups.Text = originalGroups;
        surface.UpdateLayout();
        Assert(details.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, "Details scrollbar disappears again after content fits");
        var label = VisualDescendants(tree).OfType<TextBlock>().First(x => x.DataContext is DirectoryNode n && x.Text == n.Name);
        var originalName = label.Text;
        label.SetCurrentValue(TextBlock.TextProperty, new string('W', 150));
        surface.UpdateLayout();
        Assert(treeScroll.ComputedHorizontalScrollBarVisibility == Visibility.Visible, "Long tree labels can be read using the overflow scrollbar");
        label.SetCurrentValue(TextBlock.TextProperty, originalName);
        tree.Height = 80;
        surface.UpdateLayout();
        Assert(treeScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, "Tree enables vertical scrolling when expanded rows exceed available height");
        tree.ClearValue(FrameworkElement.HeightProperty);
        surface.UpdateLayout();
        Assert(treeScroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed && treeScroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, "Tree scrollbars disappear again after contents fit");
    }
}




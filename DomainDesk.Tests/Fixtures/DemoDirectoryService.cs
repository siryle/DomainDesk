using System.Security;

namespace DomainDesk.Services;

// Local test data only; the desktop application never ships a demo workspace.
public sealed class DemoDirectoryService : IDirectoryService
{
    private readonly List<DirectoryItem> _items = [];
    public DirectorySession Session { get; }
    private readonly List<DirectoryContainer> _containers = [];

    public DemoDirectoryService(bool allowWrites = true, bool useTls = true)
    {
        Session = new("dc01.example.test", "DC=example,DC=test", "DEMO\\operator", true, allowWrites, useTls);
        string people = "OU=People," + Session.BaseDn;
        string engineering = "OU=技术研发部," + people;
        foreach (var dn in new[] { Session.BaseDn, people, engineering, "OU=软件开发组," + engineering,
            "OU=质量保障组," + engineering, "OU=产品设计部," + people, "OU=基础架构部," + people,
            "OU=人力资源部," + people, "OU=待入职," + people, "OU=Devices," + Session.BaseDn,
            "OU=Groups," + Session.BaseDn, "CN=Users," + Session.BaseDn, "CN=Computers," + Session.BaseDn,
            "OU=Domain Controllers," + Session.BaseDn, "CN=ForeignSecurityPrincipals," + Session.BaseDn,
            "CN=Managed Service Accounts," + Session.BaseDn })
            _containers.Add(new(Guid.NewGuid(), dn, dn == Session.BaseDn));
        _containers.Add(new(Guid.NewGuid(), "CN=Builtin," + Session.BaseDn) { ObjectClass = "builtinDomain" });
        foreach (var name in new[] { "System", "Program Data", "Keys" })
            _containers.Add(new(Guid.NewGuid(), "CN=" + name + "," + Session.BaseDn) { IsAdvancedViewOnly = true, ObjectClass = "container" });
        _containers.Add(new(Guid.NewGuid(), "CN=Microsoft,CN=Program Data," + Session.BaseDn) { ObjectClass = "container" });
        string[] names = ["陈思远", "林知夏", "王亦舟", "张明轩", "刘可欣", "赵景行", "周予安", "吴沐言", "郑书宁", "许星辰", "孙嘉禾", "何清越"];
        string[] accounts = ["chen.sy", "lin.zx", "wang.yz", "zhang.mx", "liu.kx", "zhao.jx", "zhou.ya", "wu.my", "zheng.sn", "xu.xc", "sun.jh", "he.qy"];
        string[] departments = ["技术研发部", "产品设计部", "基础架构部", "人力资源部"];
        for (var i = 0; i < names.Length; i++)
            _items.Add(new DirectoryItem
            {
                Id = Guid.NewGuid(), Kind = DirectoryKind.Users, Name = names[i], Account = accounts[i],
                Email = $"{accounts[i]}@example.test", Department = departments[i % 4], Enabled = i != 8,
                Locked = i is 2 or 6, LastLogon = i == 8 ? null : DateTime.UtcNow.AddHours(-i * 4 - 1),
                DistinguishedName = $"CN={names[i]}," + (i == 4 ? "OU=软件开发组," + engineering : i == 8 ? "OU=质量保障组," + engineering : $"OU={departments[i % 4]}," + people), Description = "演示账号，不对应真实域用户",
                MemberOf = ["CN=All-Staff,OU=Groups,DC=example,DC=test", $"CN=Team-{i % 4 + 1},OU=Groups,DC=example,DC=test"]
            });
        for (var i = 1; i <= 8; i++)
            _items.Add(new DirectoryItem
            {
                Id = Guid.NewGuid(), Kind = DirectoryKind.Computers, Name = $"WS-ENGINEERING-{i:000}", Account = $"WS-ENG-{i:000}$",
                OperatingSystem = i <= 6 ? "Windows 11 Enterprise" : "Windows Server 2022 Standard",
                Enabled = i != 7, LastLogon = DateTime.UtcNow.AddDays(-i),
                DistinguishedName = $"CN=WS-ENGINEERING-{i:000}," + (i == 8 ? engineering : "OU=Devices," + Session.BaseDn)
            });
        string[] groups = ["All-Staff", "Engineering", "Product-Design", "IT-Operations", "VPN-Users", "Helpdesk"];
        foreach (var name in groups)
            _items.Add(new DirectoryItem
            {
                Id = Guid.NewGuid(), Kind = DirectoryKind.Groups, Name = name, Account = name,
                Description = "演示组 · 仅供界面体验", Enabled = true, GroupType = unchecked((int)0x80000002),
                DistinguishedName = $"CN={name}," + (name == "Helpdesk" ? engineering : "OU=Groups," + Session.BaseDn)
            });
    }

    public Task<QueryResult> QueryAsync(DirectoryKind kind, string search, DirectoryQueryScope? scope = null)
    {
        DirectoryQueries.Request(Session, kind, search, scope);
        if (scope is not null && !_containers.Contains(scope.Container)) throw new InvalidOperationException("所选目录已失效，请刷新目录树。");
        var value = search.Trim();
        var items = _items.Where(x => (kind == DirectoryKind.All || x.Kind == kind) &&
            (scope is null || DirectoryQueries.ContainsObject(x.DistinguishedName, scope)) &&
            new[] { x.Name, x.Account, x.Email, x.Department }.Any(s => s.Contains(value, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Name).ToArray();
        return Task.FromResult(new QueryResult(items, false));
    }

    public Task ExecuteAsync(DirectoryItem target, AccountAction action, SecureString? password = null, bool mustChangePassword = true)
    {
        DirectoryMutations.RequireWrites(Session);
        if (target.Kind != DirectoryKind.Users) throw new InvalidOperationException("仅支持用户操作。");
        var index = _items.FindIndex(x => x.Id == target.Id && x.Kind == DirectoryKind.Users);
        if (index < 0) throw new InvalidOperationException("演示用户不存在。");
        if (action == AccountAction.ResetPassword && (password is null || password.Length == 0))
            throw new ArgumentException("新密码不能为空。");
        _items[index] = action switch
        {
            AccountAction.Unlock => _items[index] with { Locked = false },
            AccountAction.Enable => _items[index] with { Enabled = true },
            AccountAction.Disable => _items[index] with { Enabled = false },
            _ => _items[index]
        };
        return Task.CompletedTask;
    }

    public Task<ContainerResult> GetContainersAsync() => Task.FromResult(new ContainerResult(_containers.ToArray(), false));

    public Task CreateUserAsync(NewUserSpec spec, SecureString password)
    {
        DirectoryMutations.RequireWrites(Session);
        DirectoryMutations.ValidateUser(spec);
        if (!_containers.Contains(spec.Container)) throw new InvalidOperationException("容器不存在。");
        if (password.Length == 0) throw new ArgumentException("初始密码不能为空。");
        var dn = DirectoryMutations.NewUserDn(spec);
        if (_items.Any(x => x.Account.Equals(spec.Account, StringComparison.OrdinalIgnoreCase) || x.DistinguishedName.Equals(dn, StringComparison.OrdinalIgnoreCase) ||
            x.UserPrincipalName.Equals(spec.UserPrincipalName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("账号已存在。");
        _items.Add(new DirectoryItem { Id = Guid.NewGuid(), Kind = DirectoryKind.Users, Name = spec.Name, Account = spec.Account,
            DistinguishedName = dn, UserPrincipalName = spec.UserPrincipalName, Email = spec.Email, Department = spec.Department, Enabled = spec.Enable });
        return Task.CompletedTask;
    }

    public Task CreateComputerAsync(NewComputerSpec spec)
    {
        DirectoryMutations.RequireWrites(Session);
        DirectoryMutations.ValidateComputer(spec);
        DirectoryMutations.ValidateCreationParent(Session, spec.Container);
        var dn = DirectoryMutations.NewComputerDn(spec);
        RequireNewObject(spec.Container, spec.Account, dn);
        _items.Add(new DirectoryItem { Id = Guid.NewGuid(), Kind = DirectoryKind.Computers, Name = spec.Name, Account = spec.Account,
            DistinguishedName = dn, Description = spec.Description, Location = spec.Location, Enabled = false });
        return Task.CompletedTask;
    }

    public Task CreateGroupAsync(NewGroupSpec spec)
    {
        DirectoryMutations.RequireWrites(Session);
        DirectoryMutations.ValidateGroup(spec);
        DirectoryMutations.ValidateCreationParent(Session, spec.Container);
        var dn = DirectoryMutations.NewGroupDn(spec);
        RequireNewObject(spec.Container, spec.Account, dn);
        _items.Add(new DirectoryItem { Id = Guid.NewGuid(), Kind = DirectoryKind.Groups, Name = spec.Name, Account = spec.Account,
            DistinguishedName = dn, Description = spec.Description, GroupType = spec.GroupType });
        return Task.CompletedTask;
    }

    private void RequireNewObject(DirectoryContainer parent, string account, string dn)
    {
        if (!_containers.Contains(parent)) throw new InvalidOperationException("目标目录已失效，请刷新并重新选择。");
        if (_items.Any(x => x.Account.Equals(account, StringComparison.OrdinalIgnoreCase) || x.DistinguishedName.Equals(dn, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("账号或目标 DN 已存在；不会覆盖现有对象。");
    }

    public Task<QueryResult> GetGroupUsersAsync(DirectoryItem group)
    {
        var current = Find(group, DirectoryKind.Groups);
        return Task.FromResult(new QueryResult(_items.Where(x => x.Kind == DirectoryKind.Users &&
            x.MemberOf.Contains(current.DistinguishedName, StringComparer.OrdinalIgnoreCase)).OrderBy(x => x.Name).ToArray(), false));
    }

    public Task ChangeGroupUserAsync(DirectoryItem group, DirectoryItem user, bool add)
    {
        DirectoryMutations.RequireWrites(Session);
        var g = Find(group, DirectoryKind.Groups);
        var u = Find(user, DirectoryKind.Users);
        var members = u.MemberOf.ToList();
        var exists = members.Contains(g.DistinguishedName, StringComparer.OrdinalIgnoreCase);
        if (exists == add) throw new InvalidOperationException(add ? "用户已经是直接成员。" : "用户不是直接成员。");
        if (add) members.Add(g.DistinguishedName);
        else members.RemoveAll(x => x.Equals(g.DistinguishedName, StringComparison.OrdinalIgnoreCase));
        _items[_items.IndexOf(u)] = u with { MemberOf = members };
        return Task.CompletedTask;
    }

    public Task UpdateComputerAsync(DirectoryItem computer, ComputerChanges changes)
    {
        DirectoryMutations.RequireWrites(Session);
        var current = Find(computer, DirectoryKind.Computers);
        if (!current.CanManageComputer) throw new InvalidOperationException("拒绝修改域控制器或关键系统计算机。");
        DirectoryMutations.Computer(current.DistinguishedName, changes);
        _items[_items.IndexOf(current)] = current with { Description = changes.Description, Location = changes.Location };
        return Task.CompletedTask;
    }

    public Task DeleteComputerAsync(DirectoryItem computer)
    {
        DirectoryMutations.RequireWrites(Session);
        var current = Find(computer, DirectoryKind.Computers);
        if (!current.CanManageComputer) throw new InvalidOperationException("拒绝删除域控制器或关键系统计算机。");
        _items.Remove(current);
        return Task.CompletedTask;
    }

    private DirectoryItem Find(DirectoryItem item, DirectoryKind kind) =>
        _items.SingleOrDefault(x => x.Id == item.Id && x.Kind == kind) ?? throw new InvalidOperationException("目标已删除或类型不匹配。");

    public void Dispose() { }
}

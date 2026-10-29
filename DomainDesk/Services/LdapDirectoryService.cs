using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace DomainDesk.Services;

public sealed class LdapDirectoryService : IDirectoryService
{
    private readonly LdapConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public DirectorySession Session { get; }
    private const int ResultLimit = 5000;
    private static readonly string[] Attributes = ["objectGUID", "displayName", "name", "sAMAccountName", "userPrincipalName", "distinguishedName",
        "mail", "department", "description", "operatingSystem", "userAccountControl", "msDS-User-Account-Control-Computed",
        "lastLogonTimestamp", "memberOf", "location", "dNSHostName", "isCriticalSystemObject", "objectClass", "groupType"];

    private LdapDirectoryService(LdapConnection connection, DirectorySession session)
    {
        _connection = connection;
        Session = session;
    }

    public static Task<LdapDirectoryService> ConnectAsync(ConnectionSettings settings) => Task.Run(() =>
    {
        RemoteValidation.ValidateServer(settings.Server);
        var connection = new LdapConnection(new LdapDirectoryIdentifier(settings.Server, settings.Port))
        {
            AuthType = AuthType.Negotiate,
            Timeout = TimeSpan.FromSeconds(15)
        };
        try
        {
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            connection.SessionOptions.SecureSocketLayer = settings.UseTls;
            if (!settings.UseTls)
            {
                connection.SessionOptions.Signing = true;
                connection.SessionOptions.Sealing = true;
            }
            if (!settings.UseCurrentIdentity)
            {
                if (settings.Password is null) throw new ArgumentException("请输入连接密码。");
                var parts = settings.Username.Split('\\', 2);
                connection.Credential = parts.Length == 2
                    ? new NetworkCredential(parts[1], settings.Password, parts[0])
                    : new NetworkCredential(settings.Username, settings.Password);
            }
            connection.Bind();
            var root = (SearchResponse)connection.SendRequest(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "dnsHostName"));
            if (root.Entries.Count != 1) throw new InvalidOperationException("无法读取域 RootDSE。");
            var baseDn = Text(root.Entries[0], "defaultNamingContext");
            if (string.IsNullOrWhiteSpace(baseDn)) throw new InvalidOperationException("该服务器不是受支持的 Active Directory 域控制器。");
            var host = Text(root.Entries[0], "dnsHostName");
            if (!string.IsNullOrWhiteSpace(host)) RemoteValidation.ValidateServer(host);
            var identity = settings.UseCurrentIdentity ? WindowsIdentity.GetCurrent().Name : settings.Username;
            return new LdapDirectoryService(connection, new DirectorySession(
                string.IsNullOrEmpty(host) ? settings.Server : host, baseDn, identity, false, settings.AllowWrites, settings.UseTls));
        }
        catch (LdapException ex) when (ex.ErrorCode == 81)
        {
            connection.Dispose();
            throw new InvalidOperationException($"无法建立到 {settings.Server}:{settings.Port} 的 LDAP 会话（错误 81）。请检查该地址是否解析到真实域控、端口/防火墙，以及 LDAPS 证书和服务是否启用。若使用 IP，先尝试 LDAP 389；TLS 只在证书包含该 IP 时可用。当前 Windows 身份为 {WindowsIdentity.GetCurrent().Name}。", ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    });

    public async Task<QueryResult> QueryAsync(DirectoryKind kind, string search, DirectoryQueryScope? scope = null)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var results = new List<DirectoryItem>();
                var request = DirectoryQueries.Request(Session, kind, search, scope, Attributes);
                if (scope is not null)
                {
                    var selected = Resolve(scope.Container.Id, DirectoryQueries.ScopeFilter, "objectGUID");
                    if (!selected.DistinguishedName.Equals(scope.Container.DistinguishedName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("所选目录已移动或重命名，请刷新目录树后重新查询。");
                }
                var page = new PageResultRequestControl(500);
                request.Controls.Add(page);
                do
                {
                    var response = (SearchResponse)_connection.SendRequest(request);
                    foreach (SearchResultEntry entry in response.Entries)
                    {
                        if (results.Count == ResultLimit) return new QueryResult(results.OrderBy(x => x.Name).ToArray(), true);
                        results.Add(Map(entry, kind));
                    }
                    var paging = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                    if (paging is null) throw new InvalidOperationException("域控制器未返回分页控件，无法确认查询完整性。");
                    page.Cookie = paging.Cookie;
                } while (page.Cookie.Length > 0);
                return new QueryResult(results.OrderBy(x => x.Name).ToArray(), false);
            });
        }
        finally { _gate.Release(); }
    }

    public async Task ExecuteAsync(DirectoryItem target, AccountAction action, SecureString? password = null, bool mustChangePassword = true)
    {
        DirectoryMutations.RequireWrites(Session);
        if (target.Kind != DirectoryKind.Users) throw new InvalidOperationException("仅允许修改用户账号。");
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                // Resolve by immutable GUID so a renamed or moved account cannot redirect an operation.
                var entry = Resolve(target.Id, "(&(objectCategory=person)(objectClass=user))", "userAccountControl");
                var request = new ModifyRequest(entry.DistinguishedName);
                switch (action)
                {
                    case AccountAction.Unlock:
                        request.Modifications.Add(Replace("lockoutTime", "0"));
                        break;
                    case AccountAction.Enable:
                    case AccountAction.Disable:
                        if (!int.TryParse(Text(entry, "userAccountControl"), out var flags))
                            throw new InvalidOperationException("无法读取账号控制标志，已取消修改。");
                        flags = action == AccountAction.Disable ? flags | 2 : flags & ~2;
                        request.Modifications.Add(Replace("userAccountControl", flags.ToString(CultureInfo.InvariantCulture)));
                        break;
                    case AccountAction.ResetPassword:
                        if (password is null || password.Length == 0) throw new ArgumentException("新密码不能为空。");
                        ResetPassword(request, password, mustChangePassword);
                        return;
                    default: throw new ArgumentOutOfRangeException(nameof(action));
                }
                _connection.SendRequest(request);
            });
        }
        finally { _gate.Release(); }
    }

    private async Task<T> SerializedAsync<T>(Func<T> operation)
    {
        await _gate.WaitAsync();
        try { return await Task.Run(operation); }
        finally { _gate.Release(); }
    }

    private SearchResultEntry Resolve(Guid id, string type, params string[] attributes)
    {
        var result = (SearchResponse)_connection.SendRequest(new SearchRequest(Session.BaseDn,
            $"(&{type}{LdapFilters.GuidFilter(id)})", SearchScope.Subtree, attributes));
        if (result.Entries.Count != 1) throw new InvalidOperationException("目标对象已删除、类型不匹配或不可访问，请刷新列表。");
        return result.Entries[0];
    }

    private (List<SearchResultEntry> Entries, bool Truncated) SearchPaged(string filter, params string[] attributes)
    {
        var entries = new List<SearchResultEntry>();
        var request = new SearchRequest(Session.BaseDn, filter, SearchScope.Subtree, attributes);
        var page = new PageResultRequestControl(500);
        request.Controls.Add(page);
        do
        {
            var response = (SearchResponse)_connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
            {
                if (entries.Count == ResultLimit) return (entries, true);
                entries.Add(entry);
            }
            page.Cookie = response.Controls.OfType<PageResultResponseControl>().SingleOrDefault()?.Cookie
                ?? throw new InvalidOperationException("服务器未返回分页控件。");
        } while (page.Cookie.Length > 0);
        return (entries, false);
    }

    public Task<ContainerResult> GetContainersAsync() => SerializedAsync(() =>
    {
        var domain = (SearchResponse)_connection.SendRequest(new SearchRequest(Session.BaseDn, "(objectClass=domainDNS)", SearchScope.Base, "objectGUID"));
        if (domain.Entries.Count != 1) throw new InvalidOperationException("无法读取当前域根。");
        var root = new DirectoryContainer(new Guid((byte[])domain.Entries[0].Attributes["objectGUID"][0]), Session.BaseDn, true);
        var result = SearchPaged(DirectoryQueries.ContainerFilter, "objectGUID", "objectClass", "showInAdvancedViewOnly");
        return new ContainerResult(new[] { root }.Concat(result.Entries.Select(x => new DirectoryContainer(new Guid((byte[])x.Attributes["objectGUID"][0]), x.DistinguishedName)
            {
                ObjectClass = x.Attributes["objectClass"].GetValues(typeof(string)).Cast<string>()
                    .FirstOrDefault(c => c.Equals("organizationalUnit", StringComparison.OrdinalIgnoreCase) || c.Equals("builtinDomain", StringComparison.OrdinalIgnoreCase)) ?? "container",
                IsAdvancedViewOnly = x.Attributes.Contains("showInAdvancedViewOnly") &&
                    string.Equals(x.Attributes["showInAdvancedViewOnly"][0]?.ToString(), "TRUE", StringComparison.OrdinalIgnoreCase)
            }))
            .OrderBy(x => x.DistinguishedName).ToArray(), result.Truncated);
    });

    public async Task CreateUserAsync(NewUserSpec spec, SecureString password)
    {
        DirectoryMutations.RequireWrites(Session);
        DirectoryMutations.ValidateUser(spec);
        if (password.Length == 0) throw new ArgumentException("初始密码不能为空。");
        await SerializedAsync(() =>
        {
            var parent = Resolve(spec.Container.Id, "(|(objectClass=organizationalUnit)(&(objectClass=container)(cn=Users)))", "objectGUID");
            if (!parent.DistinguishedName.Equals(spec.Container.DistinguishedName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标容器已移动，请重新选择。");
            var duplicates = (SearchResponse)_connection.SendRequest(new SearchRequest(Session.BaseDn,
                $"(|(sAMAccountName={LdapFilters.Escape(spec.Account)})(userPrincipalName={LdapFilters.Escape(spec.UserPrincipalName)}))", SearchScope.Subtree, "objectGUID"));
            if (duplicates.Entries.Count > 0) throw new InvalidOperationException("登录名或 UPN 已存在，请更换。");
            var add = DirectoryMutations.CreateDisabledUser(spec);
            // Provision disabled first. Never delete on a later failure or retry a multi-step creation automatically.
            _connection.SendRequest(add);
            var stage = "读取新账号";
            try
            {
                var created = (SearchResponse)_connection.SendRequest(new SearchRequest(add.DistinguishedName, "(objectClass=user)", SearchScope.Base, "objectGUID"));
                if (created.Entries.Count != 1) throw new InvalidOperationException("无法读取新账号。");
                var id = new Guid((byte[])created.Entries[0].Attributes["objectGUID"][0]);
                stage = "设置初始密码";
                var entry = Resolve(id, "(&(objectCategory=person)(objectClass=user))", "userAccountControl");
                ResetPassword(new ModifyRequest(entry.DistinguishedName), password, spec.MustChangePassword);
                if (spec.Enable)
                {
                    stage = "启用账号";
                    entry = Resolve(id, "(&(objectCategory=person)(objectClass=user))", "userAccountControl");
                    if (!int.TryParse(Text(entry, "userAccountControl"), out var flags)) throw new InvalidOperationException("无法读取账号标志。");
                    var enable = new ModifyRequest(entry.DistinguishedName);
                    enable.Modifications.Add(Replace("userAccountControl", (flags & ~2).ToString(CultureInfo.InvariantCulture)));
                    _connection.SendRequest(enable);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"账号对象已经创建：{add.DistinguishedName}\n后续步骤“{stage}”失败或结果未知。新账号初始为停用状态；若启用请求已发出，最终状态需刷新核实。请勿重复新建，先查找此账号，再重置密码/启用。\n{ex.Message}", ex);
            }
            return true;
        });
    }

    public Task CreateComputerAsync(NewComputerSpec spec)
    {
        DirectoryMutations.RequireWrites(Session);
        DirectoryMutations.ValidateComputer(spec);
        DirectoryMutations.ValidateCreationParent(Session, spec.Container);
        return CreateDirectoryObjectAsync(spec.Container, spec.Account, DirectoryMutations.CreateComputer(spec), DirectoryKind.Computers);
    }

    public Task CreateGroupAsync(NewGroupSpec spec)
    {
        DirectoryMutations.RequireWrites(Session);
        DirectoryMutations.ValidateGroup(spec);
        DirectoryMutations.ValidateCreationParent(Session, spec.Container);
        return CreateDirectoryObjectAsync(spec.Container, spec.Account, DirectoryMutations.CreateGroup(spec), DirectoryKind.Groups);
    }

    private Task CreateDirectoryObjectAsync(DirectoryContainer snapshot, string account, AddRequest add, DirectoryKind kind) => SerializedAsync(() =>
    {
        var entry = Resolve(snapshot.Id, DirectoryQueries.ScopeFilter, "objectGUID", "objectClass");
        if (!entry.DistinguishedName.Equals(snapshot.DistinguishedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标目录已移动或重命名，请刷新并重新选择；未提交新建。");
        var classes = entry.Attributes["objectClass"].GetValues(typeof(string)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = new DirectoryContainer(snapshot.Id, entry.DistinguishedName)
        {
            ObjectClass = classes.Contains("organizationalUnit") ? "organizationalUnit" : classes.Contains("builtinDomain") ? "builtinDomain" : classes.Contains("container") ? "container" : "unsupported"
        };
        if (!(kind == DirectoryKind.Computers ? actual.CanCreateComputer : actual.CanCreateGroup))
            throw new InvalidOperationException("目标目录的实际类型不允许此次新建，请重新选择。");
        var duplicates = (SearchResponse)_connection.SendRequest(new SearchRequest(Session.BaseDn,
            $"(sAMAccountName={LdapFilters.Escape(account)})", SearchScope.Subtree, "objectGUID"));
        if (duplicates.Entries.Count > 0) throw new InvalidOperationException("该账号已被域内用户、计算机或组使用，请更换名称。不会覆盖现有对象。");
        // One LDAP Add only. AD enforces concurrent name collisions; never use an upsert, retry or rollback.
        _connection.SendRequest(add);
        return true;
    });

    public Task<QueryResult> GetGroupUsersAsync(DirectoryItem group) => SerializedAsync(() =>
    {
        if (group.Kind != DirectoryKind.Groups) throw new ArgumentException("请选择用户组。");
        var entry = Resolve(group.Id, "(objectCategory=group)", "objectGUID");
        // Search memberOf instead of a ranged 'member' attribute; lists direct user members, not nested/primary members.
        var result = SearchPaged($"(&(objectCategory=person)(objectClass=user)(memberOf={LdapFilters.Escape(entry.DistinguishedName)}))", Attributes);
        return new QueryResult(result.Entries.Select(x => Map(x, DirectoryKind.Users)).OrderBy(x => x.Name).ToArray(), result.Truncated);
    });

    public async Task ChangeGroupUserAsync(DirectoryItem group, DirectoryItem user, bool add)
    {
        DirectoryMutations.RequireWrites(Session);
        if (group.Kind != DirectoryKind.Groups || user.Kind != DirectoryKind.Users) throw new ArgumentException("只允许修改组的直接用户成员。");
        await SerializedAsync(() =>
        {
            var g = Resolve(group.Id, "(objectCategory=group)", "objectGUID");
            var u = Resolve(user.Id, "(&(objectCategory=person)(objectClass=user))", "objectGUID");
            _connection.SendRequest(DirectoryMutations.GroupUser(g.DistinguishedName, u.DistinguishedName, add));
            return true;
        });
    }

    private SearchResultEntry ResolveComputer(DirectoryItem computer)
    {
        if (computer.Kind != DirectoryKind.Computers) throw new ArgumentException("请选择计算机对象。");
        var entry = Resolve(computer.Id, "(objectCategory=computer)", "userAccountControl", "isCriticalSystemObject");
        if (!int.TryParse(Text(entry, "userAccountControl"), out var flags) || (flags & (8192 | 67108864)) != 0 ||
            Text(entry, "isCriticalSystemObject").Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝修改域控制器或关键系统计算机对象。");
        return entry;
    }

    public async Task UpdateComputerAsync(DirectoryItem computer, ComputerChanges changes)
    {
        DirectoryMutations.RequireWrites(Session);
        await SerializedAsync(() =>
        {
            var entry = ResolveComputer(computer);
            _connection.SendRequest(DirectoryMutations.Computer(entry.DistinguishedName, changes));
            return true;
        });
    }

    public async Task DeleteComputerAsync(DirectoryItem computer)
    {
        DirectoryMutations.RequireWrites(Session);
        await SerializedAsync(() =>
        {
            var entry = ResolveComputer(computer);
            // Leaf-only delete: never attach TreeDeleteControl or silently remove child objects.
            _connection.SendRequest(new DeleteRequest(entry.DistinguishedName));
            return true;
        });
    }

    private void ResetPassword(ModifyRequest request, SecureString password, bool mustChangePassword)
    {
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(password);
        var chars = new char[password.Length + 2];
        byte[]? bytes = null;
        try
        {
            chars[0] = chars[^1] = '"';
            Marshal.Copy(ptr, chars, 1, password.Length);
            bytes = Encoding.Unicode.GetBytes(chars);
            var modification = new DirectoryAttributeModification { Name = "unicodePwd", Operation = DirectoryAttributeOperation.Replace };
            modification.Add(bytes);
            request.Modifications.Add(modification);
            // Both changes share one atomic LDAP Modify; never report partial success.
            if (mustChangePassword) request.Modifications.Add(Replace("pwdLastSet", "0"));
            _connection.SendRequest(request);
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            Array.Clear(chars);
            if (bytes is not null) Array.Clear(bytes);
        }
    }

    private static DirectoryAttributeModification Replace(string name, string value)
    {
        var result = new DirectoryAttributeModification { Name = name, Operation = DirectoryAttributeOperation.Replace };
        result.Add(value);
        return result;
    }

    private static DirectoryItem Map(SearchResultEntry e, DirectoryKind kind)
    {
        if (kind == DirectoryKind.All)
            kind = LdapFilters.Classify(e.Attributes.Contains("objectClass") ? e.Attributes["objectClass"].GetValues(typeof(string)).Cast<string>() : []);
        int.TryParse(Text(e, "userAccountControl"), out var flags);
        int.TryParse(Text(e, "msDS-User-Account-Control-Computed"), out var computed);
        int.TryParse(Text(e, "groupType"), out var groupType);
        DateTime? lastLogon = null;
        if (long.TryParse(Text(e, "lastLogonTimestamp"), out var ticks) && ticks > 0 && ticks <= 2650467743999999999L)
            lastLogon = DateTime.FromFileTimeUtc(ticks);
        return new DirectoryItem
        {
            Id = new Guid((byte[])e.Attributes["objectGUID"][0]), Kind = kind,
            Name = string.IsNullOrWhiteSpace(Text(e, "displayName")) ? Text(e, "name") : Text(e, "displayName"),
            Account = Text(e, "sAMAccountName"), UserPrincipalName = Text(e, "userPrincipalName"), DistinguishedName = e.DistinguishedName,
            Email = Text(e, "mail"), Department = Text(e, "department"), Description = Text(e, "description"),
            OperatingSystem = Text(e, "operatingSystem"), Enabled = (flags & 2) == 0, Locked = (computed & 16) != 0,
            Location = Text(e, "location"), DnsHostName = Text(e, "dNSHostName"), GroupType = groupType,
            IsDomainController = (flags & (8192 | 67108864)) != 0,
            IsCriticalSystemObject = Text(e, "isCriticalSystemObject").Equals("TRUE", StringComparison.OrdinalIgnoreCase),
            LastLogon = lastLogon,
            MemberOf = e.Attributes.Contains("memberOf") ? e.Attributes["memberOf"].GetValues(typeof(string)).Cast<string>().ToArray() : []
        };
    }

    private static string Text(SearchResultEntry e, string key) =>
        e.Attributes.Contains(key) && e.Attributes[key].Count > 0 ? e.Attributes[key][0]?.ToString() ?? "" : "";

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}

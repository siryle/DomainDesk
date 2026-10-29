using System.Security;
using DomainDesk.Services;

namespace DomainDesk;

// All is a query selector; returned objects always carry their actual type.
public enum DirectoryKind { Users, Computers, Groups, All }
public enum AccountAction { Unlock, Enable, Disable, ResetPassword }
public enum AdGroupScope { Global = 2, DomainLocal = 4, Universal = 8 }

public sealed record DirectoryItem
{
    public Guid Id { get; init; }
    public DirectoryKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Account { get; init; } = "";
    public string UserPrincipalName { get; init; } = "";
    public string DistinguishedName { get; init; } = "";
    public string Email { get; init; } = "";
    public string Department { get; init; } = "";
    public string Description { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string Location { get; init; } = "";
    public string DnsHostName { get; init; } = "";
    public bool IsCriticalSystemObject { get; init; }
    public bool IsDomainController { get; init; }
    public int GroupType { get; init; }
    public string GroupTypeLabel => GroupType == 0 ? "" : $"{DirectoryMutations.GroupScopeLabel((AdGroupScope)(GroupType & 14))} / {(GroupType < 0 ? "安全组" : "通讯组")}";
    public string DisplayName => Kind == DirectoryKind.Computers ? Name.TrimEnd('$') : Name;
    public string DisplayAccount => Kind == DirectoryKind.Computers ? Account.TrimEnd('$') : Account;
    public string KindLabel => Kind switch { DirectoryKind.Users => "用户", DirectoryKind.Groups => "用户组", DirectoryKind.Computers => "计算机", _ => throw new InvalidOperationException("目录对象缺少具体类型。") };
    public bool CanManageComputer => Kind == DirectoryKind.Computers && !IsCriticalSystemObject && !IsDomainController;
    public bool Enabled { get; init; }
    public bool Locked { get; init; }
    public DateTime? LastLogon { get; init; }
    public IReadOnlyList<string> MemberOf { get; init; } = [];
    public string Status => Kind == DirectoryKind.Groups ? "—" : Locked ? "已锁定" : Enabled ? "已启用" : "已停用";
    public string Detail => Kind switch
    {
        DirectoryKind.Computers => OperatingSystem,
        DirectoryKind.Groups => Description,
        _ => Department
    };
    public string LastLogonText => LastLogon?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "无记录";
    public string ContainerDn => DistinguishedNames.Parent(DistinguishedName);
    public string ContainerName => DistinguishedNames.Name(ContainerDn);
}

public sealed class ConnectionSettings : IDisposable
{
    public string Server { get; init; } = "";
    public int Port => UseTls ? 636 : 389;
    public bool UseTls { get; init; } = true;
    public bool UseCurrentIdentity { get; init; } = true;
    public string Username { get; init; } = "";
    public SecureString? Password { get; init; }
    public bool AllowWrites { get; init; }
    public void Dispose() => Password?.Dispose();
}

public sealed record DirectorySession(string Server, string BaseDn, string Identity, bool IsDemo, bool AllowWrites, bool UseTls);
public sealed record QueryResult(IReadOnlyList<DirectoryItem> Items, bool Truncated);
public sealed record DirectoryContainer(Guid Id, string DistinguishedName, bool IsDomainRoot = false)
{
    public string ObjectClass { get; init; } = "";
    public bool IsAdvancedViewOnly { get; init; }
    public string Name => IsDomainRoot ? DistinguishedNames.DomainName(DistinguishedName) : DistinguishedNames.Name(DistinguishedName);
    public bool IsOrganizationalUnit => ObjectClass.Length == 0 ? DistinguishedName.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) : ObjectClass.Equals("organizationalUnit", StringComparison.OrdinalIgnoreCase);
    private bool IsContainerNamed(string name) => (ObjectClass.Length == 0 || ObjectClass.Equals("container", StringComparison.OrdinalIgnoreCase)) &&
        DistinguishedName.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) && Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    public bool CanCreateUser => !IsDomainRoot && Id != Guid.Empty && (IsOrganizationalUnit || IsContainerNamed("Users"));
    public bool CanCreateGroup => CanCreateUser;
    public bool CanCreateComputer => !IsDomainRoot && Id != Guid.Empty && (IsOrganizationalUnit || IsContainerNamed("Computers"));
}
public sealed record DirectoryQueryScope(DirectoryContainer Container, bool IncludeDescendants);
public sealed record ContainerResult(IReadOnlyList<DirectoryContainer> Items, bool Truncated);
public sealed record NewUserSpec(DirectoryContainer Container, string Name, string Account, string UserPrincipalName,
    string Email, string Department, bool Enable, bool MustChangePassword);
public sealed record ComputerChanges(string Description, string Location);
public sealed record NewComputerSpec(DirectoryContainer Container, string Name, string Description, string Location)
{
    public string Account => Name + "$";
}
public sealed record NewGroupSpec(DirectoryContainer Container, string Name, string Account, AdGroupScope Scope, bool SecurityEnabled, string Description)
{
    public int GroupType => (int)Scope | (SecurityEnabled ? unchecked((int)0x80000000) : 0);
}
public sealed record AuditEntry(DateTimeOffset Time, string Mode, string Operator, string Server, string Action,
    string Target, string Result, string Message)
{
    public string TimeText => Time.ToLocalTime().ToString("MM-dd HH:mm:ss");
}

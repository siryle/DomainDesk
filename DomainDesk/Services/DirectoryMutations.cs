using System.DirectoryServices.Protocols;
using System.Text;
using System.Globalization;

namespace DomainDesk.Services;

public static class DirectoryMutations
{
    public static void RequireWrites(DirectorySession session)
    {
        if (!session.AllowWrites) throw new InvalidOperationException("当前连接为只读模式，请重新连接并明确允许修改。");
    }

    public static void ValidateUser(NewUserSpec spec)
    {
        if (!spec.Container.CanCreateUser || string.IsNullOrWhiteSpace(spec.Container.DistinguishedName))
            throw new ArgumentException("请选择目标 OU / Users 容器。");
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 64 || spec.Name.Any(char.IsControl))
            throw new ArgumentException("姓名/CN 必须为 1–64 个字符，不能包含控制字符。");
        if (string.IsNullOrWhiteSpace(spec.Account) || spec.Account.Length > 20 || spec.Account.EndsWith('.') ||
            spec.Account.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "\"/\\[]:;|=,+*?<>@$".Contains(c)))
            throw new ArgumentException("登录名必须为 1–20 个字符，不得包含空格及 AD 特殊字符或 $ 后缀。");
        if (string.IsNullOrWhiteSpace(spec.UserPrincipalName) || spec.UserPrincipalName.Count(c => c == '@') != 1 ||
            spec.UserPrincipalName.StartsWith('@') || spec.UserPrincipalName.EndsWith('@') || spec.UserPrincipalName.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("请输入完整用户登录名 UPN，例如 user@corp.example.com。");
        ValidateText(spec.Email, 256, "邮箱");
        ValidateText(spec.Department, 64, "部门");
    }

    public static void ValidateText(string value, int limit, string label)
    {
        if (value.Length > limit || value.Any(char.IsControl)) throw new ArgumentException($"{label}不能超过 {limit} 个字符或包含控制字符。");
    }

    public static void ValidateComputer(NewComputerSpec spec)
    {
        if (!spec.Container.CanCreateComputer) throw new ArgumentException("请选择目标 OU / Computers 容器。");
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 15 ||
            spec.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) ||
            !char.IsAsciiLetterOrDigit(spec.Name[0]) || !char.IsAsciiLetterOrDigit(spec.Name[^1]) || spec.Name.All(char.IsDigit))
            throw new ArgumentException("计算机名须为 1–15 个英文字母、数字或连字符，首尾为字母/数字，不能为纯数字；不要输入 $ 或域名后缀。");
        ValidateText(spec.Description, 1024, "描述");
        ValidateText(spec.Location, 1024, "位置");
    }

    public static void ValidateGroup(NewGroupSpec spec)
    {
        if (!spec.Container.CanCreateGroup) throw new ArgumentException("请选择目标 OU / Users 容器，不允许在 Builtin 中新建组。");
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 64 || spec.Name.Any(char.IsControl))
            throw new ArgumentException("组名称/CN 必须为 1–64 个字符，不能包含控制字符。");
        if (string.IsNullOrWhiteSpace(spec.Account) || spec.Account.Length > 20 || spec.Account != spec.Account.Trim() || spec.Account.EndsWith('.') ||
            spec.Account.Any(c => char.IsControl(c) || "\"/\\[]:;|=,+*?<>@$".Contains(c)))
            throw new ArgumentException("组账号须为 1–20 个字符，不含首尾空格、AD 特殊字符或 $ 后缀。");
        if (!Enum.IsDefined(spec.Scope)) throw new ArgumentException("请选择全局、域本地或通用组作用域。");
        ValidateText(spec.Description, 1024, "描述");
    }

    public static string GroupScopeLabel(AdGroupScope scope) => scope switch
    {
        AdGroupScope.Global => "全局", AdGroupScope.DomainLocal => "域本地", AdGroupScope.Universal => "通用", _ => "未知作用域"
    };

    public static void ValidateCreationParent(DirectorySession session, DirectoryContainer parent)
    {
        if (parent.Id == Guid.Empty || !DistinguishedNames.IsWithin(parent.DistinguishedName, session.BaseDn))
            throw new ArgumentException("目标目录不是当前域内的有效对象，请刷新目录树。");
    }

    public static string NewComputerDn(NewComputerSpec spec) => $"CN={EscapeRdn(spec.Name)},{spec.Container.DistinguishedName}";
    public static string NewGroupDn(NewGroupSpec spec) => $"CN={EscapeRdn(spec.Name)},{spec.Container.DistinguishedName}";

    public static AddRequest CreateComputer(NewComputerSpec spec)
    {
        ValidateComputer(spec);
        var request = new AddRequest(NewComputerDn(spec));
        request.Attributes.Add(new DirectoryAttribute("objectClass", "top", "person", "organizationalPerson", "user", "computer"));
        request.Attributes.Add(new DirectoryAttribute("cn", spec.Name));
        request.Attributes.Add(new DirectoryAttribute("sAMAccountName", spec.Account));
        // Pre-stage a disabled member-computer account; never set a predictable password or delegation flags.
        request.Attributes.Add(new DirectoryAttribute("userAccountControl", "4098"));
        if (spec.Description.Length > 0) request.Attributes.Add(new DirectoryAttribute("description", spec.Description));
        if (spec.Location.Length > 0) request.Attributes.Add(new DirectoryAttribute("location", spec.Location));
        return request;
    }

    public static AddRequest CreateGroup(NewGroupSpec spec)
    {
        ValidateGroup(spec);
        var request = new AddRequest(NewGroupDn(spec));
        request.Attributes.Add(new DirectoryAttribute("objectClass", "top", "group"));
        request.Attributes.Add(new DirectoryAttribute("cn", spec.Name));
        request.Attributes.Add(new DirectoryAttribute("displayName", spec.Name));
        request.Attributes.Add(new DirectoryAttribute("sAMAccountName", spec.Account));
        request.Attributes.Add(new DirectoryAttribute("groupType", spec.GroupType.ToString(CultureInfo.InvariantCulture)));
        if (spec.Description.Length > 0) request.Attributes.Add(new DirectoryAttribute("description", spec.Description));
        return request;
    }

    public static string EscapeRdn(string value)
    {
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0') { result.Append("\\00"); continue; }
            if (",+\"\\<>;=".Contains(c) || (i == 0 && c is ' ' or '#') || (i == value.Length - 1 && c == ' ')) result.Append('\\');
            result.Append(c);
        }
        return result.ToString();
    }

    public static string NewUserDn(NewUserSpec spec) => $"CN={EscapeRdn(spec.Name)},{spec.Container.DistinguishedName}";

    public static AddRequest CreateDisabledUser(NewUserSpec spec)
    {
        ValidateUser(spec);
        var request = new AddRequest(NewUserDn(spec));
        request.Attributes.Add(new DirectoryAttribute("objectClass", "top", "person", "organizationalPerson", "user"));
        request.Attributes.Add(new DirectoryAttribute("cn", spec.Name));
        request.Attributes.Add(new DirectoryAttribute("displayName", spec.Name));
        request.Attributes.Add(new DirectoryAttribute("sAMAccountName", spec.Account));
        request.Attributes.Add(new DirectoryAttribute("userPrincipalName", spec.UserPrincipalName));
        request.Attributes.Add(new DirectoryAttribute("userAccountControl", "514"));
        if (spec.Email.Length > 0) request.Attributes.Add(new DirectoryAttribute("mail", spec.Email));
        if (spec.Department.Length > 0) request.Attributes.Add(new DirectoryAttribute("department", spec.Department));
        return request;
    }

    public static ModifyRequest GroupUser(string groupDn, string userDn, bool add)
    {
        var request = new ModifyRequest(groupDn);
        var modification = new DirectoryAttributeModification { Name = "member", Operation = add ? DirectoryAttributeOperation.Add : DirectoryAttributeOperation.Delete };
        modification.Add(userDn);
        request.Modifications.Add(modification);
        return request;
    }

    public static ModifyRequest Computer(string dn, ComputerChanges changes)
    {
        ValidateText(changes.Description, 1024, "描述");
        ValidateText(changes.Location, 1024, "位置");
        var request = new ModifyRequest(dn);
        request.Modifications.Add(Replace("description", changes.Description));
        request.Modifications.Add(Replace("location", changes.Location));
        return request;
    }

    public static DirectoryAttributeModification Replace(string name, string value)
    {
        var result = new DirectoryAttributeModification { Name = name, Operation = DirectoryAttributeOperation.Replace };
        if (value.Length > 0) result.Add(value);
        return result;
    }
}

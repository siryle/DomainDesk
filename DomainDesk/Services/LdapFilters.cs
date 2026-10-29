using System.Text;

namespace DomainDesk.Services;

public static class LdapFilters
{
    public static string Escape(string value)
    {
        var result = new StringBuilder();
        foreach (var c in value)
            result.Append(c switch
            {
                '\\' => "\\5c", '*' => "\\2a", '(' => "\\28", ')' => "\\29", '\0' => "\\00", _ => c.ToString()
            });
        return result.ToString();
    }

    public static string GuidFilter(Guid id) => "(objectGUID=" +
        string.Concat(id.ToByteArray().Select(b => $"\\{b:x2}")) + ")";

    public static string Build(DirectoryKind kind, string search)
    {
        var type = kind switch
        {
            DirectoryKind.Users => "(&(objectCategory=person)(objectClass=user))",
            DirectoryKind.Computers => "(objectCategory=computer)",
            DirectoryKind.Groups => "(objectCategory=group)",
            DirectoryKind.All => "(|(&(objectCategory=person)(objectClass=user))(objectCategory=computer)(objectCategory=group))",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (string.IsNullOrWhiteSpace(search)) return type;
        var value = Escape(search.Trim());
        return $"(&{type}(|(displayName=*{value}*)(name=*{value}*)(sAMAccountName=*{value}*)(mail=*{value}*)(department=*{value}*)))";
    }

    public static DirectoryKind Classify(IEnumerable<string> objectClasses)
    {
        var classes = objectClasses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Computer inherits user in the AD schema; test the more specific class first.
        if (classes.Contains("computer")) return DirectoryKind.Computers;
        if (classes.Contains("group")) return DirectoryKind.Groups;
        if (classes.Contains("user")) return DirectoryKind.Users;
        throw new InvalidOperationException("服务器返回了无法识别类型的目录对象，已停止查询。请刷新后重试。");
    }
}

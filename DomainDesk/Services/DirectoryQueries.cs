using System.DirectoryServices.Protocols;

namespace DomainDesk.Services;

public static class DirectoryQueries
{
    public const string ContainerFilter = "(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain))";
    public const string ScopeFilter = "(|(objectClass=domainDNS)(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain))";

    public static SearchRequest Request(DirectorySession session, DirectoryKind kind, string search, DirectoryQueryScope? scope, params string[] attributes)
    {
        var dn = session.BaseDn;
        var depth = SearchScope.Subtree;
        if (scope is not null)
        {
            if (scope.Container.Id == Guid.Empty || !DistinguishedNames.IsWithin(scope.Container.DistinguishedName, session.BaseDn))
                throw new ArgumentException("查询目录不是当前域内的有效对象，请刷新目录树。");
            dn = scope.Container.DistinguishedName;
            depth = scope.IncludeDescendants ? SearchScope.Subtree : SearchScope.OneLevel;
        }
        return new SearchRequest(dn, LdapFilters.Build(kind, search), depth, attributes);
    }
    public static bool ContainsObject(string objectDn, DirectoryQueryScope scope) => scope.IncludeDescendants
        ? DistinguishedNames.IsWithin(DistinguishedNames.Parent(objectDn), scope.Container.DistinguishedName)
        : DistinguishedNames.Parent(objectDn).Equals(scope.Container.DistinguishedName, StringComparison.OrdinalIgnoreCase);
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using DomainDesk.Services;

namespace DomainDesk;

public sealed class DirectoryNode(DirectoryContainer? container, string dn) : INotifyPropertyChanged
{
    public DirectoryContainer? Container { get; } = container;
    public string DistinguishedName { get; } = dn;
    public string Name => Container?.Name ?? DistinguishedNames.Name(DistinguishedName) + "（未列出）";
    public bool IsDomainRoot => Container?.IsDomainRoot == true;
    public bool IsOu => Container?.IsOrganizationalUnit == true;
    public bool IsUnavailable => Container is null;
    public string TypeLabel => IsDomainRoot ? "域" : IsOu ? "组织单位 (OU)" : IsUnavailable ? "未列出的父目录" : "容器";
    public string ToolTip => TypeLabel + "\n" + DistinguishedName;
    public List<DirectoryNode> Children { get; } = [];
    private bool _expanded, _selected, _visible = true, _lastSibling;
    public bool IsLastVisibleSibling { get => _lastSibling; private set { _lastSibling = value; Changed(); } }
    public bool IsExpanded { get => _expanded; set { _expanded = value; Changed(); } }
    public bool IsSelected { get => _selected; set { _selected = value; Changed(); } }
    public bool IsVisible { get => _visible; private set { _visible = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public IEnumerable<DirectoryNode> All() => new[] { this }.Concat(Children.SelectMany(x => x.All()));
    public bool Filter(string term, bool ancestorMatches = false)
    {
        var own = ancestorMatches || string.IsNullOrWhiteSpace(term) || Name.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);
        var childMatch = false;
        foreach (var child in Children) childMatch |= child.Filter(term, own);
        UpdateLastSibling();
        IsVisible = own || childMatch;
        if (!string.IsNullOrWhiteSpace(term) && childMatch) IsExpanded = true;
        return IsVisible;
    }
    private void UpdateLastSibling()
    {
        var last = Children.LastOrDefault(x => x.IsVisible);
        foreach (var child in Children) child.IsLastVisibleSibling = child == last;
    }
    public void ExpandPath(DirectoryNode target)
    {
        foreach (var node in All().Where(x => DistinguishedNames.IsWithin(target.DistinguishedName, x.DistinguishedName))) node.IsExpanded = true;
    }

    public static DirectoryNode Build(ContainerResult result, string baseDn)
    {
        var rootContainer = result.Items.SingleOrDefault(x => x.IsDomainRoot && x.DistinguishedName.Equals(baseDn, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("目录查询未返回当前域根，无法建立安全的查询范围。");
        var root = new DirectoryNode(rootContainer, baseDn) { IsExpanded = true };
        var nodes = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase) { [baseDn] = root };
        var ids = new HashSet<Guid>();
        foreach (var item in result.Items)
        {
            if (item.Id == Guid.Empty || !ids.Add(item.Id) || !DistinguishedNames.IsWithin(item.DistinguishedName, baseDn) ||
                item.IsDomainRoot != item.DistinguishedName.Equals(baseDn, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目录结果含重复标识或域外对象，已停止建立目录树。");
            if (item.Id == rootContainer.Id) continue;
            if (!nodes.TryAdd(item.DistinguishedName, new(item, item.DistinguishedName))) throw new InvalidOperationException("目录结果含重复 DN。");
        }
        DirectoryNode ParentFor(string dn)
        {
            if (nodes.TryGetValue(dn, out var existing)) return existing;
            var parent = new DirectoryNode(null, dn);
            nodes.Add(dn, parent);
            ParentFor(DistinguishedNames.Parent(dn)).Children.Add(parent);
            return parent;
        }
        foreach (var node in nodes.Values.ToArray().Where(x => x != root)) ParentFor(DistinguishedNames.Parent(node.DistinguishedName)).Children.Add(node);
        // Hide the entire advanced-only branch, not just its parent, without flattening the DN hierarchy.
        void NormalView(DirectoryNode node)
        {
            node.Children.RemoveAll(x => x.Container?.IsAdvancedViewOnly == true);
            node.Children.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            node.UpdateLastSibling();
            foreach (var child in node.Children) NormalView(child);
        }
        NormalView(root);
        return root;
    }
}

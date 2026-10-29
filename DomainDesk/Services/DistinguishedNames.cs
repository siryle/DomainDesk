using System.Text;

namespace DomainDesk.Services;

public static class DistinguishedNames
{
    public static string[] Parts(string dn)
    {
        if (string.IsNullOrEmpty(dn)) return [];
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\') { i++; continue; }
            if (dn[i] != ',') continue;
            parts.Add(dn[start..i]);
            start = i + 1;
        }
        parts.Add(dn[start..]);
        return parts.ToArray();
    }

    public static string Parent(string dn) => string.Join(",", Parts(dn).Skip(1));
    public static bool IsWithin(string dn, string parent)
    {
        var parts = Parts(dn);
        var suffix = Parts(parent);
        return suffix.Length > 0 && parts.Length >= suffix.Length &&
            parts.Skip(parts.Length - suffix.Length).SequenceEqual(suffix, StringComparer.OrdinalIgnoreCase);
    }
    public static string Name(string dn)
    {
        var first = Parts(dn).FirstOrDefault() ?? "";
        var separator = first.IndexOf('=');
        return Decode(separator < 0 ? first : first[(separator + 1)..]);
    }
    public static string DomainName(string dn) => string.Join(".", Parts(dn).Where(x => x.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(x => Decode(x[3..])));
    public static string Breadcrumb(string dn, string baseDn)
    {
        if (!IsWithin(dn, baseDn)) return dn;
        var local = Parts(dn).Take(Parts(dn).Length - Parts(baseDn).Length).Reverse().Select(Name);
        return string.Join("  /  ", new[] { DomainName(baseDn) }.Concat(local));
    }
    private static string Decode(string value)
    {
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length) { result.Append(value[i]); continue; }
            var bytes = new List<byte>();
            while (i + 2 < value.Length && value[i] == '\\' && byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                bytes.Add(b);
                i += 3;
            }
            if (bytes.Count > 0) { result.Append(Encoding.UTF8.GetString(bytes.ToArray())); i--; }
            else result.Append(value[++i]);
        }
        return result.ToString();
    }
}

using System.IO;
using System.Text;

namespace DomainDesk.Services;

public static class CsvExporter
{
    public static string Cell(string value)
    {
        // CSV quoting alone does not prevent spreadsheet formula execution.
        var trimmed = value.TrimStart();
        if (value.IndexOfAny(['\t', '\r', '\n']) >= 0 || (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])))
            value = "'" + value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    public static void Export(string path, IEnumerable<DirectoryItem> items, DirectorySession session)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("模式,服务器,类型,名称,登录名,状态,邮箱,部门或说明,最近登录时间戳,DN,所在目录,所在目录DN");
        foreach (var item in items)
            writer.WriteLine(string.Join(",", new[] { session.IsDemo ? "演示" : "真实域", session.Server, item.KindLabel, item.Name, item.Account,
                item.Status, item.Email, item.Detail, item.LastLogonText, item.DistinguishedName, item.ContainerName, item.ContainerDn }.Select(Cell)));
    }
}

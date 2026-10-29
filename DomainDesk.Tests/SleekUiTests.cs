using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;

internal static partial class Program
{
    private static void TestSleekUi(DomainDesk.MainWindow window)
    {
        var chrome = WindowChrome.GetWindowChrome(window);
        Assert(chrome is { CaptionHeight: 38, ResizeBorderThickness.Left: 6 } && window.WindowStyle == WindowStyle.None,
            "Teal title bar keeps native caption dragging and window resize hit testing");
        Assert(window.Content is Border { BorderThickness.Left: 1, BorderThickness.Bottom: 1, Child: Grid } frame &&
            frame.BorderBrush is SolidColorBrush { Color.A: 255 },
            "Custom chrome has a visible outer border enclosing the complete workspace");
        foreach (var name in new[] { "MinimizeButton", "MaximizeButton", "CloseButton" })
        {
            var button = (Button)window.FindName(name);
            Assert(button is not null && AutomationProperties.GetName(button).Length > 0 && button.IsEnabled,
                "Caption control is available and has an accessible name: " + name);
        }
        Assert(((TextBlock)window.FindName("ConnectionLabel")).Text == "尚未连接" &&
            ((SolidColorBrush)((System.Windows.Shapes.Ellipse)window.FindName("ConnectionDot")).Fill).Color == Color.FromRgb(129, 146, 152),
            "Disconnected startup does not display a green connected indicator");
        Assert(((SolidColorBrush)window.FindResource("TitleBar")).Color == Color.FromRgb(21, 61, 64) &&
            ((SolidColorBrush)window.FindResource("TreeSurface")).Color == Color.FromRgb(239, 244, 242),
            "Sleek title and tree use the approved palette");

        var grid = (DataGrid)window.FindName("ObjectsGrid");
        Assert(grid.Columns[0] is DataGridTemplateColumn { SortMemberPath: "DisplayName", ClipboardContentBinding: Binding { Path.Path: "DisplayName" } } &&
            grid.Columns[3] is DataGridTemplateColumn { SortMemberPath: "Status", ClipboardContentBinding: Binding { Path.Path: "Status" } },
            "Icon-enhanced table cells retain name/status sorting and clipboard text");
        Assert(grid.IsReadOnly && grid.FrozenColumnCount == 2 && grid.Columns.Count == 5,
            "Sleek table retains read-only rows, frozen identity columns and all five fields");
        var types = (ComboBox)window.FindName("ObjectTypeBox");
        types.ApplyTemplate();
        Assert(types.Template.FindName("PART_Popup", types) is Popup && types.Items.Count == 4,
            "Custom type selector retains the standard dropdown popup and all filters");
    }
}

using System.Windows;
using System.Windows.Media;

namespace WhatsApp.App;

public static class Theme
{
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["BgApp"] = "#161717", ["BgRail"] = "#1D1F1F", ["BgPanel"] = "#161717", ["BgHeader"] = "#1F2020",
        ["BgChat"] = "#0B141A", ["BgIn"] = "#242626", ["BgOut"] = "#144D37", ["BgInput"] = "#2A2D2E",
        ["BgHover"] = "#2A2D2E", ["TextMain"] = "#E9EDEF", ["TextSub"] = "#8D9496", ["Accent"] = "#21C063",
        ["Divider"] = "#2A2D2E", ["Tick"] = "#53BDEB"
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["BgApp"] = "#FFFFFF", ["BgRail"] = "#F0F2F5", ["BgPanel"] = "#FFFFFF", ["BgHeader"] = "#F0F2F5",
        ["BgChat"] = "#EFEAE2", ["BgIn"] = "#FFFFFF", ["BgOut"] = "#D9FDD3", ["BgInput"] = "#F0F2F5",
        ["BgHover"] = "#E9EDEF", ["TextMain"] = "#111B21", ["TextSub"] = "#667781", ["Accent"] = "#21C063",
        ["Divider"] = "#E9EDEF", ["Tick"] = "#53BDEB"
    };

    public static void Apply(bool dark)
    {
        foreach (var (key, hex) in dark ? Dark : Light)
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
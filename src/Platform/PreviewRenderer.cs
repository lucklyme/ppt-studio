using System.Xml.Linq;
using System.Globalization;
namespace Platform;
public static class PreviewRenderer
{
    static readonly XNamespace P = PptPackage.P, A = PptPackage.A, R = PptPackage.R, S = "http://www.w3.org/2000/svg";
    static double N(XElement? e, string n, double d = 0) => double.TryParse((string?)e?.Attribute(n), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : d;
    static string F(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
    static XElement El(string name, params object[] values) => new(S + name, values);
    static XAttribute At(string name, object value) => new(name, value is double d ? F(d) : value);
    public static string Render(PptPackage pkg, int index)
    {
        if (index < 1 || index > pkg.Slides.Length) throw new ArgumentException("页面不存在"); var width = 960d; var scale = width / pkg.Width; var height = pkg.Height * scale;
        var svg = El("svg", At("viewBox", $"0 0 960 {F(height)}"), At("width", 960), At("height", height)); svg.Add(El("rect", At("width", 960), At("height", height), At("fill", "white")));
        var part = pkg.Slides[index - 1]; var chain = new List<string> { part }; var current = part; for (var depth = 0; depth < 2; depth++) { var rel = pkg.Relations(current).FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith(depth == 0 ? "/slideLayout" : "/slideMaster") == true); if (rel == null) break; current = PptPackage.Resolve(current, (string)rel.Attribute("Target")!); chain.Insert(0, current); }
        foreach (var cp in chain)
        {
            var doc = pkg.Xml(cp); var bg = doc.Descendants(P + "bgPr").FirstOrDefault(); if (bg != null) { var color = Color(bg); if (color != "none") svg.Add(El("rect", At("width", 960), At("height", height), At("fill", color))); var blip = bg.Descendants(A + "blip").FirstOrDefault(); if (blip != null) DrawImage(pkg, cp, blip, svg, 0, 0, width, height); }
            var tree = doc.Descendants(P + "spTree").FirstOrDefault(); if (tree != null) DrawTree(pkg, cp, tree, svg, scale, cp != part);
        }
        return svg.ToString(SaveOptions.DisableFormatting);
    }
    static string Color(XElement? e, string fallback = "none")
    {
        if (e == null) return fallback; var fill = e.Element(A + "solidFill") ?? e.Element(A + "gradFill")?.Descendants(A + "gs").FirstOrDefault(); if (fill == null) return fallback; var rgb = fill.Descendants(A + "srgbClr").FirstOrDefault(); if (rgb != null) return "#" + (string?)rgb.Attribute("val"); var scheme = (string?)fill.Descendants(A + "schemeClr").FirstOrDefault()?.Attribute("val"); return scheme switch { "dk1" or "tx1" => "#172E45", "lt1" or "bg1" => "#ffffff", "accent1" => "#005A9C", "accent2" => "#00A589", "accent3" => "#F4B640", "accent4" => "#67B8DE", _ => fallback };
    }
    static void DrawTree(PptPackage pkg, string part, XElement tree, XElement target, double scale, bool inherited)
    {
        foreach (var e in tree.Elements())
        {
            if (e.Name == P + "grpSp")
            {
                var x = e.Element(P + "grpSpPr")?.Element(A + "xfrm"); var off = x?.Element(A + "off"); var ext = x?.Element(A + "ext"); var co = x?.Element(A + "chOff"); var ce = x?.Element(A + "chExt"); var sx = N(ce, "cx") == 0 ? 1 : N(ext, "cx") / N(ce, "cx"); var sy = N(ce, "cy") == 0 ? 1 : N(ext, "cy") / N(ce, "cy"); var g = El("g", At("transform", $"translate({F(N(off, "x") * scale)} {F(N(off, "y") * scale)}) scale({F(sx)} {F(sy)}) translate({F(-N(co, "x") * scale)} {F(-N(co, "y") * scale)})")); DrawTree(pkg, part, e, g, scale, inherited); target.Add(g); continue;
            }
            if (e.Name != P + "sp" && e.Name != P + "pic" && e.Name != P + "graphicFrame" && e.Name != P + "cxnSp") continue;
            if (inherited && e.Descendants(P + "ph").Any()) continue;
            var prop = e.Element(P + "spPr"); var tr = prop?.Element(A + "xfrm") ?? e.Element(P + "xfrm"); if (tr == null) continue; var o = tr.Element(A + "off"); var ex = tr.Element(A + "ext"); var x0 = N(o, "x") * scale; var y = N(o, "y") * scale; var w = N(ex, "cx") * scale; var h = N(ex, "cy") * scale;
            var g2 = El("g"); var rot = N(tr, "rot") / 60000; if (rot != 0) g2.Add(At("transform", $"rotate({F(rot)} {F(x0 + w / 2)} {F(y + h / 2)})"));
            if (e.Name == P + "pic") { var b = e.Descendants(A + "blip").FirstOrDefault(); if (b != null) DrawImage(pkg, part, b, g2, x0, y, w, h); target.Add(g2); continue; }
            if (e.Name == P + "graphicFrame")
            {
                var table = e.Descendants(A + "tbl").FirstOrDefault(); if (table != null) { var rows = table.Elements(A + "tr").ToArray(); var ry = y; foreach (var row in rows) { var rh = N(row, "h") * scale; var cells = row.Elements(A + "tc").ToArray(); var cw = w / Math.Max(1, cells.Length); for (var i = 0; i < cells.Length; i++) { g2.Add(El("rect", At("x", x0 + i * cw), At("y", ry), At("width", cw), At("height", rh), At("fill", ry == y ? "#0B609C" : "#EEF4F8"), At("stroke", "white"))); Text(g2, cells[i], x0 + i * cw, ry, cw, rh, scale, ry == y ? "white" : "#17334B"); } ry += rh; } } else { g2.Add(El("rect", At("x", x0), At("y", y), At("width", w), At("height", h), At("fill", "#EFF5F9"), At("stroke", "#AFC5D5"))); g2.Add(El("text", At("x", x0 + w / 2), At("y", y + h / 2), At("text-anchor", "middle"), At("font-size", 14), At("fill", "#52738B"), "原生图表 · 导出后可编辑")); }
                target.Add(g2); continue;
            }
            var fill = Color(prop); var stroke = Color(prop?.Element(A + "ln")); var shape = (string?)prop?.Element(A + "prstGeom")?.Attribute("prst"); if (fill != "none" || stroke != "none") { var el = shape == "ellipse" ? El("ellipse", At("cx", x0 + w / 2), At("cy", y + h / 2), At("rx", w / 2), At("ry", h / 2)) : El("rect", At("x", x0), At("y", y), At("width", Math.Max(0, w)), At("height", Math.Max(0, h))); el.Add(At("fill", fill), At("stroke", stroke), At("stroke-width", Math.Max(.5, N(prop?.Element(A + "ln"), "w", 9525) * scale))); g2.Add(el); }
            Text(g2, e, x0, y, w, h, scale, "#17334B"); target.Add(g2);
        }
    }
    static void DrawImage(PptPackage p, string part, XElement blip, XElement dest, double x, double y, double w, double h) { var id = (string?)blip.Attribute(R + "embed"); var rel = p.Relations(part).FirstOrDefault(e => (string?)e.Attribute("Id") == id); if (rel == null) return; var img = PptPackage.Resolve(part, (string)rel.Attribute("Target")!); var mime = p.Type(img); if (mime is not ("image/png" or "image/jpeg" or "image/gif" or "image/svg+xml")) return; dest.Add(El("image", At("x", x), At("y", y), At("width", w), At("height", h), At("preserveAspectRatio", "none"), At("href", "data:" + mime + ";base64," + Convert.ToBase64String(p.Bytes(img))))); }
    static void Text(XElement target, XElement shape, double x, double y, double w, double h, double scale, string fallback)
    {
        var body = shape.Descendants(A + "bodyPr").FirstOrDefault(); var left = N(body, "lIns", 91440) * scale; var right = N(body, "rIns", 91440) * scale; var cursor = y + N(body, "tIns", 45720) * scale; foreach (var p in shape.Descendants(A + "p")) { var text = string.Concat(p.Descendants(A + "t").Select(t => t.Value)); var rp = p.Descendants(A + "rPr").FirstOrDefault() ?? p.Descendants(A + "defRPr").FirstOrDefault(); var size = N(rp, "sz", 1800) / 100 * 12700 * scale; size = Math.Clamp(size, 7, 80); if (text.Length == 0) { cursor += size * .65; continue; } var color = Color(rp, fallback); var align = (string?)p.Element(A + "pPr")?.Attribute("algn"); var tx = align == "ctr" ? x + w / 2 : align == "r" ? x + w - right : x + left; var anchor = align == "ctr" ? "middle" : align == "r" ? "end" : "start"; var max = Math.Max(1, w - left - right); var lines = new List<string>(); var line = ""; var lineWidth = 0d; foreach (var c in text) { var cw = c > 255 ? size : size * .55; if (lineWidth + cw > max && line.Length > 0) { lines.Add(line); line = ""; lineWidth = 0; } line += c; lineWidth += cw; } if (line.Length > 0) lines.Add(line); foreach (var l in lines) { cursor += size * 1.15; target.Add(El("text", At("x", tx), At("y", cursor), At("font-family", "Microsoft YaHei, sans-serif"), At("font-size", size), At("font-weight", (string?)rp?.Attribute("b") == "1" ? "700" : "400"), At("text-anchor", anchor), At("fill", color), l)); } }
    }
}

using System.IO.Compression;
using System.Xml.Linq;
using System.Text;
namespace Platform;
public record SlideInfo(int Index, string Title, string Text, string[] Warnings);
public record Selection(string File, int Index, string Chapter,string? ChapterId=null,string Summary="");
public sealed class PptPackage : IDisposable
{
    public static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main", A = "http://schemas.openxmlformats.org/drawingml/2006/main", R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships", Rel = "http://schemas.openxmlformats.org/package/2006/relationships", Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    readonly ZipArchive zip; public string File { get; }
    public string[] Slides { get; }
    public Dictionary<string, string> Types { get; } = []; public long Width { get; }
    public long Height { get; }
    public PptPackage(string file) { File = file; zip = ZipFile.OpenRead(file); try { if (zip.Entries.Count > 20000 || zip.Entries.Sum(e => e.Length) > 600_000_000) throw new InvalidOperationException("PPT 解压后过大，请拆分上传"); if (zip.GetEntry("ppt/vbaProject.bin") != null) throw new InvalidOperationException("不接受含宏的演示文件"); var pres = Xml("ppt/presentation.xml"); var size = pres.Root!.Element(P + "sldSz"); Width = (long?)size?.Attribute("cx") ?? 9144000; Height = (long?)size?.Attribute("cy") ?? 5143500; var rels = Relations("ppt/presentation.xml").ToDictionary(e => (string)e.Attribute("Id")!, e => Resolve("ppt/presentation.xml", (string)e.Attribute("Target")!)); Slides = pres.Descendants(P + "sldId").Select(e => rels[(string)e.Attribute(R + "id")!]).ToArray(); if (Slides.Length > 300) throw new InvalidOperationException("最多支持300页，请拆分上传"); foreach (var e in Xml("[Content_Types].xml").Root!.Elements()) { var key = (string?)e.Attribute("PartName") ?? "*." + (string?)e.Attribute("Extension"); Types[key.TrimStart('/')] = (string)e.Attribute("ContentType")!; } } catch { zip.Dispose(); throw; } }
    public bool Exists(string part) => zip.GetEntry(part) != null;
    public byte[] Bytes(string part) { var e = zip.GetEntry(part) ?? throw new InvalidOperationException("缺少PPT资源：" + part); using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
    public XDocument Xml(string part) { using var m = new MemoryStream(Bytes(part)); return XDocument.Load(m); }
    public IEnumerable<XElement> Relations(string part) => Exists(RelPath(part)) ? Xml(RelPath(part)).Root!.Elements().ToArray() : [];
    public string Type(string part) => Types.GetValueOrDefault(part) ?? Types.GetValueOrDefault("*." + Path.GetExtension(part).TrimStart('.')) ?? "application/octet-stream";
    public static string RelPath(string part) { var i = part.LastIndexOf('/'); return (i < 0 ? "" : part[..(i + 1)]) + "_rels/" + part[(i + 1)..] + ".rels"; }
    public static string Resolve(string from, string target) { var uri = new Uri(new Uri("http://package/" + from), target); if (uri.Host != "package") throw new InvalidOperationException("非法包内引用"); return Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')); }
    public void Dispose() => zip.Dispose();
}
public class PptEngine
{
    static readonly XNamespace P = PptPackage.P, A = PptPackage.A, R = PptPackage.R, Rel = PptPackage.Rel, Ct = PptPackage.Ct;
    public List<SlideInfo> Inspect(string file) { using var p = new PptPackage(file); return p.Slides.Select((part, i) => { var x = p.Xml(part); var paragraphs = x.Descendants(A + "p").Select(e => string.Concat(e.Descendants(A + "t").Select(t => t.Value))).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(); var warnings = new List<string>(); if (x.Descendants().Any(e => e.Name.LocalName == "chart")) warnings.Add("含原生图表，结构预览不代表精确渲染"); if (x.Descendants(P + "timing").Any()) warnings.Add("含动画，静态预览不展示播放效果"); if (p.Relations(part).Any(e => ((string?)e.Attribute("Type"))?.EndsWith("/slide") == true)) warnings.Add("跨页跳转链接将在选页导出时移除"); if (p.Relations(part).Any(e => (string?)e.Attribute("TargetMode") == "External")) warnings.Add("外部链接将在导出时移除"); if (Math.Abs((double)p.Width / p.Height - 16d / 9) > 0.02) warnings.Add("非16:9页面，合成前需调整比例"); return new SlideInfo(i + 1, paragraphs.FirstOrDefault() ?? "未命名页面 " + (i + 1), string.Join("\n", paragraphs), warnings.ToArray()); }).ToList(); }
    public byte[] Extract(string file, int index) { using var source = new PptPackage(file); var builder = new Composer(source.Width, source.Height); builder.Add(source, index, null); return builder.Save(); }

    public byte[] Build(string template, Template config, IEnumerable<Selection> selections, string title, string department, string date,ComposeOptions? options=null)
    {
        if(string.IsNullOrWhiteSpace(title)||title.Length>60)throw new InvalidOperationException("标题需为1～60个字符");
        if(options!=null)OutlineService.Validate(options);
        using var tpl=new PptPackage(template);
        var builder=new Composer(tpl.Width,tpl.Height,options?.PageNumbers==true);
        var groups=selections.GroupBy(s=>s.ChapterId??s.Chapter).ToArray();
        var chapterStarts=new int[groups.Length];
        var cursor=1+(config.Statement>0?1:0)+(int)Math.Ceiling(groups.Length/4d)+1;
        for(var i=0;i<groups.Length;i++){chapterStarts[i]=cursor;cursor+=1+groups[i].Count();}
        builder.Add(tpl,config.Cover,x=>{
            var fields=new[]{"标题文字使用","部门采用","年"}.Select(token=>x.Descendants(A+"p").FirstOrDefault(p=>Text(p).Contains(token))??throw new InvalidOperationException("模板缺少字段："+token)).ToArray();
            SetText(fields[0],title);SetText(fields[1],department);SetText(fields[2],date);
        });
        if(config.Statement>0)builder.Add(tpl,config.Statement,null);
        for(var start=0;start<groups.Length;start+=4){
            var offset=start;
            builder.Add(tpl,config.Toc,x=>{
                var names=new[]{"第一部分标题","第二部分标题","第三部分标题","第四部分标题"};
                // Locate original placeholders before writing user text, so names containing sample words are safe.
                var titleFields=names.Select(name=>x.Descendants(A+"p").FirstOrDefault(e=>Text(e).Contains(name))??throw new InvalidOperationException("目录缺少字段："+name)).ToArray();
                var bullets=x.Descendants(P+"sp").Where(s=>(string?)s.Element(P+"spPr")?.Element(A+"prstGeom")?.Attribute("prst")=="ellipse").OrderBy(s=>(long?)s.Descendants(A+"off").FirstOrDefault()?.Attribute("y")??0).ToArray();
                if(bullets.Length==4)for(var j=Math.Max(0,groups.Length-offset);j<4;j++)bullets[j].Remove();
                var descriptions=titleFields.Select(field=>field.Ancestors(P+"grpSp").FirstOrDefault()?.Descendants(P+"sp").FirstOrDefault(s=>Text(s).Contains("您的内容打在这里"))).ToArray();
                foreach(var shape in x.Descendants(P+"sp").ToArray())if(Text(shape).Contains("章节板块")||Text(shape).Contains("四种颜色"))SetText(shape,"");
                for(var j=0;j<4;j++){
                    var n=offset+j;
                    var name=n<groups.Length?groups[n].First().Chapter:"";
                    SetText(titleFields[j],n<groups.Length?(options==null?"":OutlineService.Number(n+1,options.NumberStyle)+" ")+name:"");
                    if(descriptions[j]!=null)SetText(descriptions[j]!,n<groups.Length&&options?.TocPageNumbers==true?"第 "+chapterStarts[n]+" 页":"");
                }
            });
        }
        var cache=new Dictionary<string,PptPackage>();
        try {
            for(var i=0;i<groups.Length;i++){
                var group=groups[i];var number=i+1;var first=group.First();
                builder.Add(tpl,config.Sections[i%config.Sections.Length],x=>{
                    foreach(var shape in x.Descendants(P+"sp")){
                        var text=Text(shape);
                        if(text.Contains("部分标题"))SetText(shape,first.Chapter);
                        else if(text.Contains("您的内容"))SetText(shape,first.Summary);
                        else if(text.Trim().StartsWith("PART"))SetText(shape,options?.NumberStyle=="chinese"?"第"+OutlineService.Number(number,"chinese")+"章":"PART "+number.ToString("00"));
                    }
                });
                foreach(var item in group){
                    if(!cache.TryGetValue(item.File,out var source)){source=new(item.File);cache[item.File]=source;}
                    if(source.Width!=tpl.Width||source.Height!=tpl.Height)throw new InvalidOperationException("页面尺寸与模板不同，请在WPS中调整为公司模板尺寸后上传");
                    builder.Add(source,item.Index,null);
                }
            }
            builder.Add(tpl,config.End,null);return builder.Save();
        }finally{foreach(var package in cache.Values)package.Dispose();}
    }
    public string Preview(string file, int index) { using var p = new PptPackage(file); return PreviewRenderer.Render(p, index); }
    static string Text(XElement e) => string.Concat(e.Descendants(A + "t").Select(t => t.Value));
    static void ReplaceContains(XDocument x, string token, string value) { var s = x.Descendants(A + "p").FirstOrDefault(e => Text(e).Contains(token)); if (s == null) throw new InvalidOperationException("模板缺少字段：" + token + "。当前引擎需要与公司模板一致的示例文本"); SetText(s, value); }
    static void SetText(XElement shape, string value) { var nodes = shape.Descendants(A + "t").ToArray(); if (nodes.Length == 0) return; nodes[0].Value = value; foreach (var n in nodes.Skip(1)) n.Value = ""; }
    sealed class Composer(long width, long height,bool pageNumbers=false)
    {
        readonly Dictionary<string, byte[]> files = []; readonly Dictionary<string, string> types = []; readonly Dictionary<string, string> cloned = []; readonly List<string> slides = []; readonly List<string> masters = []; int counter;
        public void Add(PptPackage p, int index, Action<XDocument>? edit) { if (index < 1 || index > p.Slides.Length) throw new InvalidOperationException("模板或内容页码超出范围"); var src = p.Slides[index - 1]; var dest = "ppt/slides/slide" + (slides.Count + 1) + ".xml"; slides.Add(dest); Copy(p, src, dest, x=>{edit?.Invoke(x);if(pageNumbers&&slides.Count>1)AddPageNumber(x,slides.Count,width,height);}); }
        string Clone(PptPackage p, string part) { var key = p.File + "|" + part; if (cloned.TryGetValue(key, out var dest)) return dest; var dir = part.Contains('/') ? part[..part.LastIndexOf('/')] : "resources"; dest = dir + "/asset" + (++counter) + Path.GetExtension(part); cloned[key] = dest; Copy(p, part, dest, null); if (part.StartsWith("ppt/slideMasters/") && part.EndsWith(".xml")) masters.Add(dest); return dest; }
        void Copy(PptPackage p, string src, string dest, Action<XDocument>? edit)
        {
            types[dest] = p.Type(src); var bytes = p.Bytes(src); XDocument? xml = null; if (src.EndsWith(".xml")) xml = p.Xml(src);
            var rels = new XElement(Rel + "Relationships"); foreach (var r in p.Relations(src))
            {
                var type = (string)r.Attribute("Type")!; var id = (string)r.Attribute("Id")!;
                if ((string?)r.Attribute("TargetMode") == "External" || type.EndsWith("/notesSlide") || type.Contains("/comment") || type.EndsWith("/slide"))
                {
                    if (xml != null) foreach (var e in xml.Descendants().Where(e => e.Attributes().Any(a => a.Name.Namespace == R && a.Value == id)).ToArray()) e.Remove(); continue;
                }
                var target = PptPackage.Resolve(src, (string)r.Attribute("Target")!); var mapped = Clone(p, target); rels.Add(new XElement(Rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", "/" + mapped)));
            }
            if (xml != null) { if(pageNumbers)foreach(var shape in xml.Descendants(P+"sp").Where(s=>s.Descendants(P+"ph").Any(e=>(string?)e.Attribute("type")=="sldNum")||s.Descendants(A+"fld").Any(e=>(string?)e.Attribute("type")=="slidenum")).ToArray())shape.Remove(); edit?.Invoke(xml); bytes = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)); }
            files[dest] = bytes; if (rels.HasElements) files[PptPackage.RelPath(dest)] = Encoding.UTF8.GetBytes(rels.ToString(SaveOptions.DisableFormatting));
        }

        static void AddPageNumber(XDocument document,int page,long w,long h){
            var tree=document.Descendants(P+"spTree").First();
            var id=document.Descendants(P+"cNvPr").Select(e=>(int?)e.Attribute("id")??0).DefaultIfEmpty(0).Max()+1;
            tree.Add(new XElement(P+"sp",
                new XElement(P+"nvSpPr",new XElement(P+"cNvPr",new XAttribute("id",id),new XAttribute("name","PlatformPageNumber")),new XElement(P+"cNvSpPr",new XAttribute("txBox",1)),new XElement(P+"nvPr")),
                new XElement(P+"spPr",new XElement(A+"xfrm",new XElement(A+"off",new XAttribute("x",(long)(w*.93)),new XAttribute("y",(long)(h*.945))),new XElement(A+"ext",new XAttribute("cx",(long)(w*.055)),new XAttribute("cy",(long)(h*.045)))),new XElement(A+"prstGeom",new XAttribute("prst","rect"),new XElement(A+"avLst")),new XElement(A+"noFill")),
                new XElement(P+"txBody",new XElement(A+"bodyPr",new XAttribute("lIns",0),new XAttribute("rIns",0),new XAttribute("tIns",0),new XAttribute("bIns",0)),new XElement(A+"lstStyle"),
                    new XElement(A+"p",new XElement(A+"pPr",new XAttribute("algn","r")),new XElement(A+"r",new XElement(A+"rPr",new XAttribute("lang","zh-CN"),new XAttribute("sz",1200),new XElement(A+"solidFill",new XElement(A+"srgbClr",new XAttribute("val","808080"))),new XElement(A+"latin",new XAttribute("typeface","微软雅黑")),new XElement(A+"ea",new XAttribute("typeface","微软雅黑"))),new XElement(A+"t",page.ToString()))))));
        }

        public byte[] Save()
        {
            var pr = new XElement(Rel + "Relationships"); var pres = new XElement(P + "presentation", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R));
            var ml = new XElement(P + "sldMasterIdLst"); for (var i = 0; i < masters.Count; i++) { var id = "m" + i; pr.Add(Relation(id, "slideMaster", masters[i])); ml.Add(new XElement(P + "sldMasterId", new XAttribute("id", 2147483648L + i), new XAttribute(R + "id", id))); }
            if (ml.HasElements) pres.Add(ml);
            var sl = new XElement(P + "sldIdLst"); for (var i = 0; i < slides.Count; i++) { var id = "s" + i; pr.Add(Relation(id, "slide", slides[i])); sl.Add(new XElement(P + "sldId", new XAttribute("id", 256 + i), new XAttribute(R + "id", id))); }
            pres.Add(sl, new XElement(P + "sldSz", new XAttribute("cx", width), new XAttribute("cy", height)), new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000)));
            files["ppt/presentation.xml"] = Encoding.UTF8.GetBytes(pres.ToString()); files["ppt/_rels/presentation.xml.rels"] = Encoding.UTF8.GetBytes(pr.ToString()); types["ppt/presentation.xml"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
            files["_rels/.rels"] = Encoding.UTF8.GetBytes(new XElement(Rel + "Relationships", Relation("root", "officeDocument", "ppt/presentation.xml")).ToString());
            var ct = new XElement(Ct + "Types", new XElement(Ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml"))); foreach (var pair in types) ct.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/" + pair.Key), new XAttribute("ContentType", pair.Value))); files["[Content_Types].xml"] = Encoding.UTF8.GetBytes(ct.ToString());
            using var ms = new MemoryStream(); using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) { foreach (var pair in files) { using var stream = z.CreateEntry(pair.Key, CompressionLevel.Fastest).Open(); stream.Write(pair.Value); } }
            return ms.ToArray();
        }
        static XElement Relation(string id, string type, string target) => new(Rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/" + type), new XAttribute("Target", "/" + target));
    }
}

namespace Platform;
public static class OutlineService {
 public static List<OutlineChapter> Resolve(IEnumerable<Page> source,IEnumerable<OutlineChapter>? input){
  var pages=source.ToArray();var result=new List<OutlineChapter>();var seen=new HashSet<string>();var chapterIds=new HashSet<string>();
  var chapters=input?.ToArray()??[];
  if(chapters.Length>100)throw new ArgumentException("最多支持100个章节");
  foreach(var chapter in chapters){
   if(string.IsNullOrWhiteSpace(chapter.Id)||!chapterIds.Add(chapter.Id)||string.IsNullOrWhiteSpace(chapter.Title)||chapter.Title.Trim().Length>40||(chapter.Summary?.Length??0)>120)
    throw new ArgumentException("章节标识不能重复；标题1～40字，摘要不超过120字");
   foreach(var id in chapter.PageIds??[])if(!pages.Any(p=>p.Id==id)||!seen.Add(id))throw new ArgumentException("大纲包含未选择或重复的页面");
   if(chapter.PageIds?.Length>0)result.Add(chapter with{Title=chapter.Title.Trim(),Summary=chapter.Summary??""});
  }
  foreach(var group in pages.Where(p=>!seen.Contains(p.Id)).GroupBy(p=>p.Versions.First(v=>v.Id==p.PublishedVersionId).Chapter)){
   var existing=result.FindIndex(c=>c.Title==group.Key);var ids=group.Select(p=>p.Id).ToArray();
   if(existing>=0)result[existing]=result[existing] with{PageIds=result[existing].PageIds.Concat(ids).ToArray()};
   else result.Add(new OutlineChapter(Ids.New(),group.Key,"",ids));
  }
  if(result.Count>100)throw new ArgumentException("最多支持100个非空章节");
  return result;
 }
 public static void Validate(ComposeOptions options){if(options.NumberStyle is not("arabic" or "chinese"))throw new ArgumentException("不支持的章节编号格式");}
 public static string Number(int index,string style){
  if(style!="chinese")return index.ToString("00");
  string[] digits=["","一","二","三","四","五","六","七","八","九"];
  if(index==100)return "一百";
  return index<10?digits[index]:(index<20?"":digits[index/10])+"十"+digits[index%10];
 }
}

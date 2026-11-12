namespace Platform;
public static class VersionLifecycle {
 public static PageVersion Save(Page page,PageVersion input){
  var draft=page.Versions.LastOrDefault(v=>v.Status=="pending"||(v.Status=="draft"&&v.Number==0));
  if(draft!=null){input.Id=draft.Id;page.Versions.Remove(draft);}
  input.Number=0;input.Expires=null;page.Versions.Add(input);return input;
 }
 public static void Approve(Page page,PageVersion version){
  if(version.Status!="pending")throw new InvalidOperationException("该版本已不在待审核状态");
  version.Number=page.Versions.Where(v=>v.Status=="approved").Select(v=>v.Number).DefaultIfEmpty(0).Max()+1;
  version.Status="approved";page.PublishedVersionId=version.Id;
 }
}

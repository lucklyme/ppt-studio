using Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using System.Collections.Concurrent;
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddConsole();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 200_000_000);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 201_000_000);
builder.Services.AddAuthentication("session").AddCookie("session", o => { o.Cookie.Name = "pptstudio"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; o.ExpireTimeSpan = TimeSpan.FromHours(8); o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; }; });
var keyRoot = builder.Configuration["DataRoot"] ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../../data/app"));
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(keyRoot, "keys")));
builder.Services.AddAuthorization(); builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("login", x => { x.PermitLimit = 12; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; }));
var app = builder.Build();
var dataRoot = builder.Configuration["DataRoot"] ?? Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../../data/app"));
var store = new Store(dataRoot); var engine = new PptEngine(); var jobs = new ConcurrentDictionary<string, JobView>(); var workGate = new SemaphoreSlim(2);
var repo = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../.."));
var nativePreview=new WpsPreviewService(store.Root,Path.Combine(repo,"scripts","render-wps.ps1"));
if (!store.Read(db => db.Templates.Any())) { var file = Directory.GetFiles(repo, "*.pptx").FirstOrDefault(f => !Path.GetFileName(f).StartsWith("~$")); if (file != null) { var up = new Upload { Name = Path.GetFileName(file), DepartmentId = "company", UserId = "system", Slides = engine.Inspect(file) }; File.Copy(file, store.FilePath(up.Id)); store.Change(db => { db.Uploads.Add(up); db.Templates.Add(new() { Name = "标准模板", UploadId = up.Id }); return true; }); } }
app.Use(async (c, next) => { try { c.Response.Headers["X-Content-Type-Options"] = "nosniff"; c.Response.Headers["Referrer-Policy"] = "same-origin"; c.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'"; if (c.Request.Path.StartsWithSegments("/api")) { c.Response.Headers.CacheControl = "no-store"; if (c.Request.Method is not ("GET" or "HEAD")) { if (c.Request.Headers["X-Requested-With"] != "PPTStudio") throw new UnauthorizedAccessException("请求校验失败"); var origin = c.Request.Headers.Origin.ToString(); if (origin.Length > 0 && origin != $"{c.Request.Scheme}://{c.Request.Host}") throw new UnauthorizedAccessException("不允许跨站操作"); } } await next(); } catch (Exception e) { if (c.Response.HasStarted) throw; c.Response.StatusCode = e switch { UnauthorizedAccessException => 403, ConflictException => 409, ArgumentException or InvalidOperationException or InvalidDataException => 400, KeyNotFoundException => 404, _ => 500 }; await c.Response.WriteAsJsonAsync(new { message = c.Response.StatusCode == 500 ? "操作失败，请查看服务日志后重试" : e.Message }); if (c.Response.StatusCode == 500) app.Logger.LogError(e, "Request failed"); } });
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.UseDefaultFiles(); app.UseStaticFiles();
User Current(HttpContext c) { var id = c.User.FindFirstValue(ClaimTypes.NameIdentifier); return store.Read(db => db.Users.FirstOrDefault(u => u.Id == id && u.Enabled)) ?? throw new UnauthorizedAccessException("账号已停用或会话失效"); }
void Admin(User u) { if (!Policy.IsAdmin(u.Role)) throw new UnauthorizedAccessException("需要公司管理员权限"); }
void Maintain(User u, string dep) { if (!Policy.CanMaintain(u.Role, u.DepartmentId, dep)) throw new UnauthorizedAccessException("只能维护本部门内容"); }
bool CanUpload(User u, Upload upload) => Policy.CanMaintain(u.Role, u.DepartmentId, upload.DepartmentId);
bool Usable(Page p) => p.Enabled && p.PublishedVersionId != null && p.Versions.Any(v => v.Id == p.PublishedVersionId && v.Status == "approved");
PageVersion Published(Page p) => p.Versions.FirstOrDefault(v => v.Id == p.PublishedVersionId) ?? throw new InvalidOperationException("页面尚未发布：" + p.Id);
void CanGeneration(User u, Generation g, Database db) { if (g.OwnerId != u.Id && !Policy.IsAdmin(u.Role)) throw new UnauthorizedAccessException("不能访问他人的生成记录"); if (g.PageIds.Any(id => !db.Pages.Any(p => p.Id == id && p.Enabled))) throw new UnauthorizedAccessException("产物包含已停用或撤回的页面，请重新生成"); }
object UserDto(User u) => new { u.Id, u.Username, u.Name, u.Role, u.DepartmentId, u.Enabled };
object BrandDto(Database db) => new { companyName=db.Brand.CompanyName, logoUrl=db.Brand.LogoFile==null?null:"/api/brand/logo/"+db.Brand.LogoFile };
async Task SignIn(HttpContext c, User u) => await c.SignInAsync("session", new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, u.Id)], "session")));
app.MapGet("/api/session", (HttpContext c) => { var setup = store.Read(db => db.Users.Count == 0); if (setup) return Results.Json(new { setup = true, user = (object?)null, brand=store.Read(db=>BrandDto(db)) }); if (c.User.Identity?.IsAuthenticated != true) return Results.Json(new { setup = false, user = (object?)null, brand=store.Read(db=>BrandDto(db)) }); return Results.Json(new { setup = false, user = UserDto(Current(c)), brand=store.Read(db=>BrandDto(db)) }); });
app.MapPost("/api/setup", async (HttpContext c, LoginInput input) => { if (input.Password.Length < 10 || input.Username.Trim().Length < 3) throw new ArgumentException("账号至少3位，密码至少10位"); var u = store.Change(db => { if (db.Users.Any()) throw new InvalidOperationException("平台已经初始化"); var user = new User { Username = input.Username.Trim(), Name = "系统管理员", Role = "admin", DepartmentId = "company", PasswordHash = Passwords.Hash(input.Password) }; db.Users.Add(user); Store.Log(db, user, "初始化平台", "系统"); return user; }); await SignIn(c, u); return Results.Json(UserDto(u)); }).RequireRateLimiting("login");
app.MapPost("/api/login", async (HttpContext c, LoginInput input) => { var u = store.Read(db => db.Users.FirstOrDefault(u => u.Username == input.Username && u.Enabled)); if (u == null || !Passwords.Verify(input.Password, u.PasswordHash)) return Results.Json(new { message = "账号或密码不正确" }, statusCode: 401); await SignIn(c, u); return Results.Json(UserDto(u)); }).RequireRateLimiting("login");
app.MapPost("/api/logout", async (HttpContext c) => { await c.SignOutAsync("session"); return Results.Ok(); });

app.MapGet("/api/brand/logo/{file}",(string file)=>{
 var current=store.Read(db=>db.Brand.LogoFile==file?new{db.Brand.LogoFile,db.Brand.LogoMime}:null)??throw new KeyNotFoundException("Logo不存在");
 var full=Path.Combine(store.Root,"brand-assets",current.LogoFile!);
 if(!File.Exists(full))throw new KeyNotFoundException("Logo不存在");
 return Results.File(full,current.LogoMime!);
});
var api = app.MapGroup("/api").RequireAuthorization();
api.MapPost("/brand/name",(HttpContext c,BrandNameInput input)=>{
 var u=Current(c);Admin(u);var name=input.CompanyName?.Trim()??"";
 if(name.Length is <1 or >80)throw new ArgumentException("公司名称需为1～80个字符");
 return store.Change(db=>{var old=db.Brand.CompanyName;db.Brand.CompanyName=name;db.Brand.Updated=DateTime.UtcNow;Store.Log(db,u,"修改公司名称",old+" → "+name);return BrandDto(db);});
});
api.MapPost("/brand/logo",async(HttpContext c)=>{
 var u=Current(c);Admin(u);var form=await c.Request.ReadFormAsync();var file=form.Files.GetFile("logo")??throw new ArgumentException("请选择PNG或SVG格式的Logo");
 if(file.Length is <1 or >5_000_000)throw new ArgumentException("Logo文件须小于5MB");
 var ext=Path.GetExtension(file.FileName).ToLowerInvariant();if(ext is not(".png"or".svg"))throw new ArgumentException("仅支持PNG或SVG格式的Logo");
 await using var ms=new MemoryStream();await file.CopyToAsync(ms);var bytes=ms.ToArray();
 if(ext==".png")ValidateBrandPng(bytes);else ValidateBrandSvg(bytes);
 var mime=ext==".png"?"image/png":"image/svg+xml";var asset=Ids.New()+ext;
 var directory=Path.Combine(store.Root,"brand-assets");Directory.CreateDirectory(directory);var destination=Path.Combine(directory,asset);
 await File.WriteAllBytesAsync(destination,bytes);
 try{
  var old=store.Change(db=>{var previous=db.Brand.LogoFile;db.Brand.LogoFile=asset;db.Brand.LogoMime=mime;db.Brand.Updated=DateTime.UtcNow;Store.Log(db,u,"更新公司Logo",asset);return previous;});
  if(old!=null)try{File.Delete(Path.Combine(directory,old));}catch{}
  return Results.Json(store.Read(BrandDto));
 }catch{try{File.Delete(destination);}catch{}throw;}
});
api.MapPost("/brand/logo/delete",(HttpContext c)=>{
 var u=Current(c);Admin(u);
 var old=store.Change(db=>{var previous=db.Brand.LogoFile;db.Brand.LogoFile=null;db.Brand.LogoMime=null;db.Brand.Updated=DateTime.UtcNow;Store.Log(db,u,"恢复默认公司Logo",db.Brand.CompanyName);return previous;});
 if(old!=null)try{File.Delete(Path.Combine(store.Root,"brand-assets",old));}catch{}
 return store.Read(BrandDto);
});

api.MapGet("/state", (HttpContext c) => { var u = Current(c); return store.Read(db => new { user = UserDto(u), brand = BrandDto(db), departments = db.Departments, pages = db.Pages.Where(p => Policy.CanMaintain(u.Role, u.DepartmentId, p.DepartmentId) || Usable(p)).Select(p => new { p.Id, p.DepartmentId, p.Enabled, p.Required, p.RequiredScenario, p.DepartmentRequired, p.Revision, p.PublishedVersionId, versions = p.Versions.Where(v => Policy.CanMaintain(u.Role, u.DepartmentId, p.DepartmentId) || v.Status == "approved").ToArray() }), uploads = db.Uploads.Where(up => CanUpload(u, up)), templates = db.Templates.Where(t => t.DeletedAt == null && (t.Enabled || Policy.IsAdmin(u.Role))), plans = db.Plans.Where(p => p.OwnerId == u.Id), generations = db.Generations.Where(g => g.OwnerId == u.Id || Policy.IsAdmin(u.Role)).OrderByDescending(g => g.Created).Select(g => { if (g.PageIds.Any(id => !db.Pages.Any(p => p.Id == id && p.Enabled))) { g.Slides = []; g.Blocked = true; } return g; }), users = Policy.IsAdmin(u.Role) ? db.Users.Select(UserDto) : [], audits = Policy.IsAdmin(u.Role) ? db.Audits.TakeLast(200).Reverse() : [] }); });

api.MapPost("/uploads", async (HttpContext c) => { var u = Current(c); if (u.Role == "viewer") throw new UnauthorizedAccessException("普通使用者不能上传维护内容"); var form = await c.Request.ReadFormAsync(); var file = form.Files.GetFile("file") ?? throw new ArgumentException("请选择PPTX文件"); if (!file.FileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) || file.Length == 0 || file.Length > 200_000_000) throw new ArgumentException("仅支持200MB以内的PPTX文件"); var dep = form["departmentId"].ToString(); if (string.IsNullOrEmpty(dep)) dep = u.DepartmentId; Maintain(u, dep); if (!store.Read(db => db.Departments.Any(d => d.Id == dep))) throw new ArgumentException("部门不存在"); var up = new Upload { Name = Path.GetFileName(file.FileName), DepartmentId = dep, UserId = u.Id }; var target = store.FilePath(up.Id); try { await using (var stream = File.Create(target)) await file.CopyToAsync(stream); up.Slides = await Task.Run(() => engine.Inspect(target)); if (up.Slides.Count == 0) throw new InvalidOperationException("文件中没有幻灯片"); store.Change(db => { db.Uploads.Add(up); Store.Log(db, u, "上传PPT", up.Name); return true; }); return Results.Json(up); } catch { File.Delete(target); throw; } });
api.MapGet("/uploads/{id}/preview/{index:int}", async (HttpContext c, string id, int index) => { var u = Current(c); var allowed = store.Read(db => db.Uploads.Any(up => up.Id == id && (CanUpload(u, up) || db.Templates.Any(t => t.UploadId == id && t.DeletedAt == null && t.Enabled && (new[] { t.Cover, t.Statement, t.Toc, t.End }.Concat(t.Sections).Contains(index))))) || db.Pages.Any(p => Usable(p) && p.Versions.Any(v => v.Id == p.PublishedVersionId && v.UploadId == id && v.SlideIndex == index))); if (!allowed) throw new UnauthorizedAccessException("无权预览该页面"); return Results.File(await nativePreview.Render(store.FilePath(id),index,c.RequestAborted), "image/png"); });
api.MapGet("/uploads/{id}/download", (HttpContext c, string id) => { var u = Current(c); var up = store.Read(db => db.Uploads.First(x => x.Id == id)); if (!CanUpload(u, up)) throw new UnauthorizedAccessException("无权下载整份源文件"); store.Change(db => { Store.Log(db, u, "下载原始PPT", up.Name); return true; }); return Results.File(store.FilePath(id), "application/vnd.openxmlformats-officedocument.presentationml.presentation", up.Name); });
api.MapPost("/imports", (HttpContext c, ImportInput input) => { var u = Current(c); if (input.Items.Length == 0) throw new ArgumentException("请至少选择一页"); var key = c.Request.Headers["Idempotency-Key"].ToString(); return store.Change(db => { if (key.Length > 0 && db.ImportKeys.TryGetValue(u.Id + key, out var existing)) return new { ids = existing.Split(',') }; var up = db.Uploads.First(x => x.Id == input.UploadId); Maintain(u, up.DepartmentId); var ids = new List<string>(); var targets = new HashSet<string>(); foreach (var item in input.Items) { if (item.SlideIndex < 1 || item.SlideIndex > up.Slides.Count) throw new ArgumentException("源页码错误"); if (item.Title.Trim().Length is < 1 or > 100 || item.Chapter.Trim().Length is < 1 or > 40) throw new ArgumentException("页面标题1～100字，章节1～40字"); Page page; if (!string.IsNullOrEmpty(item.TargetPageId)) { if (!targets.Add(item.TargetPageId)) throw new ArgumentException("一次导入不能多次替换同一页"); page = db.Pages.First(p => p.Id == item.TargetPageId); Maintain(u, page.DepartmentId); Policy.CheckRevision(page.Revision, item.ExpectedRevision); page.Revision++; } else { page = new() { DepartmentId = up.DepartmentId }; db.Pages.Add(page); } var v = new PageVersion { Number = 0, UploadId = up.Id, SlideIndex = item.SlideIndex, Title = item.Title.Trim(), Chapter = item.Chapter.Trim(), Tags = item.Tags, Scenario = item.Scenario, Expires = null, Note = item.Note, Status = input.Submit ? "pending" : "draft", AuthorId = u.Id }; VersionLifecycle.Save(page,v); ids.Add(page.Id); Store.Log(db, u, item.TargetPageId == null ? "导入页面" : "替换新版本", v.Title); } if (key.Length > 0) db.ImportKeys[u.Id + key] = string.Join(',', ids); return new { ids = ids.ToArray() }; }); });

api.MapPost("/pages/{id}/versions", (HttpContext c, string id, EditInput input) =>
{
    var u = Current(c); return store.Change(db =>
    {
        var p = db.Pages.First(p => p.Id == id); Maintain(u, p.DepartmentId); Policy.CheckRevision(p.Revision, input.Revision);

        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 100 || string.IsNullOrWhiteSpace(input.Chapter) || input.Chapter.Length > 40) throw new ArgumentException("标题1～100字，章节1～40字");
        var source = p.Versions.First(v => v.Id == input.VersionId);
        var version = new PageVersion { Number = 0, UploadId = source.UploadId, SlideIndex = source.SlideIndex, Title = input.Title.Trim(), Chapter = input.Chapter.Trim(), Tags = input.Tags, Scenario = input.Scenario, Expires = null, Note = input.Note, Status=input.Submit?"pending":"draft", AuthorId = u.Id };
        VersionLifecycle.Save(p,version); p.Revision++; Store.Log(db, u, "创建编辑/恢复版本", version.Title + " V" + version.Number); return version;
    });
});

api.MapPost("/pages/{id}/submit", (HttpContext c, string id, RevisionInput input) => { var u = Current(c); return store.Change(db => { var p = db.Pages.First(p => p.Id == id); Maintain(u, p.DepartmentId); Policy.CheckRevision(p.Revision, input.Revision); var v = p.Versions.Last(); if (v.Status != "draft") throw new InvalidOperationException("只有草稿可以提交"); v.Status = "pending"; p.Revision++; Store.Log(db, u, "提交审核", v.Title); return true; }); });
api.MapPost("/pages/{id}/review", (HttpContext c, string id, ReviewInput input) => { var u = Current(c); Admin(u); return store.Change(db => { var p = db.Pages.First(p => p.Id == id); Policy.CheckRevision(p.Revision, input.Revision); var v = p.Versions.First(v => v.Id == input.VersionId); if (v.Status != "pending") throw new InvalidOperationException("该版本已不在待审核状态"); if(input.Approve)VersionLifecycle.Approve(p,v);else {v.Status="rejected";v.Number=0;} v.ReviewNote = input.Note; p.Revision++; Store.Log(db, u, input.Approve ? "审核发布" : "驳回版本", v.Title); return true; }); });
api.MapPost("/pages/{id}/settings", (HttpContext c, string id, SettingsInput input) => { var u = Current(c); return store.Change(db => { var p = db.Pages.First(p => p.Id == id); Maintain(u, p.DepartmentId); Policy.CheckRevision(p.Revision, input.Revision); if (!Policy.IsAdmin(u.Role) && (input.Required != p.Required || input.RequiredScenario != p.RequiredScenario || input.DepartmentRequired != p.DepartmentRequired)) throw new UnauthorizedAccessException("只有管理员可以修改必选规则"); p.Enabled = input.Enabled; p.Required = input.Required; p.RequiredScenario = input.RequiredScenario; p.DepartmentRequired = input.DepartmentRequired; p.Revision++; Store.Log(db, u, "调整页面状态与规则", p.Versions.Last().Title); return true; }); });
api.MapGet("/pages/{id}/versions/{vid}/download", (HttpContext c, string id, string vid) => { var u = Current(c); var pair = store.Read(db => { var p = db.Pages.First(p => p.Id == id); var v = p.Versions.First(v => v.Id == vid); if (!Policy.CanMaintain(u.Role, u.DepartmentId, p.DepartmentId) && (!p.Enabled || v.Status != "approved")) throw new UnauthorizedAccessException("无权下载该版本"); return (p, v); }); var bytes = engine.Extract(store.FilePath(pair.v.UploadId), pair.v.SlideIndex); store.Change(db => { Store.Log(db, u, "下载页面版本", pair.v.Title + " V" + pair.v.Number); return true; }); return Results.File(bytes, "application/vnd.openxmlformats-officedocument.presentationml.presentation", pair.v.Title + (pair.v.Status=="approved"?"-v"+pair.v.Number:"-"+pair.v.Status) + ".pptx"); });
api.MapPost("/plans", (HttpContext c, Plan input) => { var u = Current(c); input.Id = Ids.New(); input.OwnerId = u.Id; input.Created = DateTime.UtcNow; if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("请输入方案名称"); return store.Change(db => { if (input.PageIds.Any(id => !db.Pages.Any(p => p.Id == id && Usable(p)))) throw new InvalidOperationException("方案中包含不可用页面"); OutlineService.Validate(input.Options);var deps=db.Pages.Where(p=>input.PageIds.Contains(p.Id)).Select(p=>p.DepartmentId).ToHashSet();var required=db.Pages.Where(p=>p.Required||(p.RequiredScenario.Length>0&&p.RequiredScenario==input.Scenario)||(p.DepartmentRequired&&deps.Contains(p.DepartmentId))).Select(p=>p.Id);var planPages=input.PageIds.Concat(required).Distinct().Select(id=>db.Pages.First(p=>p.Id==id)).ToArray();if(planPages.Any(p=>!Usable(p)))throw new InvalidOperationException("必选页面不可用，无法保存方案");input.Chapters=OutlineService.Resolve(planPages,input.Chapters);db.Plans.Add(input); Store.Log(db, u, "保存生成方案", input.Name); return input; }); });
api.MapPost("/templates", (HttpContext c, Template input) => { var u = Current(c); Admin(u); var up = store.Read(db => db.Uploads.First(x => x.Id == input.UploadId)); if (!CanUpload(u, up)) throw new UnauthorizedAccessException(); if (input.Sections.Length == 0 || new[] { input.Cover, input.Toc, input.End }.Concat(input.Sections).Any(i => i < 1 || i > up.Slides.Count) || input.Statement < 0 || input.Statement > up.Slides.Count) throw new ArgumentException("模板角色页码不正确"); engine.Build(store.FilePath(up.Id), input, [new(store.FilePath(up.Id), input.Cover, "模板校验")], "模板字段校验", "部门", "2026年11月11日"); input.Id = Ids.New(); input.Created = DateTime.UtcNow; input.DeletedAt = null; input.PreviewImages = []; return store.Change(db => { db.Templates.Add(input); Store.Log(db, u, "发布模板版本", input.Name); return input; }); });
api.MapPost("/templates/{id}/toggle", (HttpContext c, string id) => { var u = Current(c); Admin(u); return store.Change(db => { var t = db.Templates.FirstOrDefault(t => t.Id == id && t.DeletedAt == null) ?? throw new KeyNotFoundException("模板不存在或已删除"); t.Enabled = !t.Enabled; Store.Log(db, u, "切换模板状态", t.Name); return t; }); });

api.MapPost("/templates/{id}/delete", (HttpContext c, string id) =>
{
    var u = Current(c); Admin(u);
    return store.Change(db =>
    {
        var t = db.Templates.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException("模板不存在");
        if (t.DeletedAt == null) { t.DeletedAt = DateTime.UtcNow; t.Enabled = false; Store.Log(db, u, "删除模板版本", t.Name); }
        return new { deleted = true };
    });
});

api.MapGet("/templates/{id}/download",(HttpContext c,string id)=>{
 var u=Current(c);
 var template=store.Read(db=>db.Templates.FirstOrDefault(t=>t.Id==id&&t.DeletedAt==null&&t.Enabled))
     ??throw new KeyNotFoundException("模板未发布、已停用或已删除");
 store.Change(db=>{Store.Log(db,u,"下载公司模板",template.Name);return true;});
 return Results.File(store.FilePath(template.UploadId),"application/vnd.openxmlformats-officedocument.presentationml.presentation",template.Name+".pptx");
});

Template AccessibleTemplate(User u, string id)
{
    return store.Read(db => db.Templates.FirstOrDefault(t => t.Id == id && t.DeletedAt == null && (t.Enabled || Policy.IsAdmin(u.Role))))
        ?? throw new KeyNotFoundException("模板不存在或已删除");
}
api.MapGet("/templates/{id}/preview/{index:int}", async (HttpContext c, string id, int index) =>
{
 var t=AccessibleTemplate(Current(c),id);
 if(index<1||!new[]{t.Cover,t.Statement,t.Toc,t.End}.Concat(t.Sections).Contains(index))throw new UnauthorizedAccessException("无权预览非模板角色页面");
 c.Response.Headers["X-Preview-Mode"]="wps-native";
 return Results.File(await nativePreview.Render(store.FilePath(t.UploadId),index,c.RequestAborted),"image/png");
});
api.MapPost("/templates/{id}/previews", async (HttpContext c, string id) =>
{
    var u = Current(c); Admin(u); var t = AccessibleTemplate(u, id);
    var count = store.Read(db => db.Uploads.First(x => x.Id == t.UploadId).Slides.Count);
    var form = await c.Request.ReadFormAsync();
    if (form.Files.Count is < 1 or > 300) throw new ArgumentException("请选择1～300张由该模板导出的PNG图片");
    if (form.Files.Sum(f => f.Length) > 200_000_000) throw new ArgumentException("预览图片总大小不能超过200MB");
    var prepared = new Dictionary<int, byte[]>();
    foreach (var file in form.Files)
    {
        var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file.FileName), @"(\d+)\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index) || index < 1 || index > count)
            throw new ArgumentException("图片文件名须以对应页码结尾，例如 幻灯片1.PNG 或 Slide1.png");
        if (prepared.ContainsKey(index)) throw new ArgumentException("存在重复页码，请检查图片文件名");
        if (file.Length is < 45 or > 15_000_000) throw new ArgumentException("每张PNG图片须小于15MB");
        using var ms = new MemoryStream(); await file.CopyToAsync(ms); var bytes = ms.ToArray();
        if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) ||
            System.Text.Encoding.ASCII.GetString(bytes,12,4)!="IHDR" ||
            System.Text.Encoding.ASCII.GetString(bytes,bytes.Length-8,4)!="IEND")
            throw new ArgumentException("图片不是有效的PNG文件，请重新从WPS或PowerPoint导出");
        var width=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4));
        var height=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4));
        if(width is 0 or > 8192 || height is 0 or > 8192)throw new ArgumentException("图片宽高须在1～8192像素之间");
        prepared[index] = bytes;
    }
    var directory = Path.Combine(store.Root, "template-previews"); Directory.CreateDirectory(directory);
    var saved = new Dictionary<int,string>();
    try
    {
        foreach(var pair in prepared) { var asset=Ids.New(); await File.WriteAllBytesAsync(Path.Combine(directory,asset+".png"),pair.Value); saved[pair.Key]=asset; }
        return store.Change(db =>
        {
            var target=db.Templates.FirstOrDefault(x=>x.Id==id&&x.DeletedAt==null)??throw new KeyNotFoundException("模板已被删除");
            foreach(var pair in saved)target.PreviewImages[pair.Key]=pair.Value;
            Store.Log(db,u,"上传模板原版预览",target.Name+"，"+saved.Count+"页");
            return new { count=saved.Count };
        });
    }
    catch { foreach(var asset in saved.Values) File.Delete(Path.Combine(directory,asset+".png")); throw; }
});

api.MapPost("/departments", (HttpContext c, DepartmentInput input) => { var u = Current(c); Admin(u); if (input.Name.Trim().Length is < 1 or > 60) throw new ArgumentException("请输入有效部门名称"); return store.Change(db => { if (db.Departments.Any(d => d.Name == input.Name.Trim())) throw new ConflictException("部门名称已存在"); var d = new Department(Ids.New(), input.Name.Trim()); db.Departments.Add(d); Store.Log(db, u, "新增部门", d.Name); return d; }); });

api.MapPost("/departments/{id}/update",(HttpContext c,string id,DepartmentInput input)=>{
 var u=Current(c);Admin(u);var name=input.Name?.Trim()??"";
 if(name.Length is <1 or >60)throw new ArgumentException("部门名称需为1～60字");
 return store.Change(db=>{
  var index=db.Departments.FindIndex(d=>d.Id==id);if(index<0)throw new KeyNotFoundException("部门不存在");
  if(db.Departments.Any(d=>d.Id!=id&&string.Equals(d.Name,name,StringComparison.OrdinalIgnoreCase)))throw new ConflictException("部门名称已存在");
  var old=db.Departments[index];db.Departments[index]=old with{Name=name};Store.Log(db,u,"修改部门",old.Name+" → "+name);return db.Departments[index];
 });
});
api.MapPost("/departments/{id}/delete",(HttpContext c,string id)=>{
 var u=Current(c);Admin(u);return store.Change(db=>{
  var d=db.Departments.FirstOrDefault(d=>d.Id==id)??throw new KeyNotFoundException("部门不存在");
  if(id=="company")throw new InvalidOperationException("系统管理部门不能删除，可修改名称");
  var users=db.Users.Count(x=>x.DepartmentId==id);var pages=db.Pages.Count(x=>x.DepartmentId==id);var uploads=db.Uploads.Count(x=>x.DepartmentId==id);
  if(users+pages+uploads>0)throw new ConflictException($"该部门关联{users}个账号、{pages}个内容页、{uploads}份上传文件，不能删除。请先处理归属，停用账号仍保留关联。");
  db.Departments.Remove(d);Store.Log(db,u,"删除部门",d.Name);return new{deleted=true};
 });
});

api.MapPost("/users", (HttpContext c, UserInput input) => { var u = Current(c); Admin(u); if (input.Role == "admin" && u.Role != "admin") throw new UnauthorizedAccessException("只有超级管理员可以创建超级管理员"); if (!new[] { "admin", "company", "department", "editor", "viewer" }.Contains(input.Role) || input.Password.Length < 10 || input.Username.Trim().Length < 3 || input.Name.Trim().Length == 0) throw new ArgumentException("请填写姓名、至少3位账号和10位密码，并选择角色"); return store.Change(db => { if (!db.Departments.Any(d => d.Id == input.DepartmentId)) throw new ArgumentException("部门不存在"); if (db.Users.Any(x => x.Username == input.Username.Trim())) throw new ConflictException("账号已存在"); var n = new User { Username = input.Username.Trim(), Name = input.Name.Trim(), DepartmentId = input.DepartmentId, Role = input.Role, PasswordHash = Passwords.Hash(input.Password) }; db.Users.Add(n); Store.Log(db, u, "创建用户", n.Name); return UserDto(n); }); });
api.MapPost("/users/{id}/toggle", (HttpContext c, string id) => { var u = Current(c); Admin(u); return store.Change(db => { var target = db.Users.First(x => x.Id == id); if (id == u.Id) throw new InvalidOperationException("不能停用自己的账号"); if (target.Role == "admin" && u.Role != "admin") throw new UnauthorizedAccessException("不能管理超级管理员"); target.Enabled = !target.Enabled; Store.Log(db, u, "切换用户状态", target.Name); return true; }); });
api.MapPost("/generations", (HttpContext c, GenerateInput input) =>
{
    var u = Current(c); if (input.Title.Trim().Length is < 1 or > 60) throw new ArgumentException("介绍标题需为1～60字"); var snapshot = store.Read(db => { var template = db.Templates.FirstOrDefault(t => t.Id == input.TemplateId && t.DeletedAt == null && t.Enabled) ?? throw new InvalidOperationException("请选择可用模板"); var deps = db.Pages.Where(p => input.PageIds.Contains(p.Id)).Select(p => p.DepartmentId).ToHashSet(); var rules = db.Pages.Select(p => new RulePage(p.Id, p.Required || (p.RequiredScenario.Length > 0 && p.RequiredScenario == input.Scenario) || (p.DepartmentRequired && deps.Contains(p.DepartmentId)), Usable(p), null)); var resolved = Policy.Resolve(rules, input.PageIds, DateOnly.FromDateTime(DateTime.Now)); if (resolved.Length == 0) throw new InvalidOperationException("请至少选择一页内容"); var ordered = input.PageIds.Where(resolved.Contains).Distinct().Concat(resolved.Except(input.PageIds)).Select(id => db.Pages.First(p => p.Id == id)).ToArray(); var versions = ordered.Select(Published).ToArray(); if (versions.Any(v => v.Scenario != "通用" && v.Scenario != input.Scenario)) throw new InvalidOperationException("包含不适用于当前场景的页面，请更换场景或内容"); var chapters=OutlineService.Resolve(ordered,input.Chapters); var options=input.Options??new ComposeOptions(); OutlineService.Validate(options); return (template, ordered, versions, chapters, options); });
    var g = new Generation { Chapters=snapshot.chapters, Options=snapshot.options, OwnerId = u.Id, Title = input.Title.Trim(), TemplateId = snapshot.template.Id, Scenario = input.Scenario, PageIds = snapshot.chapters.SelectMany(ch=>ch.PageIds).ToArray(), VersionIds = snapshot.chapters.SelectMany(ch=>ch.PageIds).Select(id=>snapshot.ordered.First(p=>p.Id==id).PublishedVersionId!).ToArray() }; var job = new JobView(g.Id, u.Id, "queued", "等待生成", null); jobs[g.Id] = job;
    _ = Task.Run(async () => { await workGate.WaitAsync(); try { jobs[g.Id] = job with { Status = "running", Message = "正在组装原生PPT页面" }; var selections = snapshot.chapters.SelectMany(ch=>ch.PageIds.Select(id=>{var page=snapshot.ordered.First(p=>p.Id==id);var v=snapshot.versions.First(v=>v.Id==page.PublishedVersionId);return new Selection(store.FilePath(v.UploadId),v.SlideIndex,ch.Title,ch.Id,ch.Summary);})); var bytes = engine.Build(store.FilePath(snapshot.template.UploadId), snapshot.template, selections, g.Title, input.Department, input.Date,snapshot.options); await File.WriteAllBytesAsync(store.FilePath(g.Id), bytes); g.Hash = Convert.ToHexString(SHA256.HashData(bytes)); g.Slides = engine.Inspect(store.FilePath(g.Id)); store.Change(db => { db.Generations.Add(g); Store.Log(db, u, "生成PPT", g.Title); return true; }); jobs[g.Id] = job with { Status = "done", Message = "已生成，请预览确认" }; } catch (Exception ex) { jobs[g.Id] = job with { Status = "failed", Message = "生成失败", Error = ex.Message }; app.Logger.LogWarning(ex, "Generation failed"); } finally { workGate.Release(); } }); return Results.Accepted("/api/jobs/" + g.Id, new { jobId = g.Id });
});
api.MapGet("/jobs/{id}", (HttpContext c, string id) => { var u = Current(c); if (!jobs.TryGetValue(id, out var job)) throw new KeyNotFoundException("任务不存在或服务已重启，请查看生成历史后重试"); if (job.OwnerId != u.Id && !Policy.IsAdmin(u.Role)) throw new UnauthorizedAccessException(); return job; });
api.MapGet("/generations/{id}/preview/{index:int}", async (HttpContext c, string id, int index) => { var u = Current(c); store.Read(db => { CanGeneration(u, db.Generations.First(g => g.Id == id), db); return true; }); return Results.File(await nativePreview.Render(store.FilePath(id),index,c.RequestAborted), "image/png"); });
api.MapPost("/generations/{id}/confirm", (HttpContext c, string id, ConfirmInput input) => { var u = Current(c); return store.Change(db => { var g = db.Generations.First(g => g.Id == id); CanGeneration(u, g, db); if (input.Hash != g.Hash) throw new ConflictException("文件已变化，请重新预览"); g.Confirmed = DateTime.UtcNow; Store.Log(db, u, "确认生成预览", g.Title); return true; }); });
api.MapGet("/generations/{id}/download", (HttpContext c, string id) => { var u = Current(c); var g = store.Read(db => { var g = db.Generations.First(g => g.Id == id); CanGeneration(u, g, db); if (g.Confirmed == null) throw new InvalidOperationException("请先预览并确认"); return g; }); var file = store.FilePath(id); if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != g.Hash) throw new ConflictException("产物校验失败，请重新生成"); store.Change(db => { Store.Log(db, u, "下载合成PPT", g.Title); return true; }); return Results.File(file, "application/vnd.openxmlformats-officedocument.presentationml.presentation", g.Title + ".pptx"); });
app.MapFallbackToFile("index.html"); app.Run();

static void ValidateBrandPng(byte[] bytes){
 if(bytes.Length<24||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||System.Text.Encoding.ASCII.GetString(bytes,12,4)!="IHDR")throw new ArgumentException("PNG文件结构无效");
 var w=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4));var h=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4));
 if(w is 0 or >4096||h is 0 or >4096)throw new ArgumentException("Logo宽高须在1～4096像素之间");
}
static void ValidateBrandSvg(byte[] bytes){
 try{
  using var ms=new MemoryStream(bytes);using var reader=System.Xml.XmlReader.Create(ms,new System.Xml.XmlReaderSettings{DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=5_000_000});
  var doc=System.Xml.Linq.XDocument.Load(reader);var root=doc.Root;if(root?.Name.LocalName!="svg")throw new ArgumentException("SVG根元素无效");
  var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"svg","g","path","rect","circle","ellipse","line","polyline","polygon","text","tspan","defs","linearGradient","radialGradient","stop","clipPath","mask","title","desc"};
  var forbidden=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"script","foreignObject","iframe","object","embed","audio","video"};
  foreach(var element in root.DescendantsAndSelf()){
   if(forbidden.Contains(element.Name.LocalName)||!allowed.Contains(element.Name.LocalName))throw new ArgumentException("SVG包含不支持或不安全的元素："+element.Name.LocalName);
   foreach(var attribute in element.Attributes()){
    if(attribute.IsNamespaceDeclaration||attribute.Name.LocalName=="xmlns")continue;
    if(attribute.Name.LocalName.StartsWith("on",StringComparison.OrdinalIgnoreCase)||attribute.Name.LocalName.Equals("style",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("SVG事件和内嵌样式不安全");
    var value=attribute.Value.Trim();
    if(value.Contains("javascript:",StringComparison.OrdinalIgnoreCase)||value.Contains("http:",StringComparison.OrdinalIgnoreCase)||value.Contains("https:",StringComparison.OrdinalIgnoreCase)||
       (value.Contains("url(",StringComparison.OrdinalIgnoreCase)&&!System.Text.RegularExpressions.Regex.IsMatch(value,@"url\(\s*#[^)]+\)")))throw new ArgumentException("SVG不得引用外部资源");
   }
  }
 }catch(ArgumentException){throw;}catch{throw new ArgumentException("SVG文件无效");}
}

public partial class Program { }

public record LoginInput(string Username, string Password);
public record BrandNameInput(string CompanyName);
public record DepartmentInput(string Name);
public record UserInput(string Username, string Password, string Name, string Role, string DepartmentId);
public record ImportItem(int SlideIndex, string Title, string Chapter, string Tags, string Scenario, DateOnly? Expires, string Note, string? TargetPageId, int ExpectedRevision);
public record ImportInput(string UploadId, ImportItem[] Items, bool Submit);
public record RevisionInput(int Revision);
public record ReviewInput(int Revision, string VersionId, bool Approve, string Note);
public record SettingsInput(int Revision, bool Enabled, bool Required, string RequiredScenario, bool DepartmentRequired);
public record GenerateInput(string Title, string TemplateId, string Scenario, string[] PageIds, string Department, string Date, List<OutlineChapter>? Chapters=null, ComposeOptions? Options=null);
public record ConfirmInput(string Hash);
public record JobView(string Id, string OwnerId, string Status, string Message, string? Error);

public record EditInput(int Revision, string VersionId, string Title, string Chapter, string Tags, string Scenario, DateOnly? Expires, string Note, bool Submit=false);



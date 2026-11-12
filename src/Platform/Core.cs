using System.Security.Cryptography;
using System.Text.Json;
namespace Platform;
public static class Policy
{
    public static bool IsAdmin(string role) => role is "admin" or "company";
    public static bool CanMaintain(string role, string own, string target) => IsAdmin(role) || (role is "department" or "editor") && own == target;
    public static string[] Resolve(IEnumerable<RulePage> source, IEnumerable<string> selected, DateOnly date)
    {
        var pages = source.ToArray(); var ids = selected.ToHashSet(); foreach (var p in pages.Where(p => p.Required)) ids.Add(p.Id);
        if (ids.Any(id => !pages.Any(p => p.Id == id))) throw new InvalidOperationException("选择的页面已不存在");
        foreach (var p in pages.Where(p => ids.Contains(p.Id))) if (!p.Enabled) throw new InvalidOperationException("必选或所选页面已停用或未发布：" + p.Id);
        return pages.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToArray();
    }
    public static void CheckRevision(int actual, int expected) { if (actual != expected) throw new ConflictException("内容已被其他人更新，请刷新后重新操作"); }
}
public record RulePage(string Id, bool Required, bool Enabled, DateOnly? Expires);
public class ConflictException(string message) : Exception(message);
public record Department(string Id, string Name);
public class User { public string Id { get; set; } = Ids.New(); public string Username { get; set; } = ""; public string Name { get; set; } = ""; public string Role { get; set; } = "editor"; public string DepartmentId { get; set; } = ""; public string PasswordHash { get; set; } = ""; public bool Enabled { get; set; } = true; }
public static class Passwords
{
    public static string Hash(string password) { var salt = RandomNumberGenerator.GetBytes(16); return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 150000, HashAlgorithmName.SHA256, 32)); }
    public static bool Verify(string password, string hash) { try { var p = hash.Split(':'); return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(p[1]), Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[0]), 150000, HashAlgorithmName.SHA256, 32)); } catch { return false; } }
}
public static class Ids { public static string New() => Guid.NewGuid().ToString("N"); }
public class Upload { public string Id { get; set; } = Ids.New(); public string Name { get; set; } = ""; public string DepartmentId { get; set; } = ""; public string UserId { get; set; } = ""; public DateTime Created { get; set; } = DateTime.UtcNow; public List<SlideInfo> Slides { get; set; } = []; }
public class Page { public string Id { get; set; } = Ids.New(); public string DepartmentId { get; set; } = ""; public bool Enabled { get; set; } = true; public bool Required { get; set; } public string RequiredScenario { get; set; } = ""; public bool DepartmentRequired { get; set; } public int Revision { get; set; } = 1; public string? PublishedVersionId { get; set; } public List<PageVersion> Versions { get; set; } = []; }
public class PageVersion { public string Id { get; set; } = Ids.New(); public int Number { get; set; } public string UploadId { get; set; } = ""; public int SlideIndex { get; set; } public string Title { get; set; } = ""; public string Chapter { get; set; } = "公司概况"; public string Tags { get; set; } = ""; public string Scenario { get; set; } = "通用"; public DateOnly? Expires { get; set; } public string Note { get; set; } = ""; public string Status { get; set; } = "draft"; public string AuthorId { get; set; } = ""; public DateTime Created { get; set; } = DateTime.UtcNow; public string ReviewNote { get; set; } = ""; }
public class BrandSettings { public string CompanyName {get;set;}="示例公司"; public string? LogoFile {get;set;} public string? LogoMime {get;set;} public DateTime Updated {get;set;}=DateTime.UtcNow; }
public class Template { public DateTime? DeletedAt { get; set; } public Dictionary<int,string> PreviewImages { get; set; } = []; public string Id { get; set; } = Ids.New(); public string Name { get; set; } = ""; public string UploadId { get; set; } = ""; public int Cover { get; set; } = 1; public int Statement { get; set; } = 2; public int Toc { get; set; } = 3; public int[] Sections { get; set; } = [4, 16, 22, 27]; public int End { get; set; } = 32; public bool Enabled { get; set; } = true; public DateTime Created { get; set; } = DateTime.UtcNow; }
public class Plan { public List<OutlineChapter> Chapters {get;set;}=[]; public ComposeOptions Options {get;set;}=new(); public string Id { get; set; } = Ids.New(); public string OwnerId { get; set; } = ""; public string Name { get; set; } = ""; public string Title { get; set; } = ""; public string Scenario { get; set; } = "通用"; public string[] PageIds { get; set; } = []; public string TemplateId { get; set; } = ""; public DateTime Created { get; set; } = DateTime.UtcNow; }
public class Generation { public List<OutlineChapter> Chapters {get;set;}=[]; public ComposeOptions Options {get;set;}=new(); public bool Blocked { get; set; } public string Id { get; set; } = Ids.New(); public string OwnerId { get; set; } = ""; public string Title { get; set; } = ""; public string TemplateId { get; set; } = ""; public string Scenario { get; set; } = ""; public DateTime Created { get; set; } = DateTime.UtcNow; public DateTime? Confirmed { get; set; } public string Hash { get; set; } = ""; public string[] PageIds { get; set; } = []; public string[] VersionIds { get; set; } = []; public List<SlideInfo> Slides { get; set; } = []; }
public record Audit(string Id, DateTime Time, string Actor, string Action, string Resource);
public class Database { public BrandSettings Brand {get;set;}=new(); public List<User> Users { get; set; } = []; public List<Department> Departments { get; set; } = [new("company", "公司管理"), new("operations", "运行管理部"), new("technology", "技术研发部"), new("business", "市场经营部")]; public List<Upload> Uploads { get; set; } = []; public List<Page> Pages { get; set; } = []; public List<Template> Templates { get; set; } = []; public List<Plan> Plans { get; set; } = []; public List<Generation> Generations { get; set; } = []; public List<Audit> Audits { get; set; } = []; public Dictionary<string, string> ImportKeys { get; set; } = []; }
public class Store
{
    readonly object gate = new(); readonly string file; Database state;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string Root { get; }
    public Store(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); Directory.CreateDirectory(Path.Combine(Root, "files")); file = Path.Combine(Root, "database.json"); state = File.Exists(file) ? JsonSerializer.Deserialize<Database>(File.ReadAllText(file), Json)! : new(); }
    public string FilePath(string id) { if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("无效文件标识"); return Path.Combine(Root, "files", id + ".pptx"); }
    Database Copy() => JsonSerializer.Deserialize<Database>(JsonSerializer.Serialize(state, Json), Json)!;
    public T Read<T>(Func<Database, T> query) { lock (gate) return query(Copy()); }
    public T Change<T>(Func<Database, T> action) { lock (gate) { var next = Copy(); var result = action(next); var tmp = file + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(next, Json)); File.Move(tmp, file, true); state = next; return result; } }
    public static void Log(Database db, User u, string action, string resource) => db.Audits.Add(new(Ids.New(), DateTime.UtcNow, u.Name, action, resource));
}

public record OutlineChapter(string Id,string Title,string Summary,string[] PageIds);
public record ComposeOptions(string NumberStyle="arabic",bool TocPageNumbers=true,bool PageNumbers=false);

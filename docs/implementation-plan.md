# 公司宣传 PPT 协作平台实施计划

用户已批准开发，优先界面、部门便捷维护、智能直观合成；不用 COM，必要时考虑 WPS。

## 环境决策
目录仅有公司模板，无 Git 仓库。当前环境 NuGet/npm 网络被拒绝，只有 .NET 9 SDK 和 Node 22。因此首个本地可运行版本采用 ASP.NET Core + 原生 ES 模块，不引入未安装依赖；JSON 原子写入持久化，单进程运行。生产切换 PostgreSQL、受支持 .NET 与专业渲染引擎是明确后续项，不能标记生产完成。

## 设计
蓝 #0756A0、绿 #009981、深墨 #17334B、灰底 #F3F6FA、白 #FFFFFF。微软雅黑/系统中文字体。左侧窄导航，主体大幅 PPT 缩略图，蓝色主操作；合成台左内容池、中央顺序、右设置。真实模板预览而非伪造示例公司内容。

## 实施任务
1. tests/Platform.Tests：权限、版本冲突、必选有效期、排序、OOXML 组装和模板字段测试；先失败后实现。
2. src/Platform：Core.cs 领域与存储，PptEngine.cs 原生 OOXML 关系图克隆与结构预览，Program.cs Cookie 登录和资源授权 API。
3. src/Platform/wwwroot：工作台、内容库、上传选页/替换、合成台、审核、模板、规则、账号、历史。
4. 启动脚本、使用说明、运行自动测试和 HTTP 集成验证、浏览器视觉检查。

## 核心接口
Engine.Inspect(path) -> SlideInfo[]；Engine.Build(templatePath, selections, title, department, date) -> byte[]；Engine.Preview(path,index) -> SVG。Build 保留原生对象；Preview 是标注的结构近似预览，绝不宣称精确还原。
Store.Mutate(action) 原子文件提交并串行锁；Policy.CanMaintain(user,department)。Page 有稳定ID、PublishedVersionId 与不可变 Versions；新稿审核后发布；生成快照保存版本、模板与最终文件hash。

## 验证重点
越权资源/路径；批量替换原子性；必选页过期时阻断；选页后跨页链接丢失；模板文字分段与目录超过4章；下载同一预览文件；不包含未选页面与备注泄漏；会话CSRF及停用立即生效。

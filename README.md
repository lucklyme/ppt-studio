# PPT 内容协作台

多部门共同维护演示文稿内容，审核后自动合成一份**真正可编辑**的 PPTX。

![license](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-blue)
![dotnet](https://img.shields.io/badge/.NET-9.0-512BD4)
![platform](https://img.shields.io/badge/platform-Windows-lightgrey)

> 本项目以源码形式开放，允许个人学习、研究、内部试用与非商业组织使用，**不允许商业化**。详见 [许可证](#许可证)。

---

## 这个项目解决什么问题

很多单位对外汇报用的 PPT，是各部门分别做几页、再由一个人拼起来。于是反复出现几个问题：同一段介绍有三个版本、去年改过的数据今年又改回去、模板被人改花、每次做新材料都要重新排版一遍。

这个平台把**单页**当作最小管理单元：

- 部门上传自己的 PPTX，勾选需要的页面入库，填写标题、章节、场景等元信息
- 页面有草稿、待审核、已发布、已驳回的状态流转，发布后不可被悄悄覆盖
- 需要时按章节挑选页面、拖动排序，一键合成一份带封面、目录、章节页、结束页的完整 PPTX
- 合成结果保留图片、图表等**原生可编辑对象**，不是整页截图

## 功能特性

### 内容库

- 上传 PPTX 后自动解析页数与缩略图，勾选入册
- 一个页面对应多个版本，历史版本可下载、可恢复为新草稿
- 已发布版本不可变，修订产生新版本号

### 版本与审核

- 草稿 → 待审核 → 已发布 / 已驳回
- 待审核期间可继续修改，保存草稿会撤回审核状态
- 审核意见、发布与驳回在同一面板完成
- 页面可启停；停用后不再出现在合成候选中，历史产物同步阻断下载

### 合成台

- 按章节组织页面，支持新增/重命名/拖动排序/跨章移动
- 自动生成封面、声明页、每页目录、章节首页与结束页
- 章节编号支持 `01/02` 与 `一/二` 两种风格，目录页码按实际页序回填
- 生成后可预览结构、校验文件 hash、下载同一份产物
- 方案可保存复用，复用不修改内容库原始数据

### 模板管理

- 上传公司模板，解析功能页位置（封面、声明、目录、章节起始页、结束页）
- 模板可换版、启停、删除（删除为归档，保留历史与审计）
- 页面尺寸或比例与模板不一致时明确阻断合成，避免静默裁切

### 权限与账号

- 五种角色：超级管理员、公司管理员、部门管理员、部门编辑、普通使用者
- 部门维度的维护隔离，跨部门只能消费已发布内容
- Cookie 会话、PBKDF2 密码派生存储、同源写请求校验（`X-Requested-With`）
- 操作日志、账号停用、部门名称维护

### 预览

- 默认渲染结构近似 SVG，标注"近似预览"
- 可选接入本机 WPS：通过 `KWPP.Application` 只读导出 1920px 宽 PNG，按源文件 SHA256 缓存
- 预览失败直接报错，不静默降级为近似图

## 快速开始

### 环境

- Windows（WPS 高清预览为可选能力，未安装不影响主流程）
- .NET 9 SDK
- 无需 npm / NuGet 外部包，无需数据库

### 启动

```powershell
powershell -ExecutionPolicy Bypass -File .\Start.ps1
```

默认监听 <http://127.0.0.1:5188>，仅本机访问。换端口：

```powershell
.\Start.ps1 -Port 8080
```

### 首次使用

1. 打开页面，按提示创建管理员账号（账号至少 3 位，密码至少 10 位）
2. 进入「组织与账号」创建部门与编辑账号
3. 进入「内容库 → 上传与更新」上传自己的 PPTX 模板与内容页
4. 「内容审核」发布页面，「PPT 合成台」挑选排序并生成

> 仓库**不包含任何演示文稿**。首次启动会扫描项目根目录的 `*.pptx` 作为初始模板导入；没有文件时请在界面中自行上传。

### 手动运行（不使用脚本）

```powershell
dotnet build src/Platform/Platform.csproj
dotnet src/Platform/bin/Debug/net9.0/Platform.dll `
  --contentRoot "绝对路径/src/Platform" `
  --urls "http://127.0.0.1:5188"
```

`--contentRoot` 必须传绝对路径；相对路径会被解析到 dll 所在目录并报 `DirectoryNotFoundException`。数据目录默认 `data/app`，可用 `--DataRoot` 指定。

## 项目结构

```
src/Platform/
  Program.cs              API、登录、资源权限、流程与任务编排
  Core.cs                 角色策略、领域模型、原子文件存储
  PptEngine.cs            PPTX 解析、关系图克隆、模板组装
  PreviewRenderer.cs      结构近似 SVG 预览
  WpsPreviewService.cs    WPS COM 只读渲染与缓存
  OutlineService.cs       章节编排与目录生成
  VersionLifecycle.cs     版本状态流转
  wwwroot/                前端（原生 ES 模块，无构建步骤）
tests/Platform.Tests/     核心与真实 PPTX 包测试
tests/integration.mjs     独立数据目录的真实 HTTP 流程测试
tests/ui-render.mjs       模板渲染与转义检查
tests/wps-preview.mjs     WPS 渲染链路验证（需本机 WPS）
docs/                     实施计划与验证记录
scripts/render-wps.ps1    WPS 批量导出 PNG 的独立脚本
```

## 技术选型与取舍

| 选择 | 原因 |
|---|---|
| ASP.NET Core Minimal API | 单进程即可跑完整闭环，无需额外服务 |
| 原生 ES 模块 + 手写 CSS | 无构建链、无 npm 依赖，改完刷新即生效 |
| JSON 原子写入 | 单实例场景足够，避免引入数据库运维成本 |
| 原生 OOXML 操作 | 保留图表、图片、嵌入对象，成品牌可继续编辑 |
| 进程内任务 + 并发限制 | 合成任务量小，先保证正确性 |

## 已知限制

这些是当前版本的真实边界，不是待办清单里的猜测：

- **单实例**：JSON 存储不支持多进程写入，不提供高可用与生产级备份；`data/app` 与私有 PPT 需一起备份
- **任务不持久**：合成在进程内执行，服务重启会中断未完成任务
- **仅支持 PPTX**，且页面尺寸需与模板一致；不自动缩放不同尺寸页面
- **预览不是精确渲染**：结构 SVG 与 WPS 导出图都不能替代在 PowerPoint 中的最终验收
- **字段绑定有限**：动态页码、目录由引擎写入，正文任意标题与内嵌序号不会自动替换
- **未实现**：网页内在线编辑、通知、病毒扫描、细粒度页面级 ACL、多部门任职、统一登录
- **导出会移除**备注、评论、外部链接与跨页跳转；页面中的隐藏形状仍可能包含源数据，发布前需人工检查
- **安全边界**：默认绑定 127.0.0.1，请勿直接暴露公网；正式部署须配置 HTTPS 与受保护的数据保护密钥

## 测试

```powershell
dotnet run --project tests/Platform.Tests   # 需要运行目录下有一个 32 页 16:9 的 PPTX
node tests/integration.mjs                  # 使用 5190 端口与独立 data/test-* 目录
node tests/ui-render.mjs
node tests/wps-preview.mjs                  # 可选，需要本机安装 WPS
```

集成测试会创建独立的 `data/test-*` 目录，不会修改 `data/app` 中的正式数据。不要同时运行多个集成测试。

## 支持这个项目

如果这个项目帮你省下了拼接 PPT 的时间，欢迎请作者喝杯咖啡。

收款码放在 `docs/sponsor/` 目录：`wechat.png` 为微信，`alipay.png` 为支付宝。打赏完全自愿，不影响任何功能与授权。

<!--
<p align="center">
  <img src="docs/sponsor/wechat.png" alt="微信赞赏" width="220">
  <img src="docs/sponsor/alipay.png" alt="支付宝赞赏" width="220">
</p>
-->

## 许可证

本项目采用 [PolyForm Noncommercial License 1.0.0](LICENSE)。

简单说：

- ✅ 可以阅读、修改、分发源码，可用于个人学习、研究、业余项目
- ✅ 慈善组织、教育机构、公共研究机构、公益与政府机构可直接使用
- ❌ **不可用于任何商业目的**，包括商业产品集成、付费服务、经营性使用
- ❌ 不可转授权、不可转让许可

商业授权请联系作者单独协商。

---

Copyright (c) 2026 lucklyme

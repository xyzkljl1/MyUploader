# NexusUploader

供其他 Codex 任务调用的 .NET 8 命令行工具，通过 Nexus 官方 v3 API 发布文件或替换 mod 页面正文。发布文件时输入 mod 文件夹，工具读取 `modinfo.ini`、生成 ZIP，并自动选择新建 Main File 或添加版本。页面正文可读取根目录 `doc.bbcode`，结合游戏与类型模板生成，并在发布文件后自动同步。

## 能力

| 命令 | 功能 |
| --- | --- |
| `target candidates` | 获取可用于首次上传的未登记候选页面，供调用 Agent 选择 |
| `inspect` | 查询目录绑定、远端文件历史、当前页面 BBCode；按目录或 URL 查询时还列出该游戏可选的描述类型模板。只发送 GET |
| `update-file` | 首次确认时登记目录绑定；打包上传并创建/更新 Main File、同步版本及可选 changelog；模板模式下自动同步页面正文 |
| `update-info` | 从 doc.bbcode 和已选模板更新页面正文，也支持手动模式下传入完整 description；不上传文件，不改标题、摘要、版本及 changelog |

文件链路参考用户指定的 Updater、[Nexus 官方 OpenAPI](https://github.com/Nexus-Mods/Vortex/blob/master/packages/nexus-api-v3/schema/openapi.yaml) 和 [Nexus 官方上传 Action](https://github.com/Nexus-Mods/upload-action)。`update-info` 使用 OpenAPI 中的官方 `editMod` 接口；该接口目前标记为 Experimental，工具严格限定为已有页面的 `description` 字段。官方可靠接口仍不支持创建 mod 页面。创建 Main File 和修改正文的前提都是 mod 页面已经存在。

所有工具仅使用可靠 API、官方 SDK/CLI 或成熟程序接口。禁止浏览器自动化、computer-use、DOM/表单操作、模拟输入及其包装方案。按项目所有者要求，开发验证不登录 Nexus，不做真实上传，不读取真实凭据。

## 构建

```powershell
dotnet build .\NexusUploader\NexusUploader.csproj -c Release
pwsh -NoProfile -File .\NexusUploader\nexus.ps1 --help
```

需要 .NET 8 SDK/Runtime；没有第三方 NuGet 依赖。ZIP 由 .NET 生成，不需要 7-Zip。包装脚本不自动构建，也可以直接调用编译后的 `NexusUploader.dll`。

## 请求参数

从 [update-file.example.json](examples/update-file.example.json) 建立请求：

```json
{
  "requestId": "11111111-2222-4333-8444-555555555555",
  "modDirectory": "E:\\Games\\ExampleGame\\ExampleMod",
  "includeRootDirectory": true,
  "description": "Release file description.",
  "changelog": "Describe the changes for this release."
}
```

示例 requestId 仅为占位示意，每次新的逻辑操作必须生成新的 UUID。

| 字段 | 必填 | 含义 |
| --- | --- | --- |
| `requestId` | 是 | 本次逻辑发布的非空 UUID，用 `[guid]::NewGuid().ToString()` 生成。工具规范为小写；同一次操作保留同一个 ID，用于防止重复发布 |
| `modDirectory` | 是 | 准备发布的本地文件夹；调用 Agent 应传完整路径。根目录必须包含 `modinfo.ini`。工具只用末端最多三层目录生成不含盘符的绑定键，并用 `modinfo.ini` 名称二次校验；调用者不生成、保存或传递该键 |
| `includeRootDirectory` | 否 | 是否在 ZIP 中增加以 `modDirectory` 文件夹名命名的顶层目录；默认 `true`。设为 `false` 时文件直接放在 ZIP 根目录 |
| `description` | 否 | 此次上传的**文件版本说明**，不修改 mod 页面正文；不从 INI 的 description 自动继承。省略或 null 时不发送此字段；显式提供时须非空、无首尾空白，最多 50,000 字符 |
| `changelog` | 否 | 文件发布并核验成功后，为 INI 版本号追加的 mod 更新日志；不会替换旧日志。省略或 null 时跳过此调用；显式提供时须非空、无首尾空白，最多 50,000 字符 |
| `descriptionMode` | 否 | `template` 启用模板及上传后的正文同步；`manual` 让 update-file 只处理文件。首次未设置时保留原来的文件发布行为；后续省略时复用已保存的模式 |
| `descriptionProfile` | 否 | 游戏内的类型模板 ID，例如 lua、pak、blueprint 或自定义类型。首次启用模板必须选择；单独提供此字段也表示选择 template 模式。manual 模式不能同时提供它 |

`update-file` 请求 JSON 不接受 `operation`、`target`、`modId`、用户 ID、预期 mod 名称、页面 URL、动作选择、fileId、压缩包路径、fileName、version 或包摘要参数；操作由调用命令决定。ID/页面 URL 仅用于首次 `update-file --dry-run --mod` 或 `inspect --mod`。请求和输出没有 `schemaVersion`；旧请求不会兼容，未知/重复字段会报错。认证直接使用所提供的 API key，不进行额外账号身份比对。

### 单独修改页面正文

从 [update-info.example.json](examples/update-info.example.json) 建立另一份请求：

```json
{
  "requestId": "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
  "modDirectory": "E:\\Games\\ExampleGame\\ExampleMod"
}
```

| 字段 | 必填 | 含义 |
| --- | --- | --- |
| `requestId` | 是 | 本次正文修改的非空 UUID；同一次操作保留同一个 ID，用于防止重复写入 |
| `modDirectory` | 是 | 已登记绑定的完整本地 mod 文件夹；工具内部解析为固定全局 `modId`。即使只改页面正文，也必须提供它以避免修改错误页面 |
| `description` | 否 | 手动模式下可显式提供完整页面 BBCode，最多 1,000,000 字符。省略时固定读取根目录 doc.bbcode。模板模式不能提供此字段 |
| `descriptionMode` / `descriptionProfile` | 否 | 与 update-file 相同；省略时复用该目录的已保存设置 |

`update-info` 使用已经登记的目录绑定，不接受 `--mod`，也不接受 `operation`、`target`、`modId`、`summary`、标题、版本、文件说明或 changelog 字段。没有启用模板时，doc.bbcode 或显式 description 就是完整页面正文；启用模板时，doc.bbcode 是要被模板包裹的独立正文。工具读取远端原文，dry-run 展示修改前后的完整 BBCode，确认后整体替换并读回核验。生成结果与远端一致时跳过 PATCH。

## doc.bbcode 与描述模板

调用 Agent 必须在 mod 根目录维护 UTF-8 的 `doc.bbcode`，发布前重新读取并核对。功能、用法、配置、安装要求、兼容性或已知限制变化时同步更新；纯内部改动可保持原文。正文记录当前完整行为，changelog 记录本次变化。公共安装说明和公共页头页尾由 Uploader 模板维护，特殊情况写在该 mod 的独立正文里。Uploader 只读 mod 文件夹，不创建或改写说明文件。

可以从 [doc.bbcode 示例](examples/doc.bbcode) 开始。允许 UTF-8 BOM 和末尾换行，发布时规范化行尾并去除首尾空白；文件不得为空，最大 2 MiB，正文及组合结果最多 1,000,000 字符。拒绝目录、符号链接、无效 UTF-8 和敏感内容；用于页面的独立正文不能残留模板占位符。使用模板或省略 update-info.description 时必须存在它；文件发布的其他模式中可以没有，但已存在的根目录 doc.bbcode 仍须通过文件校验。

**根目录 doc.bbcode 始终不进入 ZIP**，不受 includeRootDirectory 或描述模式影响。只排除这个保留文件；子目录同名文件、其他 .bbcode 文件和名为 doc 的目录正常打包。删除或归档远端文件的行为不变。

模板由内到外包裹正文，各文件都必须恰好包含一个 `{{content}}`，可以自由编排插入点前后的 BBCode：

```text
modDirectory/doc.bbcode
  → descriptions/games/<gameDomain>/profiles/<profile>.bbcode
  → descriptions/games/<gameDomain>/game.bbcode
  → descriptions/global.bbcode
  → 最终页面正文
```

例如一种类型模板可以包含：

```bbcode
[b]Installation[/b]
该类型的安装说明。

{{content}}

[b]Uninstallation[/b]
该类型的卸载说明。
```

路径均相对于工具目录。游戏域名来自既有目录绑定或首次绑定候选，不由调用者猜测。类型 ID 只接受小写 ASCII 字母、数字、下划线和连字符，最长 64 字符；支持 lua、pak、blueprint 和自定义组合类型，不按文件后缀自动猜测。缺少任意一层模板或插入点无效时，上传前停止。独立正文中不允许模板占位符。

仓库提供通用的透传 global.bbcode，以及 examplegame 下三种类型的示例，**不是实际游戏的安装指南**。使用真实游戏前，先在其域名目录建立 game.bbcode 和正确的类型模板，核对前置工具、路径及步骤；不能把示例说明直接发布。模板可以提交 Git，禁止放入凭据。

首次启用可参考 [update-file-template.example.json](examples/update-file-template.example.json)，只额外选择一次：

```json
{
  "requestId": "11111111-2222-4333-8444-555555555555",
  "modDirectory": "E:\\Games\\ExampleGame\\ExampleMod",
  "descriptionMode": "template",
  "descriptionProfile": "lua"
}
```

成功确认后，模式、类型和最后同步正文的摘要保存在工具的 modconfig.json 中；不缓存 mod 独立正文作为后续来源。之后每次 update-file/update-info 只需 requestId 和完整 modDirectory，工具重新读取最新 doc.bbcode 及模板。不再传 modContent 或说明文件路径。显式重新指定模式/类型可以更改选择，dry-run 展示请求中的新设置，成功后才保存；dry-run 不改配置。

update-file 的原 description 字段始终是文件版本说明。template 模式上传流程为：打包上传 → 发布文件 → 读回核验 → 可选 changelog → 复查正文来源及远端原文 → 同步页面正文 → 读回核验 → 保存描述设置。manual 或尚未设置模式的 update-file 不改页面正文；update-info 仍可从 doc.bbcode 单独修改正文。

计划包含 `pageDescription.before`、`after`、两者摘要、来源文件摘要及 writeRequired。确认时使用已经审核的正文，模板、doc.bbcode 或远端原文变化都要求重新 dry-run；源文件变化即使不影响 ZIP，也不能忽略。上传过程中发生变化则保留已发布文件并返回部分完成，不会提交新生成的正文。未变更的正文跳过 PATCH。

远端正文若偏离上次同步摘要，返回 REMOTE_DESCRIPTION_CHANGED。调用 Agent 应先用 inspect 查看完整原文，核实网页改动；只有任务明确授权接管这些改动时，才能显式传 descriptionMode 重新 dry-run，审核 before/after 后确认。不能自动重复指定模式来绕过保护。

正文修改失败不能重传文件。回执的 update-description、verify-description、save-description-settings 阶段分别表示写入、读回或保存本地描述设置未全部完成。先用 inspect 核对远端；确有必要时，将仅补正文作为独立 update-info 操作处理，不能删除原回执或盲目重试原上传。

### 调用 Agent 的描述维护流程

1. 先阅读本说明和 AGENTS.md。已有绑定时，用 `inspect --mod-directory <完整路径>` 查看当前正文、`binding.descriptionSettings` 和可选类型模板。首次尚未绑定时，先按候选发现流程确认页面，再用 `inspect --mod <全局ID或官方URL>` 读取原正文；不要读取真实 config.json 或 modconfig.json 来查这些信息。
2. 已有 doc.bbcode 时，先读完整内容，再根据本次功能变化同步更新。首次建立说明时，结合项目现有权威文档和远端原正文整理：保留仍然有效的功能、用法、配置、兼容性、限制、专属图片和链接等独立内容。只有确定已由所选模板覆盖的公共内容才移出独立正文，避免丢失旧页面信息或重复安装章节。
3. 文件必须位于请求中 modDirectory 的根目录，使用真正的 BBCode；不能把 Markdown 或 HTML 原样当作已转换的正文。独立正文不含 `{{content}}`，也不保存套完模板的最终整页。若 modDirectory 是临时准备的发布目录，应在项目中持久维护说明源文件，准备发布目录时由调用方复制为根目录 doc.bbcode，不能只在临时目录或聊天记录中保存唯一版本。
4. 修改功能、使用方式、配置或兼容性时，同时维护说明；仅改说明时使用 update-info，无需为了修改正文上传新文件。无法从项目确认的内容不编造，不把文件版本说明 description 或 changelog 当作 mod 正文的替代品。
5. **已有 mod 绑定不代表已启用正文同步。** 用户要求文件与页面一起更新，而绑定尚未启用 template 模式时，须结合当前游戏和项目选择适配模板，在首次启用请求中提供 descriptionMode/descriptionProfile。类型无法确认时才向用户核实。缺少适配模板时停止模板发布并报告；不能把 examplegame 示例当作真实说明，也不能为单个 mod 随意修改公共模板。共享模板的修改须遵守所属项目的授权和文件范围要求。
6. dry-run 后审核 `plan.pageDescription.before` 和 `after`，确认独立内容被保留且公共片段只出现一次。`plan.pageDescription` 不存在表示本次没有同步页面正文，不能报告“描述已更新”；writeRequired=false 表示生成结果已与远端一致，无需 PATCH。正常确认时沿用同一请求和摘要，不在确认后重新采用未经审核的模板内容。
7. 发布完成后分别报告文件和正文结果。正文同步失败时核对回执及 inspect 的远端原文，保留已发布文件；不得重传文件或更换请求 ID 绕过未核实的失败。

### 图片与 BBCode 编辑

描述和模板可使用 `[img]https://公开图片直链[/img]`。模板里的公共图片可重复引用，mod 独有图片放在 doc.bbcode。拒绝本地路径、HTTP、data URL、带身份信息的 URL 和签名/凭据特征。图片必须先已有公开 HTTPS 地址；工具不上传图片，不修改 Nexus 图库，也不通过私有接口取得图片 URL。code/noparse 中的图片标签示例按文字处理。

日常直接编辑 mod 的 doc.bbcode。Uploader 只在 dry-run/发布时组合公共模板，供调用 Agent 审核最终 before/after BBCode，不提供 HTML 渲染或组合预览命令。

需要边编辑边预览单个 BBCode 文件时，可以使用 VS Code 的 [BBCode 扩展（JuanReateguiSolis）](https://marketplace.visualstudio.com/items?itemName=JuanReateguiSolis.bbcode-support)：打开 doc.bbcode 后按 Ctrl+K，再按 V，侧栏会随输入更新，并提供标签检查、补全和图片预览。另一选择是 [Steam Text Editor Preview](https://marketplace.visualstudio.com/items?itemName=MassimoMx.steam-text-editor-preview)，打开 .bbcode 后按 Ctrl+Shift+V。直接编辑和保存同一份 doc.bbcode 即可，开发 mod 仍可继续使用 VS2022。

以上是 VS Code 扩展，不是 VS2022 扩展。尚未找到可确认支持 VS2022 的专用 BBCode 实时预览插件；Preview-HTML/HTML 11 只处理 HTML。不同平台的 BBCode 方言和样式存在差异，这些编辑器预览不保证与 Nexus 完全一致。扩展均未由本项目安装或实测；不要仅因编辑器能够预览本地图片，就把本地路径写入发布正文。

## 文件夹与 modinfo.ini

```ini
name=ExampleMod
version=1.2.3
description=Local mod description
author=ExampleAuthor
```

- INI 使用 UTF-8（允许 BOM），最大 1 MiB。支持无节的键值或 `[ModInfo]` 节，键名不区分大小写，忽略其他节；支持空行、整行 `;` / `#` 注释。值不加引号、不使用行尾注释。`name`、`version` 必填，不允许重复。
- `name` 为 1–50 字符，须含字母或数字，只允许 ASCII 字母、数字、空格、下划线、单引号、括号、点和连字符。新建 Main File 时用作文件显示名称。
- `version` 为 1–50 字符，须含字母或数字，只允许 ASCII 字母、数字、点和连字符。用于文件版本、mod 页面版本、changelog 版本。
- 下载文件名自动生成为 `<name>-<version>.zip`：名称内空格改为下划线，去除首尾点，Windows 保留名称加 `mod-` 前缀。例如 `Test Mod` / `2.0` 得到 `Test_Mod-2.0.zip`。
- 默认以传入文件夹的实际名称增加一层 ZIP 顶层目录。例如传入 `E:\Mods\Clock`，条目为 `Clock/modinfo.ini`、`Clock/...`。设置 `includeRootDirectory: false` 后，内容才直接位于 ZIP 根目录。两种模式都包含 `modinfo.ini`、子目录和空目录。不编译源代码，不修改 INI，不自动过滤源码或运行时配置。调用任务先按所属项目规则准备干净的发布目录。
- 条目按路径排序，ZIP 时间固定；同一工具运行环境中，路径与内容相同会得到相同字节和 SHA-256。仅修改源文件时间不会使计划失效。
- dry-run 与 execute 都生成并检查临时 ZIP。临时存储名为随机名，上传时使用上面的下载文件名；退出打包/上传流程时自动删除临时 ZIP，不在源目录留下压缩包。

## 自动选择 Main File

Main File 按**文件组**计数：`is_active=true` 且至少一个版本的 `category=main`。同一文件组的多个 main 版本仍算一个 Main File；纯 old_version/archived/removed 历史不算当前 Main File。

| 当前 Main File 数量 | 动作 |
| --- | --- |
| 0 | 在已有 mod 页面创建 Main File，显示名称来自 INI 的 name |
| 1 | 为唯一 Main File 添加版本，自动使用其 fileId，保留该文件组的显示名称 |
| 大于 1 | 返回 `MAIN_AMBIGUOUS`，不上传，不猜测 |

`is_primary` 不是目标选择条件；另一个分类当前为默认下载也不阻止创建或更新。每次发布固定使用 `file_category=main`、`primary_mod_manager_download=true`、`update_mod_version=true`，因此新版本会成为默认下载并更新 mod 页面版本。工具不发送删除或归档旧文件的字段。

版本号按不区分大小写的方式与所有文件的完整历史比较，包含非活动文件。版本已使用时返回 `DUPLICATE_VERSION`。更新时，远端文件组显示名称也必须符合上述 Nexus 名称限制。

动作由 dry-run 时的状态确定并记录在计划 `intent.action` 中（`create` / `update`）。执行前和上传后都会复查远端；若状态改变，停止并要求重新核对，不在既有计划下静默切换动作。

## 凭据与调用流程

工具固定自动读取自身目录的 `config.json`，内部只使用 `NEXUSMODS_API_KEY`。调用任务不得打开、打印、搜索或复制配置，也不能通过命令行覆盖配置路径。

发布目标固定保存在工具目录的 `modconfig.json`。每条记录保存工具生成的目录键、`modinfo.ini` 名称、页面名称及 Nexus ID；不保存盘符或完整本地路径。目录键取规范化后的末端最多三层，例如 `E:\OtherGame\DragonDogma2\MyDD2Mod\Clock` 得到 `dragondogma2/mydd2mod/clock`，因此只改变盘符或更上层路径不会丢失绑定。调用者始终传完整路径，由工具生成和查找目录键；若末端三层冲突、INI 名称变化或没有唯一绑定，工具停止而不猜测。两个真实配置均被 Git 忽略；模板 [config.example.json](config.example.json) 和 [modconfig.example.json](modconfig.example.json) 位于同一工具目录。`modconfig.json` 不含 API key 或原始 URL，但可能标识未公开 mod，因此同样不提交。

```powershell
$tool = 'E:\MyWebsiteHelper\MyUploader\NexusUploader\nexus.ps1'

pwsh -NoProfile -File $tool target candidates

# 首次上传：在 dry-run 中指定已选定的候选 ID 或官方页面 URL
pwsh -NoProfile -File $tool update-file `
  --request 'E:\MyRelease\release.json' `
  --dry-run --mod '<global-mod-id-or-official-page-url>'

# 审查计划后确认：此时不再传 --mod，工具保存绑定并上传
pwsh -NoProfile -File $tool update-file `
  --request 'E:\MyRelease\release.json' `
  --confirm '<planSha256>'

pwsh -NoProfile -File $tool inspect --mod '<global-mod-id-or-official-page-url>'

pwsh -NoProfile -File $tool inspect --mod-directory 'E:\Games\ExampleGame\ExampleMod'

# 后续更新：先准备新版本和新的 requestId，再使用不带 --mod 的 dry-run
pwsh -NoProfile -File $tool update-file `
  --request 'E:\MyRelease\release.json' `
  --dry-run

pwsh -NoProfile -File $tool update-info `
  --request 'E:\MyRelease\update-info.json' `
  --dry-run
```

`target candidates` 使用官方 v3 `team-mods` 接口分页读取当前 API key 用户拥有或作为团队成员参与的页面。它排除已经登记的 ID、公开/审核中/已删除页面，以及任何已有文件组的页面，只返回状态为 `not_published` 或 `hidden` 且文件组为零的候选。输出包含全局 ID、游戏域名及名称、页面 ID、页面名称、状态、团队关系和文件组数量；只发送 GET，不写配置，不做字符串或 AI 猜测。接口响应缺页、列表并发变化、未知状态或超过 10,000 个团队 mod 时安全停止。

`update-file --request <JSON> --dry-run --mod <value>` 把首次绑定并入上传。`--mod` 严格接受两种格式：纯数字始终表示全局 v3 `modId`；官方 HTTPS mod 页面 URL 则通过 `getMod` 解析。URL 必须为 `https://nexusmods.com/<game-domain>/mods/<page-id>` 或带 `www` 的同一格式，可带普通查询参数和片段；拒绝其他主机、HTTP、用户信息、自定义端口和编码路径。数字 ID 必须来自当前 `target candidates` 候选，dry-run 和确认时都会查询并确认它仍然未登记、非公开且没有文件组；URL 保留为候选接口不可用或人工明确页面时的后备方式。工具校验 API 返回的页面 ID 与 URL 一致，不会把 URL 内的游戏页面 ID 当成全局 ID。

`--mod` 只允许出现在尚未绑定目录的首次 update-file dry-run，确认调用和 update-info 均不接受。dry-run 只把拟绑定的页面、游戏、目录标识及来源写入 `plan.registration`，不写 `modconfig.json`，不保存原始 URL/查询参数。Agent 必须连同文件发布计划一起审查绑定。确认时使用同一请求和摘要，从计划恢复目标；复查本地目录、远端身份/候选资格、包内容及文件状态后，以本地互斥锁和原子替换保存绑定，然后开始远端上传。原独立登记命令已删除。

已有目录绑定时，即使 ID 相同也拒绝传入 `--mod`；目录键或全局 ID 已存在时禁止覆盖或重复登记。首次计划确认前若有其他任务完成登记，返回 TARGET_EXISTS，须去掉 --mod 重新 dry-run。绑定保存与远端上传不是同一个事务：保存后上传失败时保留绑定和回执，不能把已有绑定视为上传成功，也不能删除回执或更换 requestId 重试。

`inspect` 必须且只能选择统一的 `--mod` 或 `--mod-directory`。`--mod-directory` 输出当前目录的 binding 和完整文件历史 target，binding 含描述模式、类型及最后同步摘要。`--mod` 为纯数字时按全局 v3 ID 处理，为官方页面 URL 时先解析并输出不含原始 URL/查询参数的 resolution；直接按 ID/URL 查询不读取绑定。两种方式都返回当前远端 BBCode description（无正文时可能省略），按目录或 URL 查询还列出本地 descriptionProfiles，纯全局 ID 查询不猜测游戏域名。只有当前绑定被返回。只读查询成功不代表具有写权限，实际权限由 Nexus 写接口判定。

### 面向调用 Agent 的首次登记流程

调用 `update-file` 或 `update-info` 时在请求中只传完整 `modDirectory`。绑定已存在时工具直接找到固定全局 ID，不需要 Agent 记住名称、目录键或 ID，也不得传 --mod 重复发现或登记。首次 update-file 收到 `TARGET_NOT_FOUND` 或 `MODCONFIG_MISSING` 时，调用 Agent 必须先运行 `target candidates`，不得立即要求用户提供 URL。Agent 应结合当前任务中的游戏、项目含义、目录名、`modinfo.ini` 名称以及候选的游戏和页面名称判断：只有一个合理候选时，可使用同一请求执行 `update-file --request <JSON> --dry-run --mod <candidate-modId>`；禁止仅按列表顺序、最新页面或单一字符串相似度选择。存在多个合理候选或证据不足时，才向用户列出安全的候选信息并核实。审核计划后通过 --confirm 完成绑定和上传；官方候选接口不可用时才退回明确的官方页面 URL。update-info 只用于已绑定目录，不能单独建立首次绑定。

dry-run 的 JSON 包含 `status: dry_run_ok`、完整 `plan` 和 `planSha256`。计划里的 `request.operation` 由工具根据命令生成，调用方不填写。`update-file` 计划包含规范化请求、远端文件历史、`package`（INI 名称/版本、ZIP 名称/大小、自动计算的 SHA-256、文件数及启用时的 `rootDirectory`）以及 `intent`（自动动作、目标 fileId、显示名称与分类）。首次绑定时还包含 `registration`，其中 source 为 candidate 或 url，target 包含拟绑定的页面名称、游戏域名、各 ID、目录键和 INI 名称；后续更新不包含它。该绑定方案也受计划摘要保护。调用任务检查这些内容、ZIP 顶层目录及拟发布的文件说明/changelog。`update-info` 计划包含规范化请求及完整页面正文，不创建压缩包。两者的 dry-run 远端传输层都仅允许 GET，不发送修改或创建上传会话。

计划由工具自动保存在自身目录的 `.nexus-state/plans/<requestId>/<planSha256>.plan.json`，调用方不指定路径，也不传计划 ID；`--plan` 参数已移除。同一个 requestId 可有多次 dry-run 记录，每份以完整摘要区分，已有计划不覆盖。计划不含凭据/签名 URL，但含本地源路径和拟发布内容，整个状态目录被 Git 忽略。计划有效期为 15 分钟。

已有任务上下文授权这次发布或正文替换后，使用相同请求执行：

```powershell
pwsh -NoProfile -File $tool update-file `
  --request 'E:\MyRelease\release.json' `
  --confirm '<planSha256>'

pwsh -NoProfile -File $tool update-info `
  --request 'E:\MyRelease\update-info.json' `
  --confirm '<planSha256>'
```

`--confirm` 必须原样使用 dry-run 返回的完整小写 `planSha256`。工具重新读取请求，用规范化 requestId 和该摘要精确定位并校验已保存计划；不会选取最新计划、扫描其他请求或使用外部计划文件。计划不存在返回 `PLAN_NOT_FOUND`，摘要不合法或内容被改动返回 `PLAN_CONFIRMATION`；均在远端写入前停止。摘要不是要求调用方计算压缩包摘要，也不代替发布授权。没有交互提示、默认执行模式、`--yes` 或 `--force`。

`--dry-run` 与 `--confirm <planSha256>` 必须且只能选择一个；传入确认摘要即明确请求执行。`--execute` 已移除，继续传入会报未知参数。两者都不传、同时传入或确认参数缺少值时，均在读取凭据和访问网络前拒绝。

### 上传步骤与调用接口

| 步骤 | 调用接口 | 说明 |
| --- | --- | --- |
| 准备发布目录和请求 | 无需调用工具接口 | 目录根部提供 modinfo.ini，准备干净内容；请求使用新 requestId 和完整 modDirectory |
| 生成发布计划 | `update-file --request <JSON> --dry-run` | 自动查绑定、打包、检查远端并返回计划摘要，只向远端发送 GET |
| 缺少绑定时获取候选 | `target candidates` | 仅在上述调用返回 TARGET_NOT_FOUND / MODCONFIG_MISSING 时使用，由 Agent 选择可确认的页面 |
| 首次提出绑定 | `update-file --request <同一JSON> --dry-run --mod <ID或URL>` | 重新生成含首次绑定方案的计划；此时不保存绑定 |
| 审查计划 | 无需额外接口 | 审核目标 modId、ZIP 结构、版本、自动动作及拟发布文本，保留 planSha256 |
| 上传发布 | `update-file --request <同一JSON> --confirm <planSha256>` | 复核计划、重新打包并复查远端；首次保存绑定，再上传、创建/更新 Main File、读回验证、可选 changelog，模板模式下最后同步并核验正文 |
| 成功后收尾 | 无需额外接口 | 返回成功回执及 cleanup 统计，自动清理符合条件的旧计划并退出 |
| 异常核对 | `inspect --mod-directory <full-path>` 或 `inspect --mod <全局ID或URL>` | 配合回执核对远端文件历史；正常发布不要求额外调用 inspect，退出码 4 禁止盲目重试 |

后续文件更新复用绑定，通常只需要一次 dry-run 和一次带确认摘要的调用。若还要修改页面正文，另建具有新 requestId 的 update-info 请求，先调用 `update-info --request <JSON> --dry-run`，审核完整正文后调用 `update-info --request <同一JSON> --confirm <planSha256>`。

成功执行后，输出还包含 `cleanup`，记录删除计划数、删除空目录数、保留计划数及清理失败项数。存在无法清理的项时附带 `warningCode: CLEANUP_INCOMPLETE`；发布仍保持 `status: success` 和退出码 0，不能因此重新上传或重新修改正文。

`update-info` 读取 modinfo.ini 校验目录绑定，执行前复查正文来源和远端原文。正文不同时发送一次 PATCH，成功后 GET 读回核验；相同则跳过 PATCH 并核对现状。HTTP 204 不是最终的正文一致性依据。写请求发出后的超时、取消、非预期响应或核验失败返回退出码 4，不能直接重试。成功后保存描述设置及同步摘要。该操作不打包、不上传、不修改文件版本和 changelog。

dry-run 会把派生的 `directoryKey` 及当时解析出的全局 `modId` 一并写入计划。执行前重新读取 `modconfig.json`；如果目录绑定改为另一个 modId，请求与计划将不匹配并停止。执行还会重新读取 INI、重新打包并比较计划中的包信息。修改、增加、删除或重命名源文件，改版本，改请求，或远端文件历史变化，都必须重新 dry-run。工具对每个 modId 使用同一安装目录中的互斥锁，所有调用任务应共用该目录。

## 防护与失败处理

- 打包前先检查完整文件名清单，拒绝 Git/发布状态元数据、`updater.json`、`config.json`、`modconfig.json`、`credentials.json`、`cookies.json`、`.env*`、storage-state、`*.secret.json`、PEM/key 等敏感路径，遇到这些文件名不读取内容、不静默排除后继续发布。
- 拒绝路径穿越、大小写路径冲突、符号链接、目录联接及父路径重解析点。最多 10,000 条目、8 GiB 源内容。每个文件流式扫描配置 key 及 `NEXUSMODS_API_KEY` 的 UTF-8/UTF-16 特征。不能识别所有未知秘密或嵌套压缩内容，源文件夹仍须由调用项目审核。
- 打包期间持有源文件只读共享句柄，Windows 上禁止内容写入/删除；打包后复查目录结构。上传使用独立临时 ZIP 句柄，不依赖持续读取正在开发的源文件；上传按 128 KiB 缓冲分片流式读取。
- API 与存储客户端隔离，存储不携带 API key，均禁用自动重定向。只接受 AWS/R2 的 HTTPS 存储地址。禁止输出原始 HTTP 错误、响应体、签名地址及凭据，不能启用 HTTP 调试日志。
- 上传合并不仅检查 HTTP 200，还核验 S3 完成 XML。关联文件前再查远端状态，关联后读回主要版本，最后才写 changelog。
- 更新文件时以发布响应的 `version.id` 定位新版本，读回目标 mod 的完整历史，核对它属于计划中的文件组且名称、版本、分类、主要下载状态均正确。写响应的 `file.id` 单独记为诊断字段，不用它直接判定文件组归属；读回归属错误仍返回 `PUBLISH_MISMATCH`，核验不通过不会写 changelog。
- 首次创建 Main File 时，Nexus 的顶层响应 ID 可能表示新版本或文件组。工具不根据该字段猜测类型，而是读回完整历史，要求名称、版本、分类、主要下载状态唯一匹配，并要求响应 ID 与读回的版本 ID 或文件组 ID 至少一个一致。回执最终记录读回确认的真实版本 ID 和文件组 ID。
- 没有自动网络重试。先持久化回执再发第一个写请求；文件发布、changelog、正文和本地设置保存不是原子事务，外部调用仍可能在最终检查后并发修改。

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功：`candidates` / `dry_run_ok` / `inspected` / `success` |
| 2 | 输入、校验或不支持的操作；修正具体问题，不能绕过防护 |
| 3 | 网络/API/环境错误；仅在确认未开始写入后排查 |
| 4 | 部分完成或结果不确定，核对回执及远端，禁止直接重试 |
| 130 | 写入前取消；写入开始后的取消返回 4 |

回执位置：工具目录 `.nexus-state/<requestId>.receipt.json`。包含 status、stage、安全的 upload/file ID 和 errorCode，不含原始错误。`changelog` 失败时文件可能已成功，不能重传；`publish-file` 断线时即使没有收到 ID 也须核对。

新回执还记录 `modId`、`targetFileId`（读回确认的目标文件组）及 `responseFileId`（写响应中的诊断 ID）。读回核验成功后，`publishedId` 始终是实际新版本 ID；核验失败会保留在 `verify-file` 阶段，便于只读排查。旧回执缺少这些字段或在首次创建时记录了未经读回归类的响应 ID，不能据此判断远端没有创建成功，也不能重传来复现响应。

已尝试发布或修改正文的 requestId 会被拒绝再次执行。不得删除回执、更换 requestId 或清空状态目录绕过防护。锁文件可以保留，活跃锁以系统句柄为准。沙箱 Schannel/TLS 错误应在沙箱外核实，不应据此判定凭据失效。

## 文件保留与清理

每次 `update-file --confirm <planSha256>` 或 `update-info --confirm <planSha256>` 成功后，工具顺带清理旧计划，完成后退出，不需要常驻进程。dry-run、只读查询及失败的发布不触发清理。计划的 15 分钟有效期与一个月的清理保留期分别计算。

| 文件 | 当前清理行为 | 建议 |
| --- | --- | --- |
| 临时 ZIP | 关闭打包句柄时自动删除，正常成功和异常退出流程均会关闭句柄 | 无需日常清理 |
| `.nexus-state/plans/` 下的计划 | 成功 execute 后清理到期已超过一个自然月、没有回执或回执确认成功的计划 | 部分完成、结果不确定或无法校验的计划保留供核对 |
| `.nexus-state/<requestId>.receipt.json` | 长期保留 | 保留防重复执行和核对记录，不作为垃圾文件清理 |
| `.lock` 文件 | 保留；是否加锁由活跃句柄决定 | 体积很小，无需清理，不在运行期间删除 |
| `config.json`、`modconfig.json` 及调用者的请求文件 | 不做清理 | 由各自所有者管理 |

清理阈值是“当前 UTC 时间减一个自然月”，仅删除到期时间严格早于阈值的计划，不使用文件创建/修改时间。范围限定为 `.nexus-state/plans/<requestId>/<planSha256>.plan.json`，且必须验证文件名、请求身份、完整摘要和有效期信息一致。只删除通过检查的单个文件及本次清空的目录，不递归删除，不跟随符号链接或目录联接。

没有回执的旧计划可以删除；有回执时，只有 requestId、modId 一致且状态为 success、阶段为 complete 才删除。partial/uncertain、其他非成功状态、损坏/身份不一致的回执和残留写入临时回执均保留对应计划。清理与发布共用 mod 互斥锁，正在写入的目标会保留并提示，无需等待所有 Agent 的命令结束。清理无法完成时只返回上述提示，不改变发布结果。

需要手动处理保留项时，应先核对部分完成或结果不确定的操作，并按审计需要决定是否保留其计划。不要递归清空整个 `.nexus-state/`，也不要删除执行回执；即使发布成功，其回执仍用于阻止重复执行。旧版由调用者指定路径生成的计划不会被自动查找、迁移或删除，可由其所有者处理。

清理过期计划后需要执行同一尚未尝试写入的逻辑操作时，保留原 requestId 并重新 dry-run；已尝试写入的操作仍须先核对回执及远端，不能用清理或新 requestId 绕过。

## 离线测试

```powershell
dotnet run --project .\NexusUploader\tests\NexusUploader.Tests.csproj
```

使用合成文件夹、临时凭据与内存 HTTP handlers，不访问真实 Nexus，不读取真实配置，不启动浏览器。覆盖候选筛选及只读约束、目录绑定、自动计划存取及防篡改、自动打包/选目标、根目录说明文件排除、模板包裹组合及保存复用、正文差异和回读、INI、敏感内容、目录联接、源内容及远端变化、分片上传、互斥与部分失败，以及已移除接口、旧参数和不支持操作的拒绝。

# NexusUploader

供其他 Codex 任务调用的 .NET 8 命令行工具，通过 Nexus 官方 v3 API 发布文件。输入 mod 文件夹，工具读取 `modinfo.ini`、生成 ZIP，并自动选择新建 Main File 或添加版本。

## 能力

| 命令 | 功能 |
| --- | --- |
| `inspect` | 按全局 `modId` 读取文件及全部版本历史，只发送 GET |
| `update-file` | 打包文件夹、分片上传、自动创建/更新 Main File、同步页面版本，可追加 changelog |
| `create-mod` / `update-info` | 当前不支持；立即返回 `UNSUPPORTED_OPERATION`，退出码 2 |

文件链路参考用户指定的 Updater、[Nexus 官方 OpenAPI](https://github.com/Nexus-Mods/Vortex/blob/master/packages/nexus-api-v3/schema/openapi.yaml) 和 [Nexus 官方上传 Action](https://github.com/Nexus-Mods/upload-action)。当前接入的可靠接口不包含创建 mod 页面或编辑页面标题/正文的操作。创建 Main File 的前提是 mod 页面已经存在。

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
  "operation": "update-file",
  "modId": "12345",
  "modDirectory": "../release/ExampleMod",
  "description": "Release file description.",
  "changelog": "Describe the changes for this release."
}
```

示例 ID 仅为占位示意，必须替换为目标的真实全局 ID。

| 字段 | 必填 | 含义 |
| --- | --- | --- |
| `requestId` | 是 | 本次逻辑发布的非空 UUID，用 `[guid]::NewGuid().ToString()` 生成。工具规范为小写；同一次操作保留同一个 ID，用于防止重复发布 |
| `operation` | 是 | 固定为 `update-file` |
| `modId` | 是 | Nexus **v3 全局 mod ID**，正整数字符串；不是 URL 中的游戏内页面 ID，不得直接互换或猜测 |
| `modDirectory` | 是 | 准备发布的本地文件夹；支持绝对路径，或相对**请求 JSON 所在目录**的路径。根目录必须包含 `modinfo.ini` |
| `description` | 否 | 此次上传的**文件版本说明**，不修改 mod 页面正文；不从 INI 的 description 自动继承。省略或 null 时不发送此字段；显式提供时须非空、无首尾空白，最多 50,000 字符 |
| `changelog` | 否 | 文件发布并核验成功后，为 INI 版本号追加的 mod 更新日志；不会替换旧日志。省略或 null 时跳过此调用；显式提供时须非空、无首尾空白，最多 50,000 字符 |

不再接受用户 ID、预期 mod 名称、页面 URL、动作选择、fileId、压缩包路径、fileName、version 或包摘要参数。请求和输出没有 `schemaVersion`；旧请求不会兼容，未知/重复字段会报错。认证直接使用所提供的 API key，不进行账号或所有者比对。

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
- 文件夹内容直接放在 ZIP 根目录，包含 `modinfo.ini`、子目录和空目录；不额外套一层文件夹。不编译源代码，不修改 INI，不自动过滤源码或运行时配置。调用任务先按所属项目规则准备干净的发布目录。
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

`--config` 指向本地 JSON，工具内部只使用 `NEXUSMODS_API_KEY`。调用任务不得打开、打印、搜索或复制配置。可以传入已知的旧 Updater 配置路径，其他条目及其 `mods` 映射不会被使用。

本地配置放在 `NexusUploader/credentials.json`，已被 Git 忽略；初始 `NEXUSMODS_API_KEY` 为空，由用户在本地填写。新 checkout 可根据 [空白配置模板](examples/credentials.example.json) 创建此文件，真实配置不得提交。

```powershell
$tool = 'E:\MyWebsiteHelper\MyUploader\NexusUploader\nexus.ps1'
$credentialPath = 'E:\MyWebsiteHelper\MyUploader\NexusUploader\credentials.json'

pwsh -NoProfile -File $tool inspect --mod-id '<global-mod-id>' --config $credentialPath

pwsh -NoProfile -File $tool update-file `
  --request 'E:\MyRelease\release.json' --config $credentialPath `
  --dry-run --plan 'E:\MyRelease\release.plan.json'
```

`inspect` 输出 `target.modId` 和文件/版本历史，不解析 URL，不查询账号或所有者。只读请求成功不代表具有发布权限，实际权限由 Nexus 写接口判定。

dry-run 的 JSON 包含 `status: dry_run_ok`、完整 `plan` 和 `planSha256`。计划包含规范化请求、远端文件历史、`package`（INI 名称/版本、ZIP 名称/大小、自动计算的 SHA-256、文件数）以及 `intent`（自动动作、目标 fileId、显示名称与分类）。调用任务检查这些内容及拟发布的 description/changelog。远端传输层仅允许 GET，不创建上传会话、不上传文件。

计划是本地新文件，已有文件不覆盖。计划不含凭据/签名 URL，但含本地源路径和拟发布内容，保存在受控目录。计划有效期为 15 分钟。

已有任务上下文授权这次发布后，使用相同请求执行：

```powershell
pwsh -NoProfile -File $tool update-file `
  --request 'E:\MyRelease\release.json' --config $credentialPath `
  --execute --plan 'E:\MyRelease\release.plan.json' --confirm '<planSha256>'
```

`--confirm` 核对 dry-run 计划摘要，不是要求调用方计算压缩包摘要，也不代替发布授权。没有交互提示、默认执行模式、`--yes` 或 `--force`。

执行会重新读取 INI、重新打包并比较计划中的包信息。修改、增加、删除或重命名源文件，改版本，改请求，或远端文件历史变化，都必须重新 dry-run。工具对每个 modId 使用同一安装目录中的互斥锁，所有调用任务应共用该目录。

## 防护与失败处理

- 打包前先检查完整文件名清单，拒绝 Git/发布状态元数据、`updater.json`、`credentials.json`、`cookies.json`、`.env*`、storage-state、`*.secret.json`、PEM/key 等敏感路径，遇到这些文件名不读取内容、不静默排除后继续发布。
- 拒绝路径穿越、大小写路径冲突、符号链接、目录联接及父路径重解析点。最多 10,000 条目、8 GiB 源内容。每个文件流式扫描配置 key 及 `NEXUSMODS_API_KEY` 的 UTF-8/UTF-16 特征。不能识别所有未知秘密或嵌套压缩内容，源文件夹仍须由调用项目审核。
- 打包期间持有源文件只读共享句柄，Windows 上禁止内容写入/删除；打包后复查目录结构。上传使用独立临时 ZIP 句柄，不依赖持续读取正在开发的源文件；上传按 128 KiB 缓冲分片流式读取。
- API 与存储客户端隔离，存储不携带 API key，均禁用自动重定向。只接受 AWS/R2 的 HTTPS 存储地址。禁止输出原始 HTTP 错误、响应体、签名地址及凭据，不能启用 HTTP 调试日志。
- 上传合并不仅检查 HTTP 200，还核验 S3 完成 XML。关联文件前再查远端状态，关联后读回主要版本，最后才写 changelog。
- 没有自动网络重试。先持久化回执再发第一个写请求；文件发布与 changelog 不是原子事务，外部调用仍可能在最终检查后并发修改。

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功：`dry_run_ok` / `inspected` / `success` |
| 2 | 输入、校验或不支持的操作；修正具体问题，不能绕过防护 |
| 3 | 网络/API/环境错误；仅在确认未开始写入后排查 |
| 4 | 部分完成或结果不确定，核对回执及远端，禁止直接重试 |
| 130 | 写入前取消；写入开始后的取消返回 4 |

回执位置：工具目录 `.nexus-state/<requestId>.receipt.json`。包含 status、stage、安全的 upload/file ID 和 errorCode，不含原始错误。`changelog` 失败时文件可能已成功，不能重传；`publish-file` 断线时即使没有收到 ID 也须核对。

已尝试发布的 requestId 会被拒绝再次执行。不得删除回执、更换 requestId 或清空状态目录绕过防护。锁文件可以保留，活跃锁以系统句柄为准。沙箱 Schannel/TLS 错误应在沙箱外核实，不应据此判定凭据失效。

## 离线测试

```powershell
dotnet run --project .\NexusUploader\tests\NexusUploader.Tests.csproj
```

使用合成文件夹、临时凭据与内存 HTTP handlers，不访问真实 Nexus，不读取真实配置，不启动浏览器。覆盖自动打包/选目标、INI、敏感内容、目录联接、源内容及远端变化、分片/读回/同步信息、互斥与部分失败，以及旧参数和不支持操作的拒绝。

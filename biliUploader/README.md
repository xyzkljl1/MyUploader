# biliUploader

供其他 Codex 任务调用的 Rust 命令行工具。底层固定使用 [biliup v1.2.2](https://github.com/biliup/biliup/releases/tag/v1.2.2) 库，提供创建 Bilibili 稿件、替换已有分 P、更新稿件信息、dry-run 计划、执行确认和防重复回执。

所有功能只使用程序接口。工具不实现浏览器自动化、DOM 操作或模拟输入，不读取浏览器配置。

## 当前能力

| 命令 | 功能 |
| --- | --- |
| `login` | 在终端显示二维码，扫码后自动生成同目录 `config.json` |
| `inspect` | 按 BVID 查询稿件和分 P，只读 |
| `create` | 上传一个视频并创建单 P 稿件，可设为公开或仅自己可见 |
| `update-video` | 替换已有稿件中的一个分 P，可同时修改可见性 |
| `update-info` | 更新明确传入的标题、简介、标签、分区、版权、来源、封面或可见性 |
| `set-chapters` | 当前返回 `UNSUPPORTED_CHAPTER_WRITE`，不读取凭据、不上传 |

biliup v1.2.2 没有公开可靠的原生章节写入接口。请求中只要出现 `chapters`，工具就会在本地预检阶段停止。简介时间戳不被当作原生章节。

## 构建

需要 Rust stable；首次构建需要下载依赖：

```powershell
cargo build --manifest-path .\biliUploader\Cargo.toml --release
pwsh -NoProfile -File .\biliUploader\bili.ps1 --help
```

依赖通过 `Cargo.lock` 固定；biliup 同时固定到 Git 标签 `v1.2.2`。包装脚本不自动构建。

## 凭据

本工具直接使用内嵌的 biliup v1.2.2 登录接口，不需要另行下载或定位 `biliup.exe`。首次使用只需运行：

```powershell
pwsh -NoProfile -File .\biliUploader\bili.ps1 login
```

程序会在终端显示二维码。使用哔哩哔哩手机客户端扫码并确认后，程序自动把完整认证状态写入 `biliUploader/config.json`。不使用浏览器自动化，也不需要复制浏览器 Cookie。

配置文件统一使用 `biliUploader/config.json`，该名称已被 Git 忽略。`login`、`inspect` 和所有发布命令都会自动定位它，正常调用无需传配置路径。仅当工具被放在特殊目录结构中时，才使用可选的 `--config <...\config.json>` 覆盖路径。

其他任务不能打开、打印、搜索或复制真实配置。工具不会输出 Cookie、Token、签名地址或上游原始错误。

`config.json` 是认证状态缓存，不是需要手工填写的普通设置。`cookie_info` 保存接口会话，`token_info` 保存访问令牌、刷新令牌和账号 ID，`sso` 与 `platform` 是 biliup 登录及令牌签名所需的上游字段。空白示例不能用于登录或投稿，也不应手工拼装真实值。

登录失效时重新运行 `bili.ps1 login`。它只在扫码并确认成功后覆盖配置；不会把二维码保存为图片，也不会打印认证内容。

## 请求

从 [创建示例](examples/create.example.json)、[替换视频示例](examples/update-video.example.json)、[更新信息示例](examples/update-info.example.json) 或 [章节能力示例](examples/set-chapters.example.json) 复制请求。未知字段、重复字段和错误类型均被拒绝。

通用字段：

- `requestId`：非空 UUID。一次逻辑操作始终使用同一个值。
- `operation`：`create`、`update-video` 或 `update-info`。
- `visibility`：可选值为 `public` 或 `self-only`。创建时省略则为公开；更新时省略则保留远端状态。
- 文件路径相对请求 JSON 所在目录解析，计划中保存规范化绝对路径、大小和 SHA-256。

创建字段：

- `videoPath`、`title`、`tags`、`categoryId`、`copyright` 必填。
- `copyright` 为 `original` 或 `reprint`；转载必须提供 `source`。
- `description`、`coverPath`、`partTitle`、`visibility` 可选。

替换视频字段：

- `bvid`、`videoPath` 必填。
- 单 P 稿件自动选中；多 P 稿件必须提供属于该 BVID 的 `cid`。
- `partTitle` 可选；省略时保留原分 P 标题。`visibility` 可选；提供时同时修改整个稿件的可见性。

更新信息字段：

- `bvid` 必填，且至少传入一个更新字段。
- 可更新 `title`、`description`、`tags`、`categoryId`、`copyright`、`source`、`coverPath`、`visibility`。
- 未提供字段保留远端原值；完全相同的更新会被拒绝。

## 使用流程

```powershell
$tool = 'E:\MyWebsiteHelper\MyUploader\biliUploader\bili.ps1'

pwsh -NoProfile -File $tool login

pwsh -NoProfile -File $tool inspect --bvid 'BV1xxxxxxxxx'

pwsh -NoProfile -File $tool create `
  --request 'E:\Publish\create.json' `
  --dry-run --plan 'E:\Publish\create.plan.json'

pwsh -NoProfile -File $tool create `
  --request 'E:\Publish\create.json' `
  --execute --plan 'E:\Publish\create.plan.json' --confirm '<planSha256>'
```

`update-video` 和 `update-info` 使用相同流程，只需替换子命令。dry-run 对更新操作发送只读查询；它不会上传视频、封面或提交修改。计划会明确显示目标可见性。计划有效期为 15 分钟，执行前会重新计算本地文件摘要并重新读取远端稿件；提交后还会回读并校验可见性。

媒体路径规范化时只检查类型、扩展名和大小，dry-run 与 execute 各完整读取一次媒体来计算或复核 SHA-256。execute 在摘要复核前取得媒体保护句柄并持有到上传结束；Windows 上同时禁止其他进程写入、删除或替换计划中的视频和封面。

## 状态与错误

回执保存在可执行文件旁的 `.bili-state/<requestId>.receipt.json`。写操作开始前先落盘；已经有回执的 requestId 不允许再次执行。所有发布共用系统文件锁，避免多个任务并发上传。

| 退出码 | 含义 |
| --- | --- |
| 0 | 查询、dry-run 或执行成功 |
| 2 | 输入错误、目标不匹配、计划失效或功能不支持 |
| 3 | 凭据、网络或本地环境错误，且尚未确认开始发布写入 |
| 4 | 上传或提交可能部分完成，必须人工核对，禁止直接重试 |
| 5 | 已脱敏的内部错误 |

## 离线测试

```powershell
cargo test --manifest-path .\biliUploader\Cargo.toml
cargo audit --file .\biliUploader\Cargo.lock --ignore RUSTSEC-2023-0071
```

测试只使用合成本地文件和合成 API 数据，不访问 Bilibili，不读取真实凭据。`rsa` 审计例外的不可达性及约束记录在 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

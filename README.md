# MyUploader

独立小工具集合，每个工具放在自己的顶层目录。

所有工具只通过可靠 API、官方 SDK/CLI 或成熟的程序接口实现功能；禁止浏览器自动化、computer-use、模拟点击/键盘及其包装方案。缺少可靠接口的操作明确标记为不支持。

| 工具 | 用途 | 调用说明 |
| --- | --- | --- |
| NexusUploader | 通过官方 API 发布 Nexus Mods 文件 | [README](NexusUploader/README.md)、[任务入口](NexusUploader/AGENTS.md) |
| biliUploader | 通过固定版本 biliup 库创建和更新 Bilibili 稿件 | [README](biliUploader/README.md)、[任务入口](biliUploader/AGENTS.md) |

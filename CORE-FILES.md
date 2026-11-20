# 核心文件与架构

Claude UI 是 Windows x64 上的 C# / WPF 客户端。界面通过本机 Claude Code CLI 子进程执行模型请求；源码由 `Build.ps1` 调用 .NET Framework C# 编译器构建，不需要 npm 或 NuGet 安装步骤。

## 核心模块

| 文件 | 职责与关键关系 |
| --- | --- |
| `src/Program.cs` | STA 入口、单实例互斥、准备本地数据目录并启动 WPF 窗口。 |
| `src/MainWindow.xaml` | 主窗口布局、控件模板、主题与交互入口。 |
| `src/MainWindow.cs` | 对话发送、模型选择、执行状态、取消、CLI 工具页和模块协调，是主要业务入口。 |
| `src/ClaudeCli.cs` | 定位原生或 npm 安装的 CLI，构建请求参数、启动子进程、收集输出和停止执行。 |
| `src/ClaudeStream.cs` | 解析 CLI 的 stream-json，合并增量内容、处理请求 ID、用量、结束状态与错误。 |
| `src/CliToolPermission.cs`、`src/ReplyProgress.cs` | 工具授权往返、阶段展示、执行耗时与回复过程记录。 |
| `src/CliConversationCatalog.cs`、`src/CliSessionLocation.cs` | 发现本地 CLI 会话、读取历史链并找到接续所用的 CLI 配置目录。 |
| `src/CliHistoryMerge.cs`、`src/CliReplyGroups.cs` | 将 CLI 历史与客户端缓存有序合并，归并同一轮回复并保留客户端元数据。 |
| `src/SidebarNavigation.cs`、`src/SidebarResizer.cs` | 项目/对话导航、分组、归档及侧栏宽度。 |
| `src/AppData.cs`、`src/PortableData.cs` | JSON 状态、对话、主题与布局偏好；迁移旧目录到程序旁的 `Data`。 |
| `src/ModelProfile.cs`、`src/ModelProfilesDialog.cs`、`src/ModelProfileProbe.cs` | 模型配置、端点校验、Windows 当前用户凭据加密、子进程环境路由与连接测试。 |
| `src/PromptCachePolicy.cs` | CLI 缓存策略环境变量：沿用、5 分钟、1 小时或禁用。 |
| `src/ChatAttachment.cs`、`src/AttachmentPaste.cs` | 附件复制、大小限制、图片处理、剪贴板粘贴及消息负载。 |
| `src/MarkdownReply.cs`、`src/PipeTable.cs`、`src/CliOutputView.cs` | Markdown、表格和 CLI 输出的本地呈现，不调用模型。 |
| `src/RequestStatistics.cs`、`src/RequestPricing.cs` | 请求数据、JSONL 存取、网关来源、费用与计费规则匹配。 |
| `src/RequestStatisticsView.cs`、`src/RequestRatesDialog.cs` | 统计筛选、记录删除、计费配置和多币种展示。 |
| `src/RequestOverviewLayout.cs`、`src/RequestDetailLayout.cs`、`src/RequestDetailResizer.cs` | 统计概览、请求详情布局与尺寸偏好。 |
| `src/CliUpdate.cs` | CLI 版本解析、安装方式识别、更新命令生成与更新后的验证。 |
| `src/PromptShortcuts.cs`、`src/ProjectActionVisuals.cs` | 输入快捷键和项目菜单视觉辅助。 |
| `src/Assets/` | 构建所需的图标与头像资源，由编译脚本嵌入程序。 |

## 一轮请求的数据流

```text
用户输入 / 附件
  → MainWindow + RequestOptions + ModelProfile + PromptCachePolicy
  → CliRunner 启动 Claude Code CLI
  → stdout / stderr → ClaudeStream
  → 回复内容 + ReplyProgress + 工具授权
  → RequestPricing → StatisticsStore → 请求统计视图
  → DataStore 保存对话及界面状态
```

CLI 自己管理原始会话与认证；客户端的 `Data/state.json` 保存界面状态、聊天缓存和加密后的自定义模型凭据，`Data/request-statistics.jsonl` 保存请求记录，`Data/attachments/` 保存附件副本。凭据加密绑定 Windows 当前用户，因此整个 `Data/` 都应留在本机。

## 构建与验证

在 Windows x64 PowerShell 中，从仓库根目录执行：

```powershell
.\Build.ps1
.\Build.ps1 -Smoke -OutputDirectory .\artifacts\smoke
```

第一条生成根目录的 `ClaudeUI.exe`。Smoke 使用临时数据和模拟记录验证界面、历史合并、附件、计费、缓存与进程交互；最后还要求本机可发现 Claude Code CLI，以检查版本、健康诊断、登录状态和后台任务命令。它不是完全脱离 CLI 安装的单元测试。

`-Live` 额外调用真实模型并产生用量，仅在需要端到端验证时手动执行。`tools/Test-PromptCacheWire.py` 则使用 Python 标准库、本地回环模拟 API 和合成凭据，检查实际 CLI 的缓存协议；通过 `--cli` 指定本机 `claude.exe` 路径。

## 仓库收录范围

收录 `src/`、`tests/`、`tools/`、构建脚本和说明文档，以及构建必需的 `third_party/litellm/` 价格快照、来源版本与原始 MIT 许可。客户端代码的许可不由该第三方许可决定。

`.gitignore` 排除本地聊天与凭据、附件、编译产物、测试输出、日志和历史备份。详细设计见 [请求看板说明](REQUEST-OBSERVABILITY.md) 与 [输入 Token 优化评估](TOKEN-OPTIMIZATION.md)；其中价格和 CLI 兼容性结论属于文档记录时的快照。

# 输入 Token 优化评估与集成

评估日期：2026-11-20。本次验证 CLI：2.1.318；路径均相对于仓库根目录。

## 结论与边界

没有找到同时满足“轻量、易用、开源、实际减少任意会话的输入 token、上下文语义及信息完整、保证回答质量不下降、低侵入接入当前远程 API”的通用方案。不能把有损压缩描述成无损，也不能把缓存命中描述成上下文 token 减少。

本次集成最接近要求的 **CLI 原生前缀缓存控制与效果观测**。保持原始输入，由服务端复用已计算的相同前缀，降低重复 prefill 计算与按缓存费率收取的费用。它不缩短请求正文或上下文，不增加可容纳的历史长度，也不是新接入的开源压缩引擎；Anthropic 服务和 Claude Code CLI 本身并非 MIT/Apache 开源组件。新增本地 C# 适配无需任何新运行时或第三方包，源码可直接审查；仓库未提供整体开源许可证，不能擅自把整个项目宣称为开源。

Claude Code 本来就默认启用 prompt caching。因此旧配置和“本机默认”保持原行为，默认新增收益可能是 **0**。新增收益来自纠正进程继承的禁用开关、按端点关闭不兼容缓存、或在有较长间隔的会话中选择更合适的 TTL。1 小时写入更贵，不应默认开启。本文不宣称已实测真实端点节省比例，也不声称有限测试能够证明任意模型回答完全一致。

## 项目结构与调用链

项目没有 npm/Python 后端或独立 HTTP 模型客户端；`Build.ps1` 调用 Windows .NET Framework 4.x 的 `csc.exe` 编译顶层 `src/*.cs`，形成 Windows x64 WPF 单可执行文件。

| 层 | 文件及作用 |
| --- | --- |
| 界面与编排 | `MainWindow.xaml`、`MainWindow.cs`：输入、模型选择、发送、停止、工具授权、显示回答 |
| 状态与历史 | `AppData.cs`、`PortableData.cs`：GUI 状态；`CliConversationCatalog.cs`、`CliSessionLocation.cs`：发现与读取 CLI 历史、定位会话配置目录 |
| 附件 | `ChatAttachment.cs`：图片成为原生图片块，其他附件通过本地副本路径供 CLI 工具读取 |
| 模型路由 | `ModelProfile.cs`、`ModelProfilesDialog.cs`：端点、模型 ID、凭据加密、子进程环境及新增缓存策略 |
| CLI 执行 | `ClaudeCli.cs`：`RequestOptions.Arguments()`、`CliRunner.RunAsync()`、stdin/stdout、进程树停止 |
| 流与授权 | `CliToolPermission.cs`：stream-json 输入及授权；`ClaudeStream.cs`：解析文本、工具事件、错误和用量 |
| 可观测性 | `RequestStatistics.cs`、`RequestPricing.cs`、`RequestStatisticsView.cs`：按请求采集、缓存拆分、费率与明细 |
| 验证 | `tests/Smoke.cs`、`tools/Test-PromptCacheWire.py` |

```text
MainWindow.Send
  → ActiveModel.CliEnvironment（本次在这里应用缓存策略）
  → AttachmentFiles.Prompt / Content
  → RequestOptions.Arguments（保留 --resume 指定会话）
  → MainWindow.Run（保留会话 CLAUDE_CONFIG_DIR）
  → CliRunner.RunAsync（仅子进程环境，UTF-8 stdin）
  → 本机 claude --print --output-format stream-json
  → CLI 装载历史、系统提示词、工具定义和 MCP，上下文由 CLI 管理
  → Anthropic 兼容端点 → 模型/服务端缓存
  → CLI stdout → ClaudeStream → 回答视图与 RequestStatisticsView
```

连接测试走 `ModelProfileProbe.RunAsync → profile.CliEnvironment → CliRunner`，因此自动复用同一策略。手动 `/compact` 原有入口保留，未被当成节省方案调用；已有 CLI 自动压缩策略仍由 CLI 管理。本改动不对历史执行摘要、删词、重排、去重、截断或检索替换，也不安装 hook、不修改 CLI 配置和原始 JSONL。

## 候选对比

下面的数值来自各项目自身公开说明/基准，不是本项目实测；工具输出、结构化片段和总会话的节省不能混用。维护状态基于此次可访问的仓库/提交页面，索引可能滞后，不把页面抓取日当作最近发布日期。

| 方案 | 预期效果及场景 | 许可证与维护证据 | 接入成本 | 主要风险与决定 |
| --- | --- | --- | --- | --- |
| [LLMLingua / LLMLingua-2](https://github.com/microsoft/LLMLingua) | 项目宣传最高 20 倍压缩；适合容许信息损失的长文、RAG、示例压缩，不能迁移成所有聊天节省 95% | MIT；微软研究项目，含 2025 SecurityLingua；可检索的提交页最近条目为 2025-10-28，维护节奏偏研究型，无法确认更晚实时提交 | 高：Python、PyTorch/Transformers、模型权重；当前 UI 无法拦截 CLI 内部全部请求 | 删词是有损操作；数字、否定、代码、边界条件可能丢失；额外模型占内存和增加延迟。排除 |
| [RTK](https://github.com/rtk-ai/rtk) | 常见开发命令输出声称减少 60–90%；单个 Rust CLI，适合容许过滤的终端日志 | 当前 CLI Apache-2.0；engine 和 proxy 为 BUSL-1.1（source-available，不能统称全部开源）；公开元数据 pushed_at 为 2026-11-19，迭代活跃 | 中：二进制、hook/命令代理、Windows 兼容、工具权限与过滤规则 | 过滤、聚合、截断会改变模型实际看到的输出；原始日志落盘仍不等于模型得到全部信息。严格要求下排除 |
| [TOON](https://github.com/toon-format/toon) | 均匀对象数组/表格更适合；README 混合数据基准比格式化 JSON 少 32.7%，却比 compact JSON 多 1.6%；均匀员工表比 compact JSON 少 36.8% | MIT；TS SDK/规范/基准持续维护；可访问元数据 pushed_at 为 2026-11-01，未归档 | 中到高：限定结构化数据、引入编码器/MCP 适配及模型理解验证；不能转换 API 协议本身 | JSON 数据值可无损往返不等于文本语义、字节含义或模型理解完全不变；任意历史、源码、图片不适用。当前链路不采用 |
| [LMCache](https://github.com/LMCache/LMCache) 精确 KV 复用 | 服务端复用 KV，降低重复 prefill、TTFT；逻辑输入长度仍不变，无统一 token 减少率 | Apache-2.0；可访问 dev 提交页有 2026-09 活动，集成 vLLM 等服务框架 | 很高：需掌控 GPU 推理服务与引擎，当前远程 API 客户端无权接管 KV | 服务端部署和缓存隔离成本；应只用精确复用，不能把近似混合、量化等路径视作质量无损。当前排除 |
| CLI 原生 prompt caching（本次采用） | 相同前缀、多轮工具调用、会话接续；上下文长度减少 0%，缓存部分费用按端点优惠，原来缓存正常时新增收益可为 0 | Anthropic 专有服务/CLI；本机 2.1.318，官方文档明确默认缓存和 TTL 控制 | 低：本地 C# 策略、模型配置下拉框及统计说明，无额外包、代理或端口 | 端点须支持；首写溢价、TTL、短前缀、模型/工具变化导致 miss；不满足“新增开源压缩组件”条件，但最符合本项目的信息完整性与低侵入要求 |

没有采用语义答案缓存/GPTCache：相似问题不保证相同有效上下文；即使问题一致，工作目录内容、工具状态、权限和时间也可能不同。直接返回旧答案会跳过模型与工具执行，不能保证答案质量。gzip/Brotli/Base64 只改变传输或编码，不能让远程模型的 tokenizer 少读取原本的有效上下文。

## 节省口径

Anthropic 使用的总逻辑输入为 `T = input_tokens + cache_creation_input_tokens + cache_read_input_tokens`。不能只展示未缓存 `input_tokens` 然后声称总输入大幅减少。

以常见的读缓存 0.1 倍、5 分钟写缓存 1.25 倍、1 小时写缓存 2 倍输入价举例（部分新模型有不同读缓存倍率；第三方网关也可能自行定价）：

- 5 分钟成本约为 `P × (U + 1.25W + 0.1R)`。
- 1 小时成本约为 `P × (U + 2W + 0.1R)`。
- 相对全部按未缓存价计费，净节省分别为 `P × (0.9R − 0.25W)` 和 `P × (0.9R − W)`；不是固定节省 90%。
- 同一 100k 前缀重复 5 次，假设首次写、之后 4 次全部命中、无新增内容：基线 500k 输入价单位；5m 为 165k（少 67%），1h 为 240k（少 52%）。这是理论示例，仍是 500k 总逻辑输入，不包含输出费、网关加价或其它优惠。

新增明细只显示 `R / (U+W+R)` 命中占比，不声称它就是费用节省率。缺失字段、部分流、无模型响应、零输入均不伪造百分比。不重算历史费用，也不修改历史记录。

## 改动与使用

1. `src/PromptCachePolicy.cs`：纯环境适配函数；沿用模式完全保留输入环境，显式模式复制字典后设置缓存变量。对主会话和子请求使用相同 TTL，5m/1h 会解除已知的进程继承禁用开关。未知策略报错。
2. `src/ModelProfile.cs`：新增可选 `PromptCaching` 字段，在既有 `CliEnvironment()` 应用策略；未填写字段的旧配置保持原行为。认证字段、模型路由和配置目录处理不变。
3. `src/ModelProfilesDialog.cs`：新增上下文缓存选择与费用/版本提示；保存、编辑、测试连接共用模型配置。
4. `src/RequestStatisticsView.cs`：请求详情追加命中占比与口径说明；只读已有统计字段。
5. `tests/Smoke.cs`：策略、旧配置、序列化、配置 UI、真实子进程环境和逐字 stdin 测试。
6. `tools/Test-PromptCacheWire.py`：本地 Messages API 模拟端点，验证已安装 CLI 的实际请求。

打开“模型配置 → 上下文缓存”。普通连续对话先沿用 CLI；需要明确启用的兼容端点可选 5 分钟；经常在 5–60 分钟后继续的长对话可评估 1 小时；不兼容端点可选禁用。没有自定义 API 地址的已保存模型也可设置。全局“本机默认”继续直接沿用 CLI。修改从下一轮生效，不会迁移或重写已有会话；选择“沿用 CLI”即可撤销本地覆盖。

TTL 环境变量要求 CLI 2.1.242+。旧版可能忽略 TTL 而保留默认值；本机验证版本是 2.1.318。组织托管配置、网关策略可能覆盖本地选项，必须以实际 usage 和写缓存 TTL 统计确认。没有通过删掉缓存字段后自动重试来兼容失败端点，以免重复计费、重复工具操作或隐藏故障。

## 验证与实际收益验收

已执行：

```powershell
.\Build.ps1 -Smoke -OutputDirectory .\artifacts\prompt-cache
python .\tools\Test-PromptCacheWire.py --cli "C:\path\to\claude.exe"
```

本地 smoke 全部通过，包含新增策略/UI、旧版状态兼容、历史读取、附件、权限、请求统计、计费、显示和 CLI 只读命令。新增子进程测试确认中英文、JSON 字面量、空格、换行原样传递，路由/凭据保留，父进程环境不变。

真实 CLI + 本地模拟端点测试通过：禁用模式 0 个缓存标记；5m 和 1h 模式各 3 个，TTL 正确。去掉 `cache_control` 后，三组请求的 `model/system/messages/tools/tool_choice/thinking/output_config/max_tokens` 完全相同。使用临时配置目录、合成输入及假凭据，不读取真实聊天或向真实模型发送消息。模拟端点仅验证协议与输入不变，不能验证真实服务的命中、计费、速度或回答质量。

真实收益验收应使用单独测试会话和实际目标端点：保持模型/目录/工具配置稳定，准备达到该模型缓存门槛的合成前缀，在 TTL 内接续；从“请求统计”核对 U/W/R 和网关账单，并对比关闭缓存的独立基线。短连接测试通常不足缓存门槛，不能据此判定缓存不可用。不要拿用户既有会话做压缩实验。若已有缓存正常命中，应把本次收益记为更清晰的策略控制与可验证性，不能把既有收益算作新收益。

## 一手资料

- [Claude Code：缓存机制、会话接续、TTL 与禁用开关](https://code.claude.com/docs/en/prompt-caching)
- [Anthropic：缓存支持、定价、门槛与 usage 口径](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)
- [LLMLingua 源码与 MIT 许可](https://github.com/microsoft/LLMLingua)、[提交记录](https://github.com/microsoft/LLMLingua/commits/main/)
- [RTK 源码、过滤效果及 Apache/BUSL 边界](https://github.com/rtk-ai/rtk)、[仓库元数据](https://api.github.com/repos/rtk-ai/rtk)
- [TOON 源码、MIT 许可与基准](https://github.com/toon-format/toon)、[仓库元数据](https://api.github.com/repos/toon-format/toon)
- [LMCache 源码、集成与 Apache-2.0 许可](https://github.com/LMCache/LMCache)、[提交记录](https://github.com/LMCache/LMCache/commits/dev/)

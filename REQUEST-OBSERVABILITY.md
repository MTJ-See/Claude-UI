# 请求用量看板：架构与选型

## 项目与依赖

本文路径均相对于仓库根目录。

项目为 Windows x64 的 C# / WPF 桌面应用，使用系统 .NET Framework 4.x 的 `csc.exe` 直接编译。没有 npm、NuGet、Python 服务、Web 后端或数据库服务。框架依赖为 WPF、System.Xaml、System.Web.Extensions（JSON）、Windows.Forms（目录选择）和 System.Security（本机凭据加密）。模型调用依赖本机 Claude Code CLI；应用不直接持有 Anthropic HTTP 调用链。

```text
MainWindow.Send / 手动压缩
  → RequestOptions + ModelProfile.CliEnvironment
  → Run → CLI 配置目录解析、GatewayRoute、历史费用基线
  → CliRunner.RunAsync → claude.exe 子进程 → Anthropic 兼容端点
  → stdout stream-json（采集接收时刻）→ 队列 → ClaudeStream
  → api_message_id / message.id 去重、parent_tool_use_id 分流
  → message_start / message_delta / message_stop / assistant / result
  → Finish / 取消 / 关闭 → Statistics → RequestPricing.Apply
  → StatisticsStore.Append → 本地 JSONL → RequestStatisticsView

模型配置“测试连接” → ModelProfileProbe → 同一个 CliRunner / ClaudeStream
  → finally 回传统计 → UI Dispatcher → 同一个 Record / 存储 / 看板
```

版本、健康检查、登录检查、重命名和创建分支是 CLI 管理操作，不伪造模型请求。工具执行产生的后续模型响应通过独立消息 ID 分开记录。CLI 不公开的内部重试、隐藏压缩或后台请求无法凭空还原。

## 调研与选择（2026-11-20）

| 方案 | 优点 | 本项目取舍 |
| --- | --- | --- |
| [ccusage](https://github.com/ccusage/ccusage)、[发布记录](https://github.com/ccusage/ccusage/releases) | MIT、本地 Claude JSONL 分析、持续维护，适合历史用量报表 | 另增 Node/Bun 子进程；其报表不能直接补齐本应用的取消、进程失败、连接测试和请求事件耗时。保留为外部核对工具，本次不依赖 |
| [LiteLLM 价格目录](https://github.com/BerriAI/litellm/blob/main/model_prices_and_context_window.json) | MIT 数据、维护活跃、精确模型 ID、含缓存及长上下文单价，可离线嵌入 | **选择仅集成价格目录，由现有 WPF 与 JSONL 实现采集和看板**。不安装 SDK、网关或数据库；不是声称部署了完整 LiteLLM 可观测平台 |
| [Langfuse 自托管](https://langfuse.com/self-hosting/deployment/infrastructure/containers) | 完整追踪、评估、协作和多项目平台 | Web/Worker、PostgreSQL、ClickHouse、Redis、对象存储等超出本地单人桌面需求；不引入 |

选中目录的实际提交为 `65d8137acf16d46dd56656e34ccca1e3e73739ee`，上游价格提交时间 `2026-11-20T04:26:27Z`。`third_party/litellm/prices.json` 保留 24 个 Anthropic chat 模型的数值价格字段，约 12 KB。MIT 许可证和价格数据均嵌入 EXE；运行时零新增软件包、零价格联网请求。源信息保存在 `source.json`。

## 数据结构与接口

`程序所在目录的 Data\request-statistics.jsonl` 延用原文件，每行是一次 CLI 执行产生的 `RequestStat[]`，追加写入。旧字段仍兼容；破损行被跳过，缺失的新字段显示未知，不补写推算值。保存失败在看板提示，当前窗口仍保留数据。进程突然崩溃前尚未完成的执行不保证落盘；这是轻量批次落盘的取舍。历史全量加载并在内存筛选，适合桌面本地规模，不适合集中式海量日志。

新增 schema 2 字段：

| 字段 | 含义 |
| --- | --- |
| RequestId / RunId / SessionId / Operation | API 消息标识（不可用时 local 前缀）、本轮 GUID、会话、对话/压缩/连接测试 |
| Model / Gateway / GatewaySource | 响应模型及已去除用户信息、查询参数的配置端点；CLI 未报告实际 URL 时标明为推断 |
| StartedUtc / CompletedUtc / DurationMs / TimingSource | UTC ISO 8601 时间、毫秒耗时及测量口径 |
| RunStartedUtc / RunCompletedUtc / RunDurationMs | CLI 执行整体时间与耗时，同一 RunId 重复携带，不应逐行累加 |
| Input / CacheWrite / CacheWrite1Hour / CacheRead / Output | 原始非缓存输入、缓存写入及 1 小时写入、缓存读取、输出；输入合计包含三类输入 |
| Status / EndReason / Incomplete / ExecutionOnly | 成功、失败、已取消、不完整；CLI 失败而无模型响应另记执行事件，不捏造 token |
| BilledAmount / BilledCurrency / BillingField | CLI 转发的响应实际金额及字段来源；未返回显示“未返回”，无币种金额不混合汇总 |
| EstimatedInputCost / EstimatedCacheCost / EstimatedOutputCost / EstimatedCost / EstimateCurrency / PriceSource | 采集时的参考费用分项、合计、币种及价格版本 |
| CliEstimate / RunEstimate / CallsInRun | 原有 CLI 估算；多次调用仅保留本轮合计，不平均摊到每个请求 |

这是进程内接口，不增加 HTTP 服务或监听端口：

- `ClaudeStream.Read(line, receivedUtc)`：在 stdout 接收时记录 UTC 时间，UI 队列延迟不计入观测时间。
- `ClaudeStream.Statistics(...)`：生成请求批次，包括终止原因、操作和关联 ID。
- `RequestPricing.Apply(row, rates)`：先精确匹配指定网关+模型，再匹配网关为空的模型规则；响应模型无匹配规则时，再按 `RequestedModel`（本轮传给 CLI 的模型标识）采用相同网关优先级匹配；仍无配置时才精确匹配内嵌响应模型价格。请求与响应模型分别保存，详情在两者不同时显示请求模型。停用、重复、非法配置或不完整数据均记录明确状态，不使用其他价格。已有金额或计费状态的快照不会重算。
- `StatisticsStore.Read/Append`：向后兼容的持久化接口。
- `StatisticsStore.Delete(selected, all)`：删除选中记录或全部统计。先写同目录临时文件，再原子替换；持久化失败时保留原文件和界面数据。选中删除按完整记录匹配，兼容旧记录并保留磁盘中其他请求；清空全部不受筛选限制。两种操作均需在界面确认，提供每行勾选框、全选当前结果、取消选择及已选条数，也支持 Ctrl / Shift 多选；刷新保留仍可见的选中项，不默认选中待删除记录。不删除聊天、回复中的请求 ID 或 CLI 会话。
- `StatisticsFilter.Apply`：请求 ID 或执行 ID 子串、精确模型、状态、当前会话及本地日期闭区间组合查询。
- `RequestStatisticsView.Record/Show`：记录、持久化、更新筛选项和看板。连接测试通过回调进入同一接口。

## 费用与耗时口径

按 Token 的配置费用 =（非缓存输入×输入单价 + 缓存写入×写入单价 + 缓存读取×读取单价 + 输出×输出单价）÷计费单位；单位支持 1、1,000、1,000,000 Token。按次计费为每个完整模型响应收取一次配置单价，不依赖 Token 用量；不完整响应和仅有执行错误的记录不计算。LiteLLM 原始单位为每 Token。默认缓存写入按标准 5 分钟价格，有 1 小时 usage 明细时使用对应价格；存在目录中的 `above_100k_tokens` 阶梯时按输入合计选取。免费项保留 0。未知 ID、缺少必要单价或不完整响应不估算。

公开目录仅提供标准 API 参考价，不代表订阅实际扣费、网关加价、批处理折扣、区域加价、优先级服务、搜索工具等附加费用。通过“请求统计 → 模型计费标准”维护任意模型的规则，无需先产生请求。配置保存在原 `state.json` 的 `RequestRates`，旧费率自动兼容为启用、按每百万 Token 计费。每轮 CLI 执行（包括连接测试）启动时保留一份规则列表，执行期间编辑从下一轮生效。金额、币种、所用单价及高峰/空闲信息保存为历史快照，详情不再使用当前规则重算旧请求。实际扣费、参考估算按币种分别汇总，不做汇率换算，不把 CLI 的 `total_cost_usd` 当作网关账单。

单次 HTTP 请求的发送时间被 CLI 封装，程序无法直接取得。**有事件的请求耗时为首个响应事件到 message_stop 的观测时长，不含首字等待**；没有流式事件的 assistant 快照耗时显示未知。另记 CLI 启动时间到结束的执行总耗时，包含首字等待、工具与授权等待。页面明确展示测量口径。未收到 API 请求 ID 的失败/取消使用本地执行标识。并发子调用通过 parent ID 分开，重复 assistant 事件不会重复计费，result 汇总不会覆盖已收到的逐请求 token。

## 使用与更新

重新启动新编译的 `ClaudeUI.exe`，打开左侧“请求统计”。可组合填写请求/执行 ID、选择模型、开始日期、结束日期和状态，也可仅看当前会话；“重置”清除全部筛选。选择一行查看缓存细分、费用来源与执行关联。

构建：`./Build.ps1 -Smoke`。EXE 正在使用时可指定 `-OutputDirectory artifacts/usage-dashboard`。

价格更新需显式执行 `./tools/Update-LiteLlmPrices.ps1 -Revision <40位上游提交SHA>`，审核差异后重新运行构建与 smoke。脚本只请求 LiteLLM GitHub 公开文件，不发送本地请求数据。固定 SHA 避免随上游变更悄悄重算历史费用。

## 验证

`Build.ps1 -Smoke` 验证实际 WPF 控件加载与筛选交互，覆盖 token/cache 统计、stream 与 assistant 去重、精确事件耗时、费率优先级和历史快照稳定性、未知模型与不完整数据、日期边界、请求/执行关联、JSONL 新旧结构及损坏尾行、失败/取消和主题布局。`-Live` 另使用现有 CLI 配置发送短消息，验证真实 token、状态、耗时及持久化，再验证会话分支与接续。看板渲染预览可通过环境变量 `CLAUDE_UI_STATS_PREVIEW` 指定图片前缀。


## 模型计费配置

规则字段包括：`Model`（区分大小写的精确模型标识，优先响应模型、其次本轮请求模型）、`Gateway`（空值为全部网关，否则精确匹配请求明细中的网关）、`Enabled`、`BillingMode`（Token / Request）、`Unit`、`Currency`（USD / CNY）、`Input`、`CacheWrite`、`CacheRead`、`Output` 和 `PerRequest`。模型无需在程序中注册，新模型直接新增规则；一个模型与网关范围只允许一条规则，启停通过编辑原规则保存。

可选 `PeakEnabled`、`PeakInput`、`PeakCacheWrite`、`PeakCacheRead`、`PeakOutput`、`PeakPerRequest` 和 `Holidays`。按请求首个响应事件的 `StartedUtc` 转北京时间选择时段（CLI 不暴露实际 HTTP 发送时刻）：周一至周五 09:00（含）至 12:00（不含）、14:00（含）至 18:00（不含）为高峰，其余时刻为空闲。周末及 `Holidays` 列出的日期全天为空闲；节假日由用户维护，不联网推断，也不将周末调休算作高峰。

附件价格可配置为 CNY / 每百万 Token：空闲未缓存输入 1、缓存命中输入 0.02、输出 4；高峰对应 2、0.04、8。缓存写入价格未在附件给出，应按实际服务规则填写（免费项填 0），不自动假设模型名称或写入价格。

单价校验为 0–1,000,000；模型标识不能为空、含空白或超过 256 字符；单位、币种及日期严格校验。保存通过现有 state.json 临时文件替换机制落盘，失败回滚内存规则。读取到冲突、停用、无效配置时保留状态，不回退成美元参考价。`PricingStatus` 随统计存储，配置费用、公开参考估算与网关实际扣费分别标识；不同币种不相加、不转换。用户费率对缓存写入使用一个配置单价，公开参考价仍保留原有的 1 小时缓存和长上下文阶梯逻辑。

`Build.ps1 -Smoke` 新增覆盖：旧规则默认值、任意模型配置、网关优先级、禁用与冲突、无效币种/日期/单价/单位、三种 Token 单位、按次与免费计费、北京时间所有峰谷边界、周末与节假日、历史快照、执行中编辑、配置落盘与失败回滚、空统计页配置入口、真实 WPF 表单新增/编辑/启停和混合币种汇总。可用 `CLAUDE_UI_BILLING_PREVIEW` 指定配置窗口浅色/深色渲染预览的文件前缀。

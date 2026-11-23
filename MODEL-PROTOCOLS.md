# 模型接口协议适配

模型配置支持「自动识别」「Anthropic Messages」「OpenAI Chat Completions」「OpenAI Responses」「Gemini generateContent」。

可直接填入完整地址：

| 地址 | 自动识别结果 |
| --- | --- |
| https://aitokendance.com/gateway/v1beta/models/gemini-4-hydrogen:generateContent | Gemini |
| https://aitokendance.com/gateway/v1/chat/completions | Chat Completions |
| https://aitokendance.com/gateway/v1/responses | Responses |

填写显示名称、模型 ID、接口地址、认证和凭据后，可使用「测试连接」。Gemini URL 中的模型段会按表单中的模型 ID 更新。只填 https://aitokendance.com/gateway 等基础地址时，应手动选择协议；自动识别对没有接口路径的地址使用 Anthropic。

OpenAI 通常选择 Bearer Token。Gemini 官方 API Key 选择 API Key（发送 x-goog-api-key）；第三方网关按其认证要求选择。凭据仍由 Windows 当前账户加密保存。不支持通过 URL 查询参数传密钥。

已有未声明协议的配置沿用原来的 Anthropic 路由（包含旧 TokenDance 地址修正规则），避免升级后静默切换协议。需要切换协议的旧配置可在界面中显式选择并保存。

## 实现

ModelProfile.ApiProtocol 持久化协议。ModelProtocol 负责地址解析、请求与响应转换；ModelProtocolBridge 在每次 CliRunner 执行期间创建独立的 IPv4 回环监听，结束或取消时关闭。原有 Anthropic 配置直接使用 Claude CLI。

转换包括系统提示、对话历史、本地图片、工具定义、工具调用和结果、停止原因、输入/输出及缓存 token 用量；Gemini 函数调用的 thoughtSignature 随工具 ID 编码保存并在后续调用时恢复。CLI 只获得本地随机令牌，上游密钥保留在主进程；不跟随上游重定向。请求统计仍使用用户配置的上游地址。

转换协议当前向上游发送非流式请求，收到完整结果后转为 Anthropic JSON/SSE 返回 CLI，因此回复会在完整生成后显示。该 SSE 是兼容封装，并非上游逐字流式传输。未知的消息内容类型、服务端专用工具会明确报错；本地 token 计数接口未实现，不会将估算当作实际用量。其他厂商专用参数、跨协议的原生推理上下文和服务端会话状态不作无损迁移保证。

新增协议时在 ModelProtocol.Values / Labels 注册，并补充 Resolve、Endpoint、Request、Response 的转换及协议测试。

## 验证

运行 tools/Test-ModelProtocols.ps1：使用本地模拟上游与真实子进程，验证地址检测与拼接、配置序列化、文本/图片/工具转换、Gemini 签名回放、缓存用量、应用 SSE 解析、Bearer/API Key、CLI 凭据隔离、429 错误与取消监听。

运行 Build.ps1 -Smoke：验证现有应用回归测试。以上测试不连接用户的第三方网关；实际模型可用性、权限与额度请使用界面的「测试连接」验证。

协议参考：
- [OpenAI Chat](https://developers.openai.com/api/reference/resources/chat)
- [OpenAI Responses](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [Gemini generateContent](https://aistudio.google.com/api-reference/generate-content)

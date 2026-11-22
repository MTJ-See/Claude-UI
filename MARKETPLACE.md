# MCP / Skill 市场

左侧「MCP 市场」「Skill 市场」实时浏览魔搭内容。支持搜索、分页、刷新、失败重试、图标和深浅主题。详情使用摘要卡片、标签、Markdown 功能介绍、逐项参数和权限说明；官方原始安装配置折叠显示。

## 安装

详情页选择「安装到 Claude」，在确认窗口填写名称、作用域与必填参数，确认权限后安装。可查看阶段日志、下载量和脱敏后的 CLI 输出，并停止操作。同名 MCP / Skill 会阻止安装，不自动覆盖。

- MCP：从魔搭官方 `server_config` 解析安装选项，使用 `claude mcp add-json ... --scope user|project` 注册。`streamable_http` 映射到 Claude 的 `http`。Windows 的 npx 启动转换为 Node.js + npm 自带 npx-cli.js，避免 cmd 参数转义问题。检查所需启动程序是否存在；npx / uvx 包通常由启动器首次运行时按需获取，注册成功不等同于已连接。
- 托管 MCP：用户在安装窗口明确选择「部署到魔搭并连接」后，使用官方 deploy API 创建 HTTP 服务，再注册到 Claude。需要魔搭 Token；API Key 会按官方接口提交。云部署与本地注册不能组成原子事务，本地失败或取消后应在魔搭控制台确认云端状态，不自动销毁云服务。
- Skill：使用魔搭官方 install.sh 相同的 `/skills/{owner}/{name}/archive/zip/master` 发布包下载协议，无需在 Windows 执行 Bash 脚本。安全解压、验证根目录 SKILL.md，在同一磁盘暂存后移动到最终目录；不执行包内脚本。限制下载 50MB、解压 150MB、3000 个条目，拒绝路径越界与符号链接。
- 用户级 Skill 写入当前 Claude 配置目录下的 skills（默认 `~/.claude/skills`）；项目级写入当前项目 `.claude/skills`。安装过程不修改已有扩展。已安装 Skill 目录内仅保存最小来源记录 `.claudeui-install.json`，用于后续管理。

移除了市场工具栏的「访问令牌（可选）」按钮。Token 仍可通过 `MODELSCOPE_API_TOKEN` / `MODELSCOPE_API_KEY` 注入；下载私有 Skill 或部署托管 MCP 时，可在安装窗口按需填写本次 Token。MCP 参数中的密钥写入 Claude 原生配置，项目配置可能被提交到版本控制，应按安装窗口提示处理。

## 数据与接口

市场数据只驻留内存：45 秒、最多 64 个响应；每次进入市场和手动刷新绕过缓存。不写本地市场清单，不设置定时同步。认证身份隔离缓存，公开内容可匿名访问。图标独立匿名下载并在内存解码，不使用 URI 磁盘缓存。

`MarketplaceView` → `IMarketplaceProvider` → `ModelScopeMarketplaceProvider` → `MarketplaceHttp`；安装由 `MarketplaceInstaller` 执行。

MCP：`PUT /openapi/v1/mcp/servers`、`GET /openapi/v1/mcp/servers/{id}`。MCP API 仅允许访问前 100 条，更多条目使用搜索。Skill：`GET /openapi/v1/skills`、`GET /openapi/v1/skills/{id}`。

## 验证

```powershell
.\tools\Test-Marketplace.ps1
.\tools\Test-Marketplace.ps1 -Live
.\Build.ps1 -Smoke -OutputDirectory .\artifacts\marketplace
```

独立验证覆盖缓存、数据映射、冲突、解压路径校验、参数替换、日志脱敏和模拟 CLI 安装。生成浅色/深色布局与详情 PNG 到 artifacts/marketplace-tests。`-Live` 额外匿名读取魔搭列表和详情，并将一个公开 Skill 下载安装到隔离临时目录后清理；不调用模型、不执行下载的脚本、不部署云服务。

参考：[Claude MCP](https://code.claude.com/docs/en/mcp)、[Claude Skills](https://code.claude.com/docs/en/skills)、[魔搭官方安装脚本](https://modelscope.cn/skills/install.sh)。

## 已安装与开关

市场工具栏移除「返回列表」，新增「已安装」。打开后扫描当前 Claude 配置目录和项目，MCP 市场仅显示已安装 MCP，Skill 市场仅显示已安装 Skill；显示来源、安装路径和启用状态，不再提供重复的分类切换。

- MCP：扫描用户级、项目共享、项目私有、组织提供和已安装插件的标准配置。开关按官方 `/mcp` 机制写入当前项目的 `disabledMcpServers`，保留服务配置和认证参数。
- Skill：扫描用户、当前/父级项目、组织目录及已安装插件的标准 skills 目录，从 SKILL.md 读取名称。开关使用当前项目 `.claude/settings.local.json` 中的 `skillOverrides`（`on` / `off`），不删除或改名 Skill 文件。
- 开关采用青绿色圆角滑块。开关作用于当前工作目录所代表的项目，同名条目共同生效；新建或重启 Claude 会话后生效，开启不等同于连接健康或通过所有权限审批。
- 插件 Skill 不受 skillOverrides 控制，显示只读开关和 `/plugin` 管理提示。显式组织可见性策略和 Skill 权限规则会显示限制原因。托管和审批规则仍由 Claude 执行。
- 本地扫描不访问远程市场，不启动 MCP 服务。读取异常会提示；配置写入采用临时文件替换，保留其他字段并检测并发修改。不会覆盖格式异常的配置。

独立检查额外覆盖多来源扫描、MCP / Skill 开关持久化、原配置保留、插件来源、受限状态及真实扫描页面的深浅主题布局。所有启停测试使用隔离临时目录，不修改用户配置。

后续管理能力：市场列表已安装徽标、卸载和更新尚未接入。

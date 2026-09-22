# GptPlus Manager

本地优先的 ChatGPT / Codex 多账号管理工具。Windows 桌面应用，使用 **WPF + .NET 10**。

## 功能

- **账号管理**：添加、编辑、删除、搜索，支持备注与购买时间。
- **一键复制**：单击邮箱 / 密码 / 2FA 密钥 / 动态验证码即可复制到剪贴板。
- **2FA 验证码**：内置 RFC 6238 TOTP 生成，卡片实时显示验证码与 30 秒倒计时。
- **官方授权登录**：使用与 Codex CLI 相同的 OAuth PKCE 流程，在系统浏览器中登录；授权窗口可取消、可重新打开、可复制登录链接。
- **用量查询**：读取 5 小时与每周窗口的已用百分比、剩余百分比、实时重置倒计时。
- **订阅与重置卡**：显示套餐、订阅到期时间，以及官方发放的重置卡数量，并可直接兑换。
- **一键查用量**：批量刷新所有已授权账号。
- **定时保活**：按配置间隔静默刷新用量，保持令牌有效。
- **Codex 切换**：把指定账号写入 Codex 的 `auth.json`，一键切换当前账号。
- **重启客户端**：重启 Windows 上的 ChatGPT / Codex 客户端。
- **无效账号**：标记无效并自动沉底，批量操作自动跳过。
- **拖拽排序**：拖动卡片即可调整顺序，所见即所得地实时让位。
- **日间 / 夜间主题**：一键切换，选择自动保存。
- **第三方模型**：接入兼容 Responses API 的自建/中转模型。「官方账号 / 第三方」两个独立页面，路由模式互斥切换，密钥 DPAPI 加密。

## 技术栈

| 组件 | 说明 |
| --- | --- |
| 运行时 | .NET 10 (LTS) |
| 界面 | WPF，纯原生控件与矢量图标，无第三方 UI 框架 |
| MVVM | CommunityToolkit.Mvvm |
| 存储 | System.Text.Json，原子写入（临时文件 + 校验 + File.Replace） |
| TOML | Tomlyn（仅用于写入前校验，编辑仍是行级手术式） |
| 令牌保护 | Windows DPAPI（CurrentUser 作用域） |
| 测试 | xUnit |

## 项目结构

```
src/GptPlusManager.Core        业务逻辑：模型、TOTP、OAuth、用量、存储、Codex、进程控制
src/GptPlusManager.Wpf         WPF 界面、ViewModel、主题与交互
tests/GptPlusManager.Core.Tests 协议、兼容性与排序逻辑测试
```

## 构建

```powershell
dotnet build GptPlusManager.slnx -c Release
dotnet test  GptPlusManager.slnx -c Release
```

发布为自包含单文件（目标机无需安装 .NET）：

```powershell
dotnet publish src/GptPlusManager.Wpf/GptPlusManager.Wpf.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false `
  -p:DebugType=None -p:DebugSymbols=false -o publish
```

## 数据存放位置

默认位于 `%USERPROFILE%\Documents\gptplus`：

| 文件 | 内容 |
| --- | --- |
| `accounts.json` | 账号、密码、2FA 密钥、用量缓存、排序 |
| `tokens/*.token` | DPAPI 加密的 OAuth 令牌 |
| `settings.json` | 保活设置 |
| `wpf-preferences.json` | 主题偏好 |
| `providers.json` | 第三方模型供应商定义（不含密钥） |
| `provider-secrets.json` | DPAPI 加密的第三方 API Key |
| `exports/` | JSON / 文本导出 |

> **这些文件包含明文密码与 2FA 密钥，已被 `.gitignore` 排除，切勿提交或分享。**

## 第三方模型

Codex 官方只让内置模型可用。本工具可以让你把**兼容 Responses API** 的第三方模型接进 Codex 的模型选择器。

界面分两个独立页面（左上角切换）：**官方账号** 与 **第三方**。两边互不影响 —— 切到第三方页面配置供应商，官方账号页的登录态、账号列表、用量查询照旧。

**两种路由模式互斥**（第三方页顶部的单选）：

| | 官方模式 | 第三方模式 |
| --- | --- | --- |
| 模型列表 | 内置官方目录 | 只显示你配置的第三方模型 |
| 登录态 | 使用 `auth.json` | **不动 `auth.json`**，切回来无需重新登录 |
| `config.toml` | 保持你原本的内容 | 写入 `model` / `model_provider` / `model_catalog_json` |

> 第三方模式下官方模型**不显示也不可用**——这是 Codex 本身的限制（它没有按模型选择供应商的机制）。如果你的中转站也提供官方模型名，把它们加进供应商的模型列表即可照常使用。

**密钥注入两种方式**，供应商详情里可选：

- **命令式（默认，推荐）**：Codex 需要 token 时调用本程序 `--provider-token <id>`，密钥以 DPAPI 加密存储，`config.toml` 里**没有明文**。
- **明文写入**：使用 `experimental_bearer_token`，可保留官方账号上下文，但密钥以明文存在于 `config.toml`。

**安全设计**：

- 每次改写 `config.toml` 前自动备份（`config.toml.<时间戳>.bak`），第三方页提供「还原备份」一键回滚。
- 只改动自己管理的键与 `[model_providers.*]` 块，**你的注释、空行与其它配置（含 `[profiles.*]` 里的同名键）逐字节保留**；写后校验，异常自动回滚。
- 切回官方模式会把 `config.toml` 还原成你原来的内容（含你自己写的顶层 `model`）。
- 生成的模型目录放在 `~/.codex/gptplus-catalogs/`，与 `config.toml` 同处 `~/.codex`，避免目录被移动导致 Codex 无法启动。
- 模型目录结构从**你自己机器上**的官方目录克隆，因此不会随 Codex 版本升级而失配。

## 安全说明

- 未公开的 `chatgpt.com/backend-api` 接口由本项目直接调用，OpenAI 变更接口后解析可能失效。
- 授权令牌使用 Windows DPAPI 加密，只能在同一 Windows 用户下解密。
- 同时运行多个实例（或旧版本）会争用同一令牌链，程序已加入单实例与冲突检测保护。
- 本工具仅供管理你自己拥有的账号使用，请遵守 OpenAI 服务条款。

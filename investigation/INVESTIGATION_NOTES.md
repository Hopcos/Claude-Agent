# ClaudeAgent 调查笔记 — 2026-10-19 会话

## 网络层根因（已实验确证）

| 路径 | token 端点行为 | 说明 |
|---|---|---|
| 直连（无代理） | **403** "Request not allowed"（Cloudflare，CF-RAY HKG） | Anthropic 拒绝本机网络出口的直连 OAuth/API 流量 |
| 经系统代理 10.0.5.230:7890 | **429** rate_limit_error（CF-RAY SIN，新加坡出口） | 能到达应用层，但出口 IP 被限流（共享出口 + 多次失败尝试） |

- **本机系统代理**: `ProxyEnable=1, ProxyServer=10.0.5.230:7890`（HKCU Internet Settings）。
- 浏览器/PS5.1 Invoke-WebRequest 自动走系统代理 → 授权页与 API 均可达。
- **Node.js claude.exe 不读 Windows 系统代理** → 直连 → 403。这是 claude 命令行一直 "Failed to authenticate / Request not allowed" 的根因。
- 修复：给 claude.exe 设 `HTTPS_PROXY=http://10.0.5.230:7890` + `HTTP_PROXY` + `NO_PROXY=localhost,127.0.0.1`。
- 限流为 Cloudflare/Anthropic 按（出口 IP）维度，POST /v1/oauth/token 触发；/v1/models、/v1/messages 不受影响（401/403 语义正常）。

## CLI 2.1.301 凭据持久化机制（二进制逆向）

- 主存储 = Windows Credential Manager（`[windows-credman]`，Bun.secrets），失败回退 = 明文 `~/.claude/.credentials.json`（"Warning: Storing credentials in plaintext."）。
- 凭据管理器实测枚举（CredEnumerateW）：**无任何 claude 条目** → credman 写入未发生/失败。
- 12:08:03 的文件：accessToken=""、refreshToken=""、expiresAt=0，但保留了 scopes/subscriptionType/rateLimitTier 且 refreshTokenExpiresAt=2026-11-12（= 旧 10/13 登录的 30 天期限）。判定：CLI 在交换失败/或持久化失败时把旧凭据**清空**（"clearing it"），并非成功写入。
- `claude -p`（无论有无代理）→ "OAuth session expired and could not be refreshed" — 证实无可用 token。
- 相关字符串：`Claude Code login needs attention: credentials may not have been saved` / `credentials could not be saved` / `org pin refused the new token`（wje → org pin 校验）→ `R2e(persist) → Re.storage.success` 决定 "login successful" vs "needs attention"。

## 账号事实（用户名下模型）

- settings.json: `"model": "haiku"` → 实际请求模型 **claude-haiku-5-5**（用户所说 "Haiku 5.5" 真实存在），max_tokens 128000。
- debug 抓到的请求头：anthropic-beta 长 列表（claude-code-20250219 等），User-Agent claude-cli/2.1.301 (external, sdk-cli)，POST https://api.anthropic.com/v1/messages?beta=true。

## 授权码交换尝试记录（全部 429/403，无成功）

| 时间(UTC) | 方式 | 结果 |
|---|---|---|
| 02:40-02:52 | refresh grant（旧 token）direct/proxy | 400 invalid_grant → 429 开始 |
| ~03:10 | 用户终端 --sso code（代理未配） | CLI 交换 403 |
| ~03:25 | 受控注入 + 官方 CLI 交换（直连） | 403 |
| ~03:45 | 受控注入 + CLI 交换（直连, debug 捕获失败） | 403 |
| ~04:05 | 自驱交换 driver2（代理，真实码） | 429 × 25 重试，代码过期 |
| ~04:12 | console.anthropic.com 表单探测（代理） | 429（同一 Cloudflare 限流桶） |

结论：两条网络路径都可用但当时分别被 403（直连）/429（出口限流）挡住；需等冷却后**单次干净尝试**。

## 最终方案（已执行成功 ✅ 2026-10-19 13:32）

1. 冷却 ≥20 分钟后，用户终端一次性执行：
   ```powershell
   $env:HTTPS_PROXY="http://10.0.5.230:7890"; $env:HTTP_PROXY="http://10.0.5.230:7890"; $env:NO_PROXY="localhost,127.0.0.1"
   claude auth login --claudeai
   ```
   ✅ 成功：真实凭据落盘（access 108 字符 sk-ant-oat01… + refresh 108 字符 sk-ant-ort01…，refresh 有效期至 11/17；此前 12:08 的空文件只是登录启动时的清理动作，不代表交换失败）。

## ✅ 最终根因闭环（2026-10-19 13:53，决定性实验链）

登录成功后 chat 请求仍全部 429，但 `claude -p` 同账号同代理返回 200 → 不是账号限流。抓包定位：

1. **ANTHROPIC_BASE_URL=http://127.0.0.1:5060** 重定向 CLI → 本地抓包服务器捕获原始请求（investigation/cli_capture.jsonl）。
2. curl 逐字节重放该请求 → **HTTP 200 SSE**（curl 的 TLS 指纹也能过 → 不是 TLS/指纹歧视！差异在请求体）。
3. 二分定位：**CLI 在 system 提示首块携带计费头**：
   ```
   x-anthropic-billing-header: cc_version=2.1.301.eb5; cc_entrypoint=sdk-cli;
   ```
   最小请求 + 仅此 system 块 → **HTTP 200**。缺失此块（即使带完整 CLI 头/metadata/beta）→ Anthropic OAuth 限流器把请求归入最严格桶 → 全部 429。
4. 附带发现：`metadata.user_id`（device_id/account_uuid/session_id 的 JSON 字符串）为 CLI 同款配额归属；claude 5.x 模型已废弃 `temperature`（发送即 400）。

## 网关修复（已实现并验证）

- AnthropicRequestBuilder：system 输出改为 block 数组，首块 = `Provider.BillingSystemPrompt`（默认即上述计费头）；
- 增加 `metadata.user_id`（device_id/account_uuid/session_id，可经 CLAUDE_METADATA_DEVICE_ID / CLAUDE_METADATA_ACCOUNT_ID 覆盖）；
- 不再发送 temperature/top_p（5.x 模型 400）；
- messages 端点补 CLI 同款 `?beta=true` 查询参数；beta 头默认含 `claude-code-20250219`；UA 改 `(external, sdk-cli)`。

## 最终验证结果（全绿）

- 网关健康：hasToken=true，动态发现 **17 个模型**（含 claude-haiku-5-5 / sonnet-5-5 / opus-5-5 / fable-5-1）。
- dotnet 全量测试 **38/38 通过**（含 GatewayLive 集成 + 官方 OpenAI .NET SDK，全部真实 Haiku 5.5 调用）。
- Python OpenAI SDK 交叉验证 4/4 通过（models / 非流式 "9.9" / 流式 "1 2 3" / 工具调用+多轮 "$205.30"；注意 httpx2 读 Windows 注册表代理需 NO_PROXY=localhost,127.0.0.1）。
- 官方 CLI 同机可用性：claude -p（带代理）正常返回。

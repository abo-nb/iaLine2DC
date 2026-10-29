# 交接文档

> 最后更新：2026-10-30　对应提交：`4969777`
> 一句话：把 LINE 班级群的消息自动收进 MongoDB，用 LLM 拆成结构化条目，再推到 Discord 对应科目的频道。

---

## 1. 当前状态（先看这个）

| 项目 | 状态 |
|---|---|
| 代码 | 已完成 M0–M2，`dotnet build` 0 警告 0 错误，测试 1/1 通过 |
| 仓库 | https://github.com/abo-nb/iaLine2DC （`main`，工作区干净） |
| 部署 | Render Free：https://ialine2dc.onrender.com |
| LINE | webhook 已能验签、入库、入队（本地用真实 secret 验过） |
| LLM | 用真实 Gemini key 跑通过一次完整分类（产出 1 条 item） |
| Discord | 网关能连上（`ready as 114资应喵`），但**线上实例正卡在限流封禁里**，见第 7 节 |
| 未验证 | Discord 实际发卡片、斜杠指令端到端、资料库频道、撤回同步 |

**接手第一件事**：确认 Discord 是否已恢复（第 7 节），再决定是否继续部署。

---

## 2. 架构

```
LINE 群（每群一科）
   │ POST /line/webhook  + X-Line-Signature
   ▼
[Ingress] 验签 → 写 messages + jobs → 立刻 200
   ▼
[jobs 集合] 原子认领（findOneAndUpdate）
   ▼
[Worker ×2]
   ├─ 附件下载（LINE 侧会过期，优先级最高）→ 转存 Discord 资料库
   ├─ 30 秒合并窗口（同群同人连续发言合成一段）
   ├─ Semantic Kernel → Gemini/OpenAI → items[]
   ├─ 落库 + searchTokens 索引
   └─ 按 subscriptions 分发到各科目频道
   ▼
[Discord Bot] 斜杠指令 / 检索 / 配置
```

**单进程**：webhook、worker、Discord 网关同宿主。Render 免费层只有 Web Service 免费，Background Worker 要付费。

### 为什么要这么设计（避免后人「优化」回去）

| 决定 | 原因 |
|---|---|
| 不加 Redis | 班级量级（日均百条）用不上；jobs 集合 + `findOneAndUpdate` 足够，少一个要运维的组件 |
| Gemini 自写连接器 | SK 官方的 `Connectors.Google` 还是 `1.81.0-alpha`。主线模型就是 Gemini，不能压在预览包上 |
| OpenAI 用官方连接器 | 它是稳定版，且支持自定义 endpoint（DeepSeek / OpenRouter 都能接） |
| 模型名存数据库、不在环境变量 | 改模型只要一条 `/set-model`，不用重新部署 |
| 科目用「名称」当主键 | `/subscribe 数学` 一步完成建科+绑定；代价是不支持重命名 |
| 中文检索用二元组令牌 | MongoDB 的 `$text` 不能切中文，`$regex` 会随数据量变慢 |
| 附件存 Discord | Render 免费层磁盘是临时的，重启即清空 |

---

## 3. 代码地图

```
src/Msg2Dc/
├─ Program.cs                组装 DI、/line/webhook、/healthz
├─ Contracts.cs              跨模块接口（ILlmClassifier / IDiscordPublisher / ILlmModelSwitcher）
├─ Configuration/
│  ├─ Options.cs             所有配置节
│  └─ DotEnv.cs              读 .env
├─ Line/
│  ├─ LineSignature.cs       HMAC-SHA256 验签
│  ├─ WebhookIngest.cs       事件 → 文档；幂等 + 补 job
│  └─ LineApiClient.cs       下载内容、取群成员昵称
├─ Storage/
│  ├─ Documents.cs           10 个集合的文档模型
│  ├─ MongoStore.cs          连接 + 全部索引
│  ├─ JobQueue.cs            认领/重试/死信/回收
│  └─ MongoInitializer.cs    启动时建索引（失败会重试）
├─ Pipeline/
│  ├─ MessageWorker.cs       合并窗口、原子认领、分类、分发
│  └─ KeepAliveService.cs    每 N 分钟 ping 自己的 /healthz 防休眠
├─ Llm/
│  ├─ LlmClientFactory.cs    按 (provider, model) 缓存客户端
│  ├─ GeminiChatCompletionService.cs  自写的 IChatCompletionService
│  ├─ LlmClassifier.cs       提示词 + JSON 校验 + 重试 + 降级
│  ├─ LlmModelSwitcher.cs    /set-model 的验证与落库
│  └─ LlmSelectionProvider.cs 模型选择缓存
├─ Discord/
│  ├─ DiscordBotService.cs   网关 + 熔断 + 指令注册 + 邀请链接
│  ├─ DiscordCommandHandler.cs 17 条指令
│  ├─ DiscordPublisher.cs    卡片发送 + 附件归档 + 撤回删除
│  ├─ DiscordEmbeds.cs       卡片排版（含长度上限保护）
│  └─ DiscordCommands.cs     指令定义
├─ Search/Tokenizer.cs       中文二元组分词
└─ tests/Msg2Dc.Tests/       集成测试（假 OpenAI 端点 + 本地 Mongo）

scripts/secrets.ps1          凭据加密/解密（AES-256-CBC + HMAC-SHA256）
render.yaml                  Render Blueprint
Dockerfile
```

---

## 4. 本地运行

```bash
# 1) 准备配置：从 .env-save.example 复制一份 .env，填真实值
cp .env-save.example .env

# 2) 起服务
dotnet run --project src/Msg2Dc
# 默认 http://localhost:5000，/healthz 看状态

# 3) 跑测试
dotnet test
```

**本地 MongoDB**（免安装版，在 `.tools/`）：

```bash
.tools/mongodb-win32-x86_64-windows-8.0.14/bin/mongod.exe \
  --dbpath "$(pwd -W)/data/mongo" --port 27017 --logpath "$(pwd -W)/data/mongod.log"
```

> ⚠️ 不要用 `source .env-save.example` 启动。bash 会把值里的空格当命令执行，而且不处理引号。用 `.env`，代码里的 `DotEnv.Load` 按第一个 `=` 切分并 Trim，更稳。

### 手动验证 webhook

```bash
F=tests/Msg2Dc.Tests/fixtures/zh-message.json
SIG=$(openssl dgst -sha256 -hmac "$Line__ChannelSecret" -binary < "$F" | base64 -w0)
curl -i -X POST localhost:5000/line/webhook \
  -H "Content-Type: application/json" -H "X-Line-Signature: $SIG" --data-binary @"$F"
```

---

## 5. 部署（Render）

- Blueprint 文件：`render.yaml`（`type: web` / `runtime: docker` / `healthCheckPath: /healthz`）
- 环境变量（9 个 `sync: false`，在 Dashboard 填）：

| 变量 | 说明 |
|---|---|
| `Line__ChannelId` | 官方账号 Channel ID，仅用于识别与展示 |
| `Line__ChannelSecret` | 验签用，泄漏等于别人能伪造 webhook |
| `Line__ChannelAccessToken` | 下载附件、取群成员昵称 |
| `Discord__Token` | Bot Token |
| `Discord__GuildId` | 目标服务器 ID；留空且只加入一个服务器时自动识别 |
| `AdminIds` | 管理员 Discord 用户 ID，逗号分隔 |
| `Mongo__ConnectionString` | Atlas 连接串 |
| `Llm__OpenAi__ApiKey` / `Llm__Google__ApiKey` | 至少配一个 |

- **不要设 `PORT`**：Render 会注入，程序读它并绑 `0.0.0.0`。
- LINE Console 里填 `https://ialine2dc.onrender.com/line/webhook`，点 Verify，再开 **Use webhook** 和 **Allow bot to join group chats**。

---

## 6. 首次配置流程（Discord 侧）

1. 用日志里的邀请链接把 bot 加进服务器（权限 125952，不要 Administrator）。
2. 重启服务（或设好 `Discord__GuildId`），指令才会以服务器指令注册。
3. `/status` 确认供应商与模型。
4. `/set-model gemini gemini-flash-lite-latest`
5. 在 `#数学` 频道里打 `/subscribe 数学`。
6. `/set-archive #资料库`、`/set-default #未分类`。

---

## 7. 当前阻塞：Discord 网关被限流封禁

**症状**：日志刷 `Rate limit triggered: GET gateway/bot Remaining: 20xx s`，随后 `TimeoutException`，客户端反复重连。

**根因**：同一 token 短时间内被大量请求。最常见是「旧实例没停就部署新实例」——Render 零停机部署期间新旧同时在线，Discord 踢掉旧会话、旧的又重连，循环放大。**封禁是时间型的，越重试越不恢复。**

**已经做的防护**（提交 `4969777`）：15 秒窗口内 ≥3 次断线或 ≥2 次限流命中 → 主动 `StopAsync`，冷却 10 分钟，之后每次翻倍（上限 120 分钟）；单次致命断线 3 分钟后也会自愈重启。

**处置步骤**：
1. Render 上 **Suspend**（不是 Restart）。
2. 静置到限流桶 reset（日志里的 `Remaining` 归零），通常 30–40 分钟。
3. 确认只有一个实例在用这个 token（本地不要再跑）。
4. Resume，日志应出现 `Discord ready as ...`。

**铁律**：一个 token 同时只能有一个进程在连。部署前先停旧实例。

---

## 8. 已知问题 / 待办

### 高（数据正确性）

1. **归档附件链接缺 `guild_id`** —— `DiscordPublisher` 生成的 jump link 全部无效。
2. **`/reprocess` 不清理旧 item/post** —— 重跑会产生重复卡片。
3. **Discord 写操作没有超时与取消** —— 429 时 worker 会被长时间占用。

### 中

4. **`/set-model` 的命令超时（30s）短于它自己的探测窗口（45s）** —— 慢模型永远选不上，且报错误导。
5. **每次 `Ready` 重注册 17 条指令**，且注册失败也打印 `Registered N` —— 限流放大 + 日志说谎。
6. **`settings` 没有唯一键**，首次并发写可能产生多份配置。
7. **索引初始化失败只留一条 warning**，唯一约束缺失时会被静默容忍。
8. **`dead` 状态的 job 没有出口**，也没有任何保留策略（jobs / llmLogs / rawJson 会一直涨）。

### 低

9. `kind` 词表与卡片展示层不一致，`notes/question/resource` 会以英文原样显示。
10. 限流计数器按日志行累加（12 秒内可到 358 次），能正确触发熔断但噪音大。
11. `IDiscordPublisher.IsReady` 没有被使用，发布前没有就绪门控。

> 以上来自 5 个子代理的完整代码审查，已修的一批见 `496ce9e` 的提交信息。

---

## 9. 安全与凭据

- **永远不要提交** `.env`、`.env*`、`secrets.enc`——`.gitignore` 已覆盖 `.env*`（保留 `.env.example`）。
- 历史事故：`.env-save.example` 曾含真实 Discord token，被 GitHub push protection 拦下，已从提交剔除。**这次拦截是运气，不是流程。**
- 加密保存：`./scripts/secrets.sh lock` → 生成 `secrets.enc`；`./scripts/secrets.sh unlock` → 还原 `.env` 并复制到剪贴板。
- 通过对话传过的 key 一律视为已泄漏，**用完轮换**。
- `Discord__Token` 泄漏的后果：任何人都能以 bot 身份操作服务器。

---

## 10. 关键标识

| 项 | 值 |
|---|---|
| 服务地址 | `ialine2dc.onrender.com` |
| Webhook | `https://ialine2dc.onrender.com/line/webhook` |
| Bot | `114资应喵`，client id `1565362945213603860` |
| LINE Channel ID | `2012283968` |
| 数据库 | Atlas，库名 `msg2dc` |
| Render 计划 | Free：15 分钟无流量休眠，月 750 实例小时，磁盘临时 |

---

## 11. 运维速查

| 现象 | 先查哪里 |
|---|---|
| 收不到消息 | `/healthz` 的 `mongo`；LINE Console 的 Webhook 状态；`Allow bot to join group chats` 是否开 |
| 消息进了库但没推送 | `subscriptions` 里有没有该科目；`/status` 看队列积压 |
| Discord 指令没反应 | bot 是否在线（日志 `ready as`）；指令是 guild 还是 global；`AdminIds` 是否包含自己 |
| 日志刷 Rate limit | 见第 7 节，先停服务等桶恢复 |
| LLM 报 400/403 | `/status` 看模型名；`/set-model` 重新验证；key 是否有效 |
| 服务莫名重启 | Render 事件里的 `server_restarted` / OOM；免费层 512 MB |

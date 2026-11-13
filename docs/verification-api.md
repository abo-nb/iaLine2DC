# Discord 身份验证 API（给调用方）

其他服务器用这套接口确认「某个 Discord 用户确实是这个服务器里、能看见验证频道的人」。
验证通过后拿到 Bearer token，就可以把 token 当作该用户的登录凭证。

Base URL：`https://ialine2dc.onrender.com`

```
调用方                          bot                       用户
  │  ① GET /members (API Key)                            │
  │ ─────────────────────────▶│                          │
  │  ② POST /request {userId} │ ── 私信/私密线程 4 位码 ─▶ │
  │ ◀──────── requestId ──────│                          │
  │                           │           用户把码告诉调用方
  │  ③ POST /confirm {requestId, code}                    │
  │ ─────────────────────────▶│                          │
  │ ◀──── accessToken + refreshToken ─────────────────────│
  │  ④ GET /me (Bearer)       │                          │
```

---

## 一、准备（服务器管理员做一次）

1. 在目标服务器里，确保 bot 在哪个频道，就在哪个频道打：`/verify-channel channel:#频道`
   - **能看到这个频道的成员**就是允许验证的人（按频道权限覆盖计算，个人覆盖也算）。
2. `/verify-key` 拿到 `API Key`，交给调用方。轮换用 `/verify-key rotate:true`。
3. 如果要用「成员列表」接口：在 Discord 开发者后台给应用开启 **Server Members Intent**
   （Applications → Bot → Privileged Gateway Intents → Server Members Intent → Save）。
   没开时 `/members` 会返回 503。

## 二、约定

- 所有请求和返回都是 JSON，`Content-Type: application/json`。
- 需要 API Key 的接口放请求头：`X-Api-Key: <key>`。
- 需要登录态的接口放：`Authorization: Bearer <accessToken>`。
- 时间都是 UTC（ISO 8601）。

## 三、接口

### 1. 查配置状态（可选，用来探活）

```http
GET /api/verify/status
```

```json
{
  "configured": true,
  "clientIp": "203.0.113.7",
  "guildId": "1565364542995960009",
  "channelId": "1565364543809650690",
  "codeTtlMinutes": 5,
  "accessTokenMinutes": 60,
  "refreshTokenDays": 30
}
```

`configured=false` 说明管理员还没跑 `/verify-channel`。

### 2. 成员列表

```http
GET /api/verify/members
X-Api-Key: <key>
```

```json
{
  "guildId": "1565364542995960009",
  "channelId": "1565364543809650690",
  "count": 2,
  "members": [
    {
      "id": "568768836153769996",
      "username": "abohp",
      "globalName": null,
      "nickname": null,
      "avatarUrl": "https://cdn.discordapp.com/avatars/568768836153769996/xxxx.png?size=128"
    }
  ]
}
```

不含 bot 账号。大服务器会慢一点（要分页拉成员并逐个算权限）。

### 3. 发起验证

```http
POST /api/verify/request
X-Api-Key: <key>

{ "userId": "568768836153769996" }
```

```json
{
  "requestId": "0efa286c50bf4bdd9188b7c686524399",
  "expiresAt": "2026-11-12T11:44:16.2801819Z",
  "delivery": "thread"
}
```

- `delivery` 是 `"dm"`（私信）或 `"thread"`（在验证频道里开的私密线程，只有该用户能看）。
  调用方提示用户「去看私信」还是「去看频道里的验证线程」。
  走线程时，**线程名称就是 `[验证码]-用户名`**（例如 `[AB12]-abohp`），用户不点开也能看到码。
- 验证码 **4 位、全大写、字母+数字各两个**，5 分钟内有效。
- 同一用户 5 秒内只能请求一次；同一 IP 一分钟超过 300 次会被临时封禁 10 分钟（返回 429 + `Retry-After`）。
- 用户不在服务器 → 404。

### 4. 提交验证码

```http
POST /api/verify/confirm

{ "requestId": "0efa286c50bf4bdd9188b7c686524399", "code": "2Q3B" }
```

```json
{
  "accessToken": "CokF5zWTGrLR...",
  "refreshToken": "Vw2t6_v2mZH1...",
  "expiresIn": 3600
}
```

- `expiresIn` 是 access token 的秒数。
- 验证码错误返回 400，最多错 5 次；成功一次后同一个 `requestId` 立即失效。
- 成功后如果码是通过私密线程发的，线程会自动删除。

### 5. 刷新 token

```http
POST /api/verify/token

{ "refreshToken": "Vw2t6_v2mZH1..." }
```

返回和 `/confirm` 一样的结构。**refresh token 一次一换**：调用后旧的 refresh token 和旧 access token 都失效，请用返回的新的一对。

### 6. 查询用户

```http
GET /api/verify/me
Authorization: Bearer <accessToken>
```

```json
{
  "id": "568768836153769996",
  "username": "abohp",
  "globalName": null,
  "nickname": null,
  "avatarUrl": "https://cdn.discordapp.com/avatars/...",
  "guildId": "1565364542995960009",
  "roles": [{ "id": "1565364542995960009", "name": "@everyone" }],
  "joinedAt": "2026-10-29T13:59:50.137Z"
}
```

access token 过期或失效 → 401（用 refresh token 换新的）。

## 四、错误

统一格式：`{ "error": "说明" }`，HTTP 状态码是主要判断依据。

| 状态码 | 含义 |
|---|---|
| 400 | 参数不对、验证码错误/过期、请求已使用 |
| 401 | API Key 不对；access/refresh token 无效或过期 |
| 404 | 用户不在服务器、未配置验证频道 |
| 429 | 请求太频繁：同一用户 5 秒内只能发起一次；同一 IP 一分钟超过 300 次会被临时封禁 10 分钟 |
| 502 | 无法投递验证码（私信和私密线程都失败） |
| 503 | 服务端无法枚举成员（多半是没开 Server Members Intent） |

## 五、示例（curl）

```bash
BASE=https://ialine2dc.onrender.com
KEY=<api-key>

# 成员列表
curl -s -H "X-Api-Key: $KEY" "$BASE/api/verify/members"

# 发起验证
curl -s -X POST "$BASE/api/verify/request" \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"userId":"568768836153769996"}'

# 提交验证码
curl -s -X POST "$BASE/api/verify/confirm" \
  -H "Content-Type: application/json" \
  -d '{"requestId":"<requestId>","code":"2Q3B"}'

# 用 token 查用户
curl -s -H "Authorization: Bearer <accessToken>" "$BASE/api/verify/me"

# 刷新
curl -s -X POST "$BASE/api/verify/token" \
  -H "Content-Type: application/json" \
  -d '{"refreshToken":"<refreshToken>"}'
```

Node 版：

```js
const BASE = 'https://ialine2dc.onrender.com';
const KEY = process.env.VERIFY_API_KEY;

async function verifyUser(userId, code) {
  const req = await fetch(`${BASE}/api/verify/request`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY },
    body: JSON.stringify({ userId })
  });
  const ticket = await req.json();       // { requestId, expiresAt, delivery }
  // …把 requestId 存进会话，等用户读码后：
  const res = await fetch(`${BASE}/api/verify/confirm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ requestId: ticket.requestId, code })
  });
  return res.json();                     // { accessToken, refreshToken, expiresIn }
}
```

## 六、注意事项

- `API Key` 等同于「可以枚举成员 + 触发验证」，只放在服务端，别放进前端页面。
- `refreshToken` 存服务端数据库；`accessToken` 短（默认 60 分钟），过期就用 refresh 换。
- 用户可能关了服务器私信，所以**一定要处理 `delivery: "thread"`** 的提示文案。
- token 只对当前验证频道所在的服务器有效；换服务器要重新配置 `/verify-channel`。

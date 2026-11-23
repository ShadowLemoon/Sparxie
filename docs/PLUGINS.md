# 第三方游戏插件

Sparxie 可以加载第三方提供的游戏模块（插件）。主仓库和主发布包**不包含、不托管、不推荐任何第三方插件**；
插件由它的作者在自己的仓库发布，用户自行下载并放进数据目录。本文件只描述接口与加载规则。

> 插件未经 Sparxie 审计，启用后以**管理员权限**在 SessionHost 进程内运行原生代码，不提供沙箱。
> 只启用你信任其来源的插件。是否合规（游戏用户协议、反作弊、著作权等）由插件作者和使用者自行判断。

## 目录布局

数据目录 = 程序所在目录（便携布局下是 `Sparxie.exe` 所在目录）：

```
<DataRoot>\plugins\<id>\plugin.json   # 静态元数据
<DataRoot>\plugins\<id>\module.dll    # 模块，固定文件名
<DataRoot>\plugin-trust.json          # 用户确认记录（DLL、plugin.json 的 SHA-256 与大小）
<DataRoot>\plugin-data\<id>\          # 传给模块的数据目录
```

只扫描 `plugins\` 下一层目录，忽略符号链接与重解析点。主发布包不会带 `plugins\`。

## plugin.json

```json
{
  "id": "ext.<vendor>.<game>",
  "displayName": "显示名",
  "abiMajor": 1,
  "exeNames": ["Game.exe"],
  "caps": ["injects", "hotParams"],
  "version": "1.0.0",
  "license": "MIT",
  "homepage": "https://example.invalid/"
}
```

- `id`：必须匹配 `^ext\.[a-z0-9-]{2,32}\.[a-z0-9-]{2,32}$`，**必须与目录名一致**，不能占用内置游戏 id。
- `exeNames`：1–8 个不含路径的 `*.exe`。启动时 EXE 必须在这个白名单里；`explorer.exe`、`cmd.exe`、`powershell.exe` 等系统进程名被禁止。这个黑名单只是防误配置，**不是安全边界**：插件本身就是管理员权限的原生代码，能做的事远多于"启动哪个程序"。
- `caps`：`createsProcess`（模块自己创建游戏进程）、`injects`（会向游戏注入代码，启用确认里会醒目提示）、`needsArgs`（需要宿主生成命令行参数）、`hotParams`（支持热调）。必须与模块 `get_info` 返回的能力位**完全一致**。
- `plugin.json` 不超过 64 KB。

## 图标、背景与自动查找（可选）

都写在 `plugin.json` 里，由宿主执行，插件 DLL 不参与：

- `icon`：插件目录里的图片文件名（ico/png/jpg/webp/bmp，≤ 8 MB，不能带路径，不能是链接）。用在游戏标签和标题。不写 `icon` 时，包里有 `icon.ico` 或 `icon.png`（按这个顺序）就自动用它。
- 背景，按优先级：
  1. `background`：插件目录里的图片文件名，规则同上。
  2. `backgroundUrl`：https 图片地址。
  3. `backgroundApi`：宿主请求一个 JSON 接口（≤ 256 KB），按点分隔的路径取出图片地址（数字是数组下标）。
     - `url`（https）、`path`（必填）。`method` 可选 `GET`（默认）或 `POST`；`body` 是 JSON 对象（≤ 4 KB），有它时按 POST 发送。
     - `path` 指向字符串时是一张背景；指向数组时每个元素一张（最多 8 张）。元素是对象时，`itemPath` 取图片地址，可选的 `videoPath` 取动态背景（视频）地址。
     - 对应 Collapse 插件接口里的 `GetBackgroundEntries` + 图片/视频标记，区别是主程序不加载插件 DLL，所以写成声明。
     - 例（鹰角启动器）：`"path": "proxy_rsps.0.get_main_bg_image_rsp.main_bg_image", "itemPath": "url", "videoPath": "video_url"`。
  
  网络来的图由宿主下载，只允许 https，拒绝本机和内网地址（重定向后的最终地址也检查），限 8 MB，并按文件头确认是位图。图片缓存在背景缓存目录，离线时用旧缓存。
- `autoFind`：给“自动查找”提供线索。找到的文件名必须在 `exeNames` 里，并且是本机盘符路径。
  - `uninstallNames`：卸载项键名里包含的关键字（≤ 8 项），命中的安装目录下会搜索白名单 EXE。
  - `registry`：`[{ "hive": "HKCU" | "HKLM", "key": "Software\\…", "value": "InstallPath", "subdirectory": "game" }]`（≤ 8 项）。读到的值是安装目录或 EXE 路径，`subdirectory` 可选。
  - 另外总会用 Windows 的最近运行记录和正在运行的进程。

图片和这些线索不参与信任哈希（只影响显示和查找，不会让插件加载或运行任何东西）。

## 注册表键备份/恢复钩子（可选）

插件如果需要修改 HKCU 下的某些键，不能自己写，而要通过宿主提供的回调（`SpxHostApi`：`registry_set_dword` / `registry_set_string` / `registry_delete_value`，在 `SpxLaunchRequest.host` 里传入）。这套机制**与分辨率、全屏等画面设置无关**，也不改变画面设置的行为。

在 `plugin.json` 里声明允许改动的前缀：

```json
"registryHooks": { "keys": ["Software\\Vendor\\Game"] }
```

- 前缀必须形如 `Software\<厂商>\<游戏>`（至少三段，只在 HKCU 下），不得包含 `Run`、`RunOnce`、`Policies`、`Winlogon`、`Classes`、`Microsoft`、`Wow6432Node` 等自启动/策略/系统类路径段，最多 8 个。
- 回调传入的 key 必须落在声明的前缀内（前缀边界按路径段判断，不区分大小写），否则返回 `SPX_ERR_INVALID_ARGUMENT`，不会写入。
- 某个值第一次被改动前，宿主先把原值（包括"原本不存在"）写进 `recovery\registry-hook-<id>.json`，再写入；只支持 DWORD 和字符串，其他类型的值无法安全备份，宿主会拒绝改动它。
- 会话结束（含失败）时由宿主恢复；SessionHost 异常退出留下的备份，下次启动该插件前先恢复，备份里记录了当时的前缀，所以插件被删除后也能恢复。恢复失败时备份保留，且该插件拒绝启动，直到处理完；部分恢复后，已恢复的条目会从备份里去掉，避免下次重放旧原值。key、值名最长 255 个字符，字符串值最长 4096 个字符，超过的调用会被拒绝（不会被截断）；回调里的 `result` 可以为 NULL（宿主不会写）。

## 信任与加载

1. 新放入的插件**默认不启用**。Desktop「设置 → 第三方游戏插件」会显示名称、来源信息、DLL 与 `plugin.json` 的 SHA-256、大小和风险提示，用户点击确认后才写入 `plugin-trust.json`。
2. DLL 或 `plugin.json` 任何一个哈希变化，状态变为"需要重新确认"，对应游戏无法启动，配置保留。
3. Broker 每次启动会话都重新读取 `plugin.json` 与信任记录，只放行已确认的插件，下架（停用/删除）立即生效。Broker 与 Desktop 只读 JSON，不执行插件代码。
4. SessionHost 加载时以 `FILE_SHARE_READ` 打开 DLL 并持有句柄到会话结束，在该句柄上重算哈希；`LoadLibraryExW` 只搜索 DLL 所在目录和 System32；用 `get_info` 与 `plugin.json` 交叉核对 id、EXE 列表和能力位，不一致则拒绝。
5. 信任记录的作用是**发现文件被替换**，不能防止同一用户权限下的恶意软件同时改写 DLL 和信任记录。

## C ABI

头文件：`native/plugin-abi/include/sparxie_game_module.h`（MIT 许可，见同目录 `LICENSE`），当前是**草案，ABI 尚未冻结**。

- 唯一导出 `sparxie_module_query(host_abi_major, host_abi_minor, &api)`，返回函数表：
  `get_info / create_session / prepare_launch / attach_job / install / post_install / wait_exit / set_param / abort / release`。
- 同一会话的所有调用都发生在宿主的**同一个专用线程**上；每次调用有超时（`install` 120 秒；`create_session`、`prepare_launch`、`attach_job`、`post_install`、`set_param` 30 秒；`wait_exit` 每轮 5 秒；`abort` 10 秒），超时按失败处理。超时后专用线程可能仍卡在插件里：宿主会一直保留回调表直到进程退出，所以插件**不得在 `release` 返回之后**再调用宿主回调，但在此之前的迟到回调是安全的。
- 不得让 C++ 或 SEH 异常越过边界；错误信息写进宿主提供的 `SpxResult.message`（512 个 UTF-16 字符，宿主会截断并过滤控制字符），没有 free 函数。
- `release` 必须幂等。宿主不会调用 `FreeLibrary`，模块卸载靠进程退出。
- `wait_exit(timeout_ms)` 在游戏退出时返回 `SPX_OK`，本轮超时返回 `SPX_ERR_TIMEOUT`。
- 模块自己创建游戏进程时（`createsProcess`），宿主在 `install` 前调用 `attach_job`，模块创建进程后必须立即把它加入该 Job。
- 宿主服务（注册表钩子）只在声明了 `registryHooks` 时才会传入，否则 `host` 为空。
- `create_session` 里的 `settings_json` 是按 `settingsSchema` 归一化之后的设置，见下一节。

## 插件设置（settingsSchema）

插件不提供界面。需要用户可调的选项时，在 `plugin.json` 里声明 `settingsSchema`，Sparxie 据此在「配置」页生成表单、校验并补默认值，再把结果以 `settings_json`（UTF-16 JSON 对象文本）通过 `create_session` 传给插件。

```json
"settingsSchema": { "fields": [
  { "key": "inputMode", "label": "输入模式", "type": "enum", "default": "touch",
    "options": [ { "value": "touch", "label": "触屏" }, { "value": "pc", "label": "键鼠" } ] },
  { "key": "autoResolution", "label": "分辨率跟随串流", "type": "bool", "default": true },
  { "key": "width", "label": "宽度", "type": "int", "min": 320, "max": 7680, "default": 1920,
    "showWhen": { "key": "autoResolution", "equals": false } },
  { "key": "uiScale", "label": "界面缩放（%）", "type": "int", "min": 100, "max": 180, "step": 5, "default": 140, "hint": "100–180，步长 5" }
] }
```

- 字段类型：`bool`、`int`（`min`/`max` 必填，可选 `step`）、`enum`（`options` 1–32 项，`default` 必须是其中之一）、`string`（`maxLength` ≤ 256）。每个字段有 `key`（`^[A-Za-z][A-Za-z0-9_]{0,39}$`，不得重复）、`label`、`default`，可选 `hint`、`showWhen`（`{ "key": 前面已声明的字段, "equals": 值 }`，条件不满足时界面隐藏该字段，但值仍会传给插件）。最多 64 个字段。
- **schema 写错会让整个插件无效**（manifest 解析失败），而不是运行时才出错。
- **归一化是全函数，不是拒绝会话的理由**：启动前由 SessionHost 按 manifest 的 schema 处理一遍——缺失或类型不对用默认值，数值夹到 `[min, max]` 并按 `step` 对齐，枚举不在选项里用默认值，字符串去控制字符并截断，未知键丢弃。插件拿到的 `settings_json` 一定包含 schema 里的每个字段。
- Broker 只做结构检查：`settings_json` 必须是不超过 16 KB 的 JSON 对象，内置游戏不得携带。
- 设置存在 `config.json` 里对应配置的 `settings` 字段；插件暂时不可用（未启用、被删除）时原样保留，不丢用户的值。
- 目前**没有热更新**：设置只在会话创建时传入，运行中改设置不会推给插件（热调只有 `set_param(key, int64)`，目前只有 `targetFps`）。

## 测试用 mock 模块

`tests/MockGameModule/mock_game_module.c` 是一个最小实现（`ext.mock.game`）。用 Visual Studio 的 `cl.exe` 构建：

```powershell
powershell -File tests\MockGameModule\build_mock.ps1
```

未构建时，依赖它的测试会直接返回（不会失败）。

## 下架与清理

- Desktop「停用」：撤销信任，保留文件；「删除」：撤销信任、删除 `plugins\<id>\`、删除遗留的 `recovery\registry-hook-<id>.json`，并清掉偏好里记住的该游戏。
- 使用该游戏的配置会保留，只是无法启动；重新安装同 id 的插件并确认后即可恢复。
- 删除或停用时，Desktop 会先尝试恢复该插件遗留的注册表钩子备份（游戏正在运行时不动注册表）；删除遇到无法恢复的备份会**拒绝删除**，停用则照常停用并提示备份仍保留。
- 手工下架：删除 `plugins\<id>\` 并从 `plugin-trust.json` 里删掉对应记录即可，主程序照常启动。

## 作者自述字段与发布清单（可选）

`plugin.json` 里这些字段只用于展示，主程序会标注“作者自述，未经 Sparxie 验证”：

- `author`（≤ 64 字）、`description`（≤ 300 字）、`privacyNote`（≤ 300 字，说明是否联网、读写哪些数据）。
- `links`：最多 4 项 `{ "title": "…", "url": "https://…" }`，只接受 https。
- settingsSchema 字段可加 `group`（≤ 32 字）：同组的相邻字段在设置页共用一个分组标题。

**建议插件作者发布时提供**：

1. 256 px 的图标（`icon`）和一张背景（`background` 或 `backgroundApi`）。
2. 在发布页公布 `module.dll` 的 SHA-256。用户可以和确认框里显示的哈希手动核对。
3. 说明插件是否联网、会改哪些注册表项和文件（写进 `privacyNote`）。
4. 每次发布递增 `version`：导入时主程序据此判断升级或降级。

## 导入

设置里“第三方游戏插件”卡片支持把 `.zip` 或文件夹直接拖进去（一次最多 8 个，其余文件会被忽略并提示）；也可以点“导入插件…”选择。

导入分两步：先检查（解到暂存目录、校验），再提交（换进 plugins 目录）。已有同 id 的插件时，会按版本提示“升级 / 降级 / 替换”，内容完全相同则直接跳过；对应游戏正在运行时拒绝替换。失败分类：不支持的文件、过大、路径越界、含链接、缺 plugin.json、manifest 无效、缺 module.dll、id 非法、被占用。

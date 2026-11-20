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

## 注册表键备份/恢复钩子（可选）

插件如果需要修改 HKCU 下的某些键，不能自己写，而要通过宿主提供的回调（`SpxHostApi`：`registry_set_dword` / `registry_set_string` / `registry_delete_value`，在 `SpxLaunchRequest.host` 里传入）。这套机制**与分辨率、全屏等画面设置无关**，也不改变画面设置的行为。

在 `plugin.json` 里声明允许改动的前缀：

```json
"registryHooks": { "keys": ["Software\\Vendor\\Game"] }
```

- 前缀必须形如 `Software\<厂商>\<游戏>`（至少三段，只在 HKCU 下），不得包含 `Run`、`RunOnce`、`Policies`、`Winlogon`、`Classes`、`Microsoft`、`Wow6432Node` 等自启动/策略/系统类路径段，最多 8 个。
- 回调传入的 key 必须落在声明的前缀内（前缀边界按路径段判断，不区分大小写），否则返回 `SPX_ERR_INVALID_ARGUMENT`，不会写入。
- 某个值第一次被改动前，宿主先把原值（包括"原本不存在"）写进 `recovery\\registry-hook-<id>.json`，再写入；只支持 DWORD 和字符串，其他类型的值无法安全备份，宿主会拒绝改动它。
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
- 目前 `settings_json` 固定为 `{}`，`settingsSchema` 校验与表单尚未实现。

## 测试用 mock 模块

`tests/MockGameModule/mock_game_module.c` 是一个最小实现（`ext.mock.game`）。用 Visual Studio 的 `cl.exe` 构建：

```powershell
powershell -File tests\MockGameModule\build_mock.ps1
```

未构建时，依赖它的测试会直接返回（不会失败）。

## 下架与清理

- Desktop「停用」：撤销信任，保留文件；「删除」：撤销信任、删除 `plugins\<id>\`、删除遗留的 `recovery\registry-backup-<id>.json`，并清掉偏好里记住的该游戏。
- 使用该游戏的配置会保留，只是无法启动；重新安装同 id 的插件并确认后即可恢复。
- 删除或停用时，Desktop 会先尝试恢复该插件遗留的注册表钩子备份（游戏正在运行时不动注册表）；删除遇到无法恢复的备份会**拒绝删除**，停用则照常停用并提示备份仍保留。
- 手工下架：删除 `plugins\<id>\` 并从 `plugin-trust.json` 里删掉对应记录即可，主程序照常启动。

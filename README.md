# Sparxie

无 GUI 的 Windows 控制台启动器：通过 `Sparxie.LauncherCore` 管理绝区零、原神和崩坏：星穹铁道的多个安装 Profile，由管理员 Broker 与按游戏独立的 SessionHost 承担运行会话。

## 特性

- 三款游戏均以“触屏初始化成功”为启动成功硬条件；
- 原神、星铁可选 FPS 解锁，默认开启 120，支持运行中主目标 FPS 热调；
- 绝区零消费固定版本的私有闭源 Runtime，客户端不访问私库；
- 不识别游戏版本，每次启动按实际结构、特征与原始内容严格校验；
- Running 前 SessionHost 异常终止本次游戏；Running 后三款游戏均保留游戏进程；
- ZZZ 配置损坏或 Host 异常时，走共享恢复例程恢复 PC 文件配置；
- 每次 Launcher 运行持有一个私有 Broker 控制流，控制端退出不主动结束已启动的游戏。

## 控制台用法

从空白配置开始先创建 Profile；`--exe` 必须是完整路径，且 EXE 文件名必须属于对应游戏白名单。创建时不强制该文件已经存在，实际启动仍会在 SessionHost 边界验证。

```text
Sparxie.Launcher.exe profile add --id genshin-cn --name "原神 国服" --game genshin --variant cn --exe "D:\Games\Genshin Impact Game\YuanShen.exe"
Sparxie.Launcher.exe profile list
Sparxie.Launcher.exe profile show genshin-cn
Sparxie.Launcher.exe profile set genshin-cn --target-fps 144 --priority high
Sparxie.Launcher.exe profile select genshin-cn
Sparxie.Launcher.exe profile remove genshin-cn
Sparxie.Launcher.exe launch [profile-id-or-name]
```

`list` 是 `profile list` 的兼容别名。`profile set` 可修改名称、Variant、EXE 路径和适用设置；Hoyo 通用设置为 `--fps`、`--target-fps`、`--background-fps-limit`、`--background-fps`、`--priority`，原神还支持档位和触控 UI 缩放设置。运行 `Sparxie.Launcher.exe help` 可查看完整参数与范围。

`launch` 进入会话后可在同一进程输入 `fps <10-1000>` 热调目标帧率，输入 `quit` 关闭控制端。配置文件继续放在 Launcher 同目录的 `config.json`。

## 生成便携包

GitHub Actions 的 `build-test` 会下载固定的 `ZZZ-TouchRuntime v1.0.0` Runtime 资产、校验 SHA-256、构建三进程并生成完整 `sparxie-portable` 工件。

本地先下载并准备 Runtime staging：

```powershell
python build/package_runtime.py download-runtime `
  --destination zzz-runtime.zip
python build/package_runtime.py prepare-runtime `
  --archive zzz-runtime.zip `
  --destination runtime/zzz
```

完成三个项目的 `dotnet publish` 后，再运行：

```powershell
python build/package_runtime.py package `
  --runtime-directory runtime/zzz `
  --hoyo-touch-core native/HoyoTouchCore/build/Release/HoyoTouchCore.dll
```

成品路径为 `artifacts/Sparxie-portable.zip`。`package` 只覆盖 `artifacts/Sparxie/` 与该 ZIP，并会拒绝哈希不符、缺少清单 DLL 或 PDB 的 staging。

## 仓库结构

```text
Sparxie/
├─ Sparxie.slnx
├─ src/
│  ├─ Sparxie.Launcher/      # 控制台参考宿主
│  ├─ Sparxie.LauncherCore/  # 无界面启动器核心
│  ├─ Sparxie.Broker/        # 管理员 Broker
│  ├─ Sparxie.SessionHost/   # 按参数运行具体游戏会话
│  ├─ Sparxie.Contracts/     # Profile / RPC / 错误契约
│  └─ Sparxie.Infrastructure/
├─ native/
│  └─ HoyoTouchCore/         # Hoyo 上游 subtree + 适配区
├─ runtime/
│  └─ zzz/                   # Runtime 构建 staging，不提交 Runtime 源码
├─ tests/
├─ build/
│  ├─ zzz-runtime.json       # 固定 Runtime 版本、资产清单与 SHA-256
│  └─ package_runtime.py      # 下载/校验 Runtime、准备 staging、生成便携 ZIP
├─ docs/
└─ .github/workflows/
```

## 许可

自有组件采用闭源专有许可，详见根目录 `LICENSE`；第三方组件许可与版权声明见 `THIRD-PARTY-NOTICES.md`。

## 状态

实施中（2026-08-17 快照）：

- 已完成：Contracts/配置、LauncherCore、CLI Profile 创建/查看/修改/选择/删除、Broker/SessionHost 闭环、Profile 快照映射、Broker 受控管道握手、私有 Broker 单控制流与收尾、ZZZ 配置恢复与共享恢复例程、滚动文件日志、脱敏诊断包、固定 `v1.0.0` ZZZ Runtime 的 SHA-256 校验与 ZIP 合包、Hoyo subtree 与 C ABI DLL、发布许可证交付和无 GUI 发布审计。
- 待验证：生产 UAC `runas` 人工确认；Hoyo（原神/星铁）与 ZZZ Runtime 的真实游戏实机；六个正式服实机验收。
- 测试：124 个（LauncherCore 29、Config 26、Zzz 16、Broker 43、HoyoAbi 10）Release 回归；发布包要求包含 Launcher、Broker、SessionHost、HoyoTouchCore 与固定 ZZZ Runtime，且不含 PDB 或凭据痕迹。

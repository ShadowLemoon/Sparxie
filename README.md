# Sparxie

面向触屏设备的 Windows 游戏启动器（WinUI 3 桌面应用）：统一管理绝区零、原神和崩坏：星穹铁道的安装配置，由管理员 Broker 与按游戏独立的 SessionHost 承担运行会话，并为游戏注入触屏运行时。

## 特性

- 三款游戏均以“触屏初始化成功”为启动成功硬条件；
- 原神、星铁可选 FPS 解锁，默认开启 120，支持运行中主目标 FPS 热调；
- 绝区零消费固定版本的私有闭源 Runtime（仅注入，不读写游戏配置），客户端不访问私库；
- 不识别游戏版本，每次启动按实际结构、特征与原始内容严格校验；
- Running 前 SessionHost 异常终止本次游戏；Running 后三款游戏均保留游戏进程；
- 桌面端关闭时可选：退出前确认 / 最小化到托盘 / 直接退出；
- 游戏配置修改后自动保存，没有保存按钮。

## 使用

解压发布包后运行 `Sparxie.exe`（壳启动器，只负责拉起 `app-<版本>\Sparxie.Desktop.exe`）。配置、日志与缓存都写在 `Sparxie.exe` 同目录：

- `config.json`：游戏配置与界面偏好（`desktop` 段）；
- `logs\`、`cache\`。

主页选择游戏后点“启动游戏”；每个游戏的路径、帧率等在游戏设置页修改，改动自动保存。

## 生成发布包

GitHub Actions 的 `build-test` 会下载固定的 ZZZ Runtime、校验 SHA-256、构建并调用 `build/package_desktop.ps1` 生成 `Sparxie-desktop-<版本>-win-x64.zip`。本地：

```text
python build/package_runtime.py download-runtime --destination zzz-runtime.zip
python build/package_runtime.py prepare-runtime --archive zzz-runtime.zip --destination runtime/zzz
powershell -File build/package_desktop.ps1 -Version 0.1.0 -RuntimeDirectory runtime/zzz -OutputDirectory artifacts
```

`prepare-runtime` 会拒绝哈希不符或缺少清单 DLL 的 Runtime；发布包审计测试要求包含壳、Desktop、Broker、SessionHost、HoyoTouchCore 与固定 ZZZ Runtime，且不含 PDB 或凭据痕迹。

## 仓库结构

```text
Sparxie/
├─ Sparxie.slnx
├─ src/
│  ├─ Sparxie.Desktop/       # WinUI 3 桌面应用
│  ├─ Sparxie.Shell/         # 壳启动器（原生，读取 version.ini）
│  ├─ Sparxie.LauncherCore/  # 无界面启动器核心（Profile 管理、Broker 客户端）
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
│  ├─ package_runtime.py     # 下载/校验 Runtime、准备 staging
│  └─ package_desktop.ps1    # 生成壳 + app-<版本>\ 目录结构的发布 ZIP
├─ docs/
└─ .github/workflows/
```

## 许可

自有组件采用闭源专有许可，详见根目录 `LICENSE`；第三方组件许可与版权声明见 `THIRD-PARTY-NOTICES.md`。

## 状态

- 已完成：Contracts/配置、LauncherCore、桌面 GUI、Broker/SessionHost 闭环、Hoyo 触屏载荷引导、ZZZ Runtime v2 注入、固定 Runtime SHA-256 校验与打包、滚动日志、脱敏诊断包、发布许可证交付与发布包审计。
- 待验证：生产 UAC `runas` 人工确认；Hoyo（原神/星铁）与 ZZZ Runtime 的真实游戏实机；各正式服实机验收。
- 命令行入口已移除；自动化入口（如自定义 URL 协议）待定。

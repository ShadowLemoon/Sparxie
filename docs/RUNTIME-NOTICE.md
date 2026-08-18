# RUNTIME NOTICE — 绝区零 Runtime

本通知说明随 Sparxie 发布包分发的绝区零（Zenless Zone Zero）Runtime
组件的身份与分发边界。本组件为独立闭源组件，不受根目录 `LICENSE`（Sparxie
专有许可）约束，其版权归属与允许的使用/分发边界以本通知为准。

## 组件

| 项 | 值 |
|---|---|
| 组件名称 | ZZZTouchCore.dll / ZZZTouchRuntime.dll |
| 组件类型 | 闭源 Runtime（触屏注入与配置） |
| 版权归属 | 私有版权持有人（非 Sparxie 仓库作者） |
| 许可证 | 私有；仅允许随 Sparxie 成品 ZIP 分发固定版本成品 |

## 固定版本

以下信息与 `build/zzz-runtime.json` 保持一致：

| 项 | 值 |
|---|---|
| Runtime 版本 | v1.0.0 |
| Release 资产名 | ZZZTouchRuntime-v1.0.0-win-x64.zip |
| SHA-256 | e8fba7e8b237ecd9806a225bdaced97cc0f1eb8ae22b344ae0d3f47439e4f1c2 |
| 包含文件 | ZZZTouchCore.dll、ZZZTouchRuntime.dll |

## 使用与分发边界

- 仅分发固定版本成品，不包含、不暗示提供源码；
- 客户端不在运行时访问 Runtime 仓库，不包含任何仓库凭据；
- Runtime 版本与哈希固定于可审查的构建配置；升级需显式修改版本与哈希并执行回归；
- 构建时从 `ShadowLemoon/ZZZ-TouchRuntime` 的指定公开 Release 资产下载，哈希不符或缺少任一清单 DLL 时直接失败，不落入发布包；
- 根目录 Sparxie 专有许可不改变本组件的独立许可身份。

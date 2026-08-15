# HoyoTouchCore UPSTREAM 基线

## 上游来源

- 仓库：`winTEuser/Genshin_StarRail_fps_unlocker`
- 许可证：MIT（见 `upstream/LICENSE`）
- 派生自：`34736384/genshin-fps-unlock`（MIT）
- 内嵌组件：`inih/INIReader`（BSD-3-Clause，`upstream/src/inireader.h` 引用 LICENSE.txt，上游未随包提供，分发时需保留 BSD 声明）

## 导入方式

Git subtree --squash 导入，prefix：`native/HoyoTouchCore/upstream`。

```bash
git subtree add --squash --prefix=native/HoyoTouchCore/upstream \
    <上游仓库> HEAD
```

## 当前基线

- 上游提交：`4ba09224c68b8a82ec041517b144ee3287884a8c`
- 导入日期：2026-08-15
- subtree 提交：`git log --oneline --grep="Add.*upstream" -1` 或 `git log --oneline -- native/HoyoTouchCore/upstream | tail -n 1`

## 保持原样约束

- 保留 `.gitattributes` 语义（`*.h`/`*.cpp` 为 UTF-16 + CRLF）；
- 禁止自动格式化、编码转换和无关重排；
- subtree 更新后校验 `git diff` 无整文件编码差异；
- 每次更新后重放并审核本地补丁。

## 本地补丁（首版无）

首版不在 upstream 内打补丁。以下差异全部放在 subtree 外的 adapter/：

- C ABI 导出入口（`adapter/include/hoyo_touch_core_abi.h`、`adapter/src/hoyo_adapter.cpp`）；
- `EnableFps` 条件与纯触屏分支（不扫描/不安装 FPS Patch）；
- 触屏失败硬检查（移除上游静默降级）；
- 主目标 FPS 热调仅更新 adapter 内稳定 `FpsValue`（对齐 32 位原子写），不暴露 `&FpsValue`；
- `Sync failed!` 弹窗独立屏蔽，与 `AutoExit` 解耦，保留最后值保底逻辑；
- 隔离控制台、INI、热键与插件入口（adapter/SessionHost 绕开，不删除上游代码）。

## 后续同步风险

- 上游 `main.cpp` 的入口流程、扫描特征与载荷字段随游戏更新变化；
- 需要时先评估能否在 adapter 内实现，再考虑最小 upstream 补丁；
- 每次上游更新必须跑：构建、离线测试、实机回归（原神国服/国际服、星铁国服/国际服）。

# Sparxie 测试覆盖缺口清单（2026-08-15 审计）

依据：《Sparxie 统一触屏启动器实施计划》"应该有的测试"章节逐项审计。
当前测试基线：83 通过（HoyoAbi 10 / Config 20 / Zzz 14 / Broker 39）。

## 可离线继续补齐（未做）

### 配置层
- ~~文件写入/刷盘/原子替换失败注入~~ ✅ 已完成（AppConfigStoreTests：目录不可写/不存在目录，原配置保持）
- ~~程序目录不可写时启动失败~~ ✅ 已完成（ConfigDirectoryNotWritable 路径）
- Profile 动态增删改、多 Profile 隔离
- 完整 EXE 路径保存后与恢复值一致断言
- 不回退 AppData

### 契约与设置层
- 新 Profile 全量默认值（FPS 开/120/后台开/10/Normal/跟随档位关/缩放 400%）
- 原神 30→60、45→主目标、60→1000 档位映射
- 星铁不显示原神专属设置（UI 条件显示）
- Realtime 不出现在 UI/配置模型/RPC（已覆盖 Broker 校验拒绝）
- 四档进程优先级 Win32 映射（两端已覆盖：字符串校验 + C ABI 透传，中间映射未单测）

### Broker/SessionHost 生命周期
- UI 退出后旧 Broker 关闭控制入口、活动 Host 继续、不结束 Host
- UI 重开新 Broker 不重连旧 Broker/Host
- Broker 正常/异常退出不结束既有 Host 或游戏
- Broker 消失后 UI 显示控制连接丢失、热调停止
- Broker 消失后既有 Host 仍持互斥；新 Broker 不接管
- Host 只处置自己创建的进程（非本次进程不受影响）
- 句柄不继承给无关子进程、外部/嵌套 Job、Running 前转换失败

### 发布与诊断
- ~~发布 staging/ZIP 内容清单（DLL/EXE/许可证/Notices 完整、无 CI Secret）~~ ✅ 已完成（ReleaseArtifactAuditTests：关键文件、PDB、凭据/私库痕迹扫描）
- ~~目录不可写退出~~ ✅ 已完成（配置层）
- 自包含目录运行
- 完整诊断包敏感项审计（config.invalid-*、凭据、路径组合）

### WPF UI（当前无 UI 测试项目）
- ✅ 新增 Sparxie.App.Tests（提交 cde5b13）：AppState Profile 管理 6 单测 +
  WPF STA 页面构造冒烟 3 个（MainWindow/HomePage/SettingsPage，含 Hoyo 配置与空配置态）
- 窗口/NavigationView 交互/条件设置显示/异常提醒/热调入口/诊断按钮（需 UI 自动化，未覆盖）
- 125%-200% DPI 与触屏尺寸（需 UI 自动化）

## 只能由真实 Runtime 或实机证明

- Hoyo 真实特征扫描、目标唯一性、页属性与原始字节校验
- Hoyo FPS Patch/触屏载荷/FpsValue 读取与游戏内实际效果
- 原神档位、后台 FPS、虚拟 DPI 实际效果
- Running 后 Host 消失：游戏保留、最后 FPS 保留、热调停止
- ZZZ 真实 DLL ABI、注入、Hook、触点状态、ZZZTouchRelease
- ZZZ 各配置切换阶段恢复回滚
- 三款游戏 Running 前/后 Host 强杀语义
- 临时 Job 对真实游戏启动链与反作弊的影响
- 六个首版发布阻塞安装（原神/星铁/ZZZ × 国服/国际服正式服）
- 国服/国际服组合并行验收

## 已由架构保证（无需单测）
- Profile 快照不可变：快照经 JSON 序列化传给独立 Host 进程（天然副本）

## 跟踪方式
- 离线可补项：进入后续迭代待办，按价值排序
- 实机项：阻塞首版发布，需真实游戏环境逐项验收

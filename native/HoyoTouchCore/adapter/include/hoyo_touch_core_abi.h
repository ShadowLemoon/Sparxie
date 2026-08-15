#ifndef SPARXIE_HOYO_TOUCH_CORE_ABI_H
#define SPARXIE_HOYO_TOUCH_CORE_ABI_H

// Sparxie Hoyo Touch Core C ABI（首版冻结契约）。
//
// 边界规则：
// - 纯 C 头文件，extern "C"，x64，__cdecl；
// - 所有结构带 size 字段，bool 用固定宽度整数（0/1）；
// - 字符串使用带长度的 UTF-16（wchar_t 指针 + 字符数）；
// - 不跨边界传递 std::string/wstring/容器/异常；
// - 会话使用不透明句柄（void*），由 SessionHost 持有；
// - 不暴露 &FpsValue、SessionHost PID 或宿主地址空间。
// 任何不兼容修改必须提升 HOYO_ABI_VERSION。

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#define HOYO_ABI_VERSION 1u
#define HOYO_ABI_NAME "SparxieHoyoTouchCore"

// ---- 错误码（稳定，跨版本不变）----
typedef enum HoyoTouchError
{
    HOYO_OK = 0,
    HOYO_ERR_INVALID_ARGUMENT = 1,
    HOYO_ERR_ABI_MISMATCH = 2,
    HOYO_ERR_CONFIG_INVALID = 3,
    HOYO_ERR_SCAN_FAILED = 4,
    HOYO_ERR_PATCH_FAILED = 5,
    HOYO_ERR_INSTALL_CONFIRM_FAILED = 6,
    HOYO_ERR_PROCESS_NOT_FOUND = 7,
    HOYO_ERR_SESSION_NOT_ACTIVE = 8,
    HOYO_ERR_NOT_SUPPORTED = 9,
    HOYO_ERR_INTERNAL = 10,
} HoyoTouchError;

// ---- 进程优先级（语义枚举，映射由 adapter 内部完成）----
typedef enum HoyoProcessPriority
{
    HOYO_PRIORITY_BELOW_NORMAL = 0,
    HOYO_PRIORITY_NORMAL = 1,
    HOYO_PRIORITY_ABOVE_NORMAL = 2,
    HOYO_PRIORITY_HIGH = 3,
} HoyoProcessPriority;

// ---- 启动请求：SessionHost 传入的不可变快照 ----
typedef struct HoyoLaunchRequest
{
    uint32_t size;              // sizeof(HoyoLaunchRequest)
    uint32_t abi_version;       // 必须 == HOYO_ABI_VERSION
    int32_t game_type;          // 0 = genshin, 1 = starRail
    int32_t fps_unlock_enabled; // 0/1
    int32_t target_fps;         // 10–1000，仅在 fps_unlock_enabled=1 时有效
    int32_t background_fps_limit_enabled; // 0/1
    int32_t background_fps;     // 10–1000
    int32_t process_priority;   // HoyoProcessPriority
    int32_t genshin_follow_in_game_preset;  // 0/1，仅原神
    int32_t genshin_preset_30_fps;          // 仅原神
    int32_t genshin_preset_60_fps;          // 仅原神
    int32_t genshin_touch_ui_scale_override_enabled; // 0/1，仅原神
    int32_t genshin_touch_ui_scale_percent;          // 100–500，仅原神
    const wchar_t* game_executable_path;    // UTF-16，NUL 结尾
    uint32_t game_executable_path_chars;    // 不含 NUL
} HoyoLaunchRequest;

// ---- 结果结构 ----
typedef struct HoyoResult
{
    uint32_t size;       // sizeof(HoyoResult)
    int32_t error_code;  // HoyoTouchError
    uint32_t stage;      // 阶段码（见下）
    uint32_t detail;     // 保留：子错误/HRESULT
    const wchar_t* message; // UTF-16，可为 NULL
    uint32_t message_chars;
} HoyoResult;

// 阶段码（首版稳定集合）
#define HOYO_STAGE_VALIDATION 1u
#define HOYO_STAGE_SCAN_TOUCH 2u
#define HOYO_STAGE_SCAN_FPS 3u
#define HOYO_STAGE_PATCH 4u
#define HOYO_STAGE_INSTALL_CONFIRM 5u
#define HOYO_STAGE_WAIT 6u

// ---- 导出函数（首版最小集合）----

// 查询 ABI 版本与能力。
HoyoTouchError hoyo_get_abi_version(uint32_t* version, uint32_t* size);

// 创建会话：校验请求、执行扫描与 Patch，全部成功才返回；失败时整次失败。
// 返回不透明会话句柄（非 NULL 表示成功）。
// session_out 由调用方持有，必须用 hoyo_release 释放。
HoyoTouchError hoyo_create_session(
    const HoyoLaunchRequest* request,
    HoyoResult* result,
    void** session_out);

// 启动游戏进程并执行注入（挂起创建 → 扫描 → Patch → 恢复主线程）。
// 调用方传入 game_pid；SessionHost 已创建进程。
HoyoTouchError hoyo_launch(
    void* session,
    uint32_t game_pid,
    HoyoResult* result);

// 更新主目标 FPS（运行中热调）。仅更新 Host 内稳定 FpsValue（对齐 32 位原子写）。
HoyoTouchError hoyo_set_target_fps(
    void* session,
    int32_t target_fps,
    HoyoResult* result);

// 等待游戏退出。timeout_ms 0 表示无限。
HoyoTouchError hoyo_wait_game_exit(
    void* session,
    uint32_t timeout_ms,
    HoyoResult* result);

// 释放会话（卸载注入句柄、释放资源）。幂等。
HoyoTouchError hoyo_release(void* session);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // SPARXIE_HOYO_TOUCH_CORE_ABI_H

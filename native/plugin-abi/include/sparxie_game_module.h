/* SPDX-License-Identifier: MIT */
#ifndef SPARXIE_GAME_MODULE_H
#define SPARXIE_GAME_MODULE_H

/*
 * Sparxie 游戏模块 C ABI（草案，未冻结）。本目录（native/plugin-abi）以 MIT 许可发布，
 * 与主程序的 GPLv3 许可相互独立，见同目录 LICENSE。
 *
 * 边界规则：
 * - 纯 C，x64，__cdecl；所有结构首字段为 size，宿主只读取 size 覆盖的字段；
 * - 布尔与枚举使用固定宽度整数；字符串为 UTF-16 指针 + 字符数（不含 NUL）；
 * - 不跨边界传递 C++ 对象/异常，不跨边界分配内存：错误信息写入宿主提供的内联缓冲，没有 free 函数；
 * - 会话使用不透明句柄；release 幂等；
 * - 同一会话的所有调用发生在宿主的同一个专用线程上（消息钩子等资源归调用线程所有）；
 * - 不得让 C++ 或 SEH 异常越过边界；
 * - 宿主传入的指针只在调用期间有效；模块返回的 info 在 DLL 卸载前有效（宿主不会调用 FreeLibrary）。
 *
 * 版本：ABI_MAJOR 不兼容变更 +1；ABI_MINOR 只追加字段/函数 +1。
 * 静态元数据（id、EXE 白名单、能力、设置 schema）以旁挂 plugin.json 为准，宿主会用 get_info 交叉核对。
 */

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SPX_MODULE_ABI_MAJOR 1u
#define SPX_MODULE_ABI_MINOR 0u

typedef enum SpxError {
    SPX_OK = 0,
    SPX_ERR_INVALID_ARGUMENT = 1,
    SPX_ERR_ABI_MISMATCH = 2,
    SPX_ERR_PROCESS_NOT_FOUND = 7,
    SPX_ERR_NOT_SUPPORTED = 9,
    SPX_ERR_INTERNAL = 10,
    SPX_ERR_INJECT_FAILED = 100,
    SPX_ERR_TIMEOUT = 101
} SpxError;

/* 能力位。是否写 Windows 注册表不由模块声明：由核心按 plugin.json 的 registry 段统一备份/写入/恢复。 */
enum {
    SPX_CAP_CREATES_PROCESS = 1, /* 模块自己创建游戏进程（并负责绑定宿主给的 Job）；否则宿主创建并传 game_pid */
    SPX_CAP_INJECTS = 2,         /* 会向游戏进程注入代码；宿主在安装确认中醒目提示 */
    SPX_CAP_NEEDS_ARGS = 4,      /* 需要宿主生成的命令行参数 */
    SPX_CAP_HOT_PARAMS = 8       /* 支持 set_param 热调 */
};

typedef struct SpxStr {
    const wchar_t* p;
    uint32_t chars;
} SpxStr;

/* 错误信息内联在结构里；message 以 NUL 结尾，宿主会截断并过滤控制字符。 */
typedef struct SpxResult {
    uint32_t size;
    int32_t error_code;
    uint32_t stage;   /* 模块自定义，仅写入日志 */
    uint32_t detail;  /* 模块自定义，仅写入日志 */
    wchar_t message[512];
} SpxResult;

typedef struct SpxModuleInfo {
    uint32_t size;
    uint32_t abi_major;
    uint32_t abi_minor;
    SpxStr id;            /* 必须与 plugin.json 的 id 一致 */
    SpxStr display_name;
    SpxStr version;
    uint32_t caps;
    const SpxStr* exe_names; /* 必须与 plugin.json 的 exeNames 一致 */
    uint32_t exe_count;
} SpxModuleInfo;

/*
 * 宿主服务（只在 create_session 期间由宿主传入）。指针与回调在会话 release 返回之前一直有效，release 返回之后不得再调用。
 * 注册表键只能通过这些回调修改：宿主校验 key 必须落在 plugin.json 的 registryHooks.keys 声明的 HKCU 前缀内，
 * 第一次改动某个值之前先把原值持久化，会话结束（或下次启动发现遗留）时由宿主恢复。
 * 它与分辨率/全屏等画面设置无关，也不改变画面设置的行为。
 * key 是 HKCU 下的相对路径（例如 L"Software\\Vendor\\Game"）；key/name 最长 255 个字符、字符串值最长 4096 个字符，超出一律拒绝。
 * result 可以为 NULL（宿主不会写）；非 NULL 时宿主只回写 result->size 覆盖到的范围。调用可发生在会话的任意模块线程上。
 */
typedef struct SpxHostApi {
    uint32_t size;
    uint32_t abi_major;
    uint32_t abi_minor;
    void* context; /* 原样传回每个回调 */
    SpxError (*registry_set_dword)(void* context, SpxStr key, SpxStr name, uint32_t value, SpxResult* result);
    SpxError (*registry_set_string)(void* context, SpxStr key, SpxStr name, SpxStr value, SpxResult* result);
    SpxError (*registry_delete_value)(void* context, SpxStr key, SpxStr name, SpxResult* result);
} SpxHostApi;

typedef struct SpxLaunchRequest {
    uint32_t size;
    SpxStr exe_path;
    SpxStr arguments;
    SpxStr settings_json; /* 已按 plugin.json 的 settingsSchema 校验并补齐默认值 */
    SpxStr log_dir;
    SpxStr data_dir;
    const SpxHostApi* host; /* 追加字段：size 不覆盖它时视为没有宿主服务 */
} SpxLaunchRequest;

typedef struct SpxModuleApi {
    uint32_t size;
    uint32_t abi_major;
    uint32_t abi_minor;
    SpxError (*get_info)(const SpxModuleInfo** out);
    SpxError (*create_session)(const SpxLaunchRequest* request, SpxResult* result, void** session);
    SpxError (*prepare_launch)(void* session, SpxResult* result);
    /* Running 前失效保护 Job（宿主在 install 之前调用，仅当宿主有 Job 时）。CREATES_PROCESS 的模块创建进程后必须立即 Assign；
       其余模块的进程由宿主自己 Assign，宿主不会调用本函数。 */
    SpxError (*attach_job)(void* session, void* job_handle, SpxResult* result);
    /* game_pid 为 0 表示由模块自己创建进程（CREATES_PROCESS）；阻塞到注入确认。 */
    SpxError (*install)(void* session, uint32_t game_pid, SpxResult* result);
    SpxError (*post_install)(void* session, SpxResult* result);
    SpxError (*wait_exit)(void* session, uint32_t timeout_ms, SpxResult* result);
    SpxError (*set_param)(void* session, SpxStr key, int64_t value, SpxResult* result);
    SpxError (*abort)(void* session, SpxResult* result);
    SpxError (*release)(void* session); /* 幂等 */
} SpxModuleApi;

/* 唯一导出符号。host_abi_major 不被支持时返回 SPX_ERR_ABI_MISMATCH。 */
__declspec(dllexport) SpxError __cdecl sparxie_module_query(
    uint32_t host_abi_major, uint32_t host_abi_minor, const SpxModuleApi** out);

#ifdef __cplusplus
}
#endif

#endif /* SPARXIE_GAME_MODULE_H */

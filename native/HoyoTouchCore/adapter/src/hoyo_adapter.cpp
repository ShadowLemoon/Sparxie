// Sparxie Hoyo Touch Core adapter：C ABI 实现骨架。
//
// 首版目标：冻结 C ABI 契约并验证 upstream 源码可编入 DLL。
// 实际扫描/Patch 流程复用 upstream 内部函数，由后续步骤接入：
//   - 纯触屏条件（fps_unlock_enabled=0 时不扫描/不安装 FPS Patch）
//   - 主目标 FPS 热调（更新 adapter 内稳定 FpsValue，对齐 32 位原子写）
//   - Sync failed 弹窗与 AutoExit 解耦的独立屏蔽
// 上游 main() 控制台入口不做 DLL 导出。

#include "hoyo_touch_core_abi.h"

#include <atomic>
#include <mutex>
#include <string>

namespace
{
// adapter 持有的稳定 FpsValue：SessionHost 通过 hoyo_set_target_fps 更新。
// 游戏内载荷沿用上游同步协议读取（该地址属于 Runtime 内部实现，不进入公共 ABI）。
std::atomic<int32_t> g_fps_value{120};
std::mutex g_session_mutex;
bool g_has_session = false;
} // namespace

extern "C" {

HoyoTouchError hoyo_get_abi_version(uint32_t* version, uint32_t* size)
{
    if (version == nullptr || size == nullptr)
    {
        return HOYO_ERR_INVALID_ARGUMENT;
    }
    *version = HOYO_ABI_VERSION;
    *size = sizeof(uint32_t);
    return HOYO_OK;
}

HoyoTouchError hoyo_create_session(
    const HoyoLaunchRequest* request,
    HoyoResult* result,
    void** session_out)
{
    if (request == nullptr || result == nullptr || session_out == nullptr)
    {
        return HOYO_ERR_INVALID_ARGUMENT;
    }

    result->size = sizeof(HoyoResult);
    result->stage = HOYO_STAGE_VALIDATION;

    if (request->size < sizeof(HoyoLaunchRequest))
    {
        result->error_code = HOYO_ERR_ABI_MISMATCH;
        result->message = L"request.size 过小";
        result->message_chars = 16;
        return HOYO_ERR_ABI_MISMATCH;
    }
    if (request->abi_version != HOYO_ABI_VERSION)
    {
        result->error_code = HOYO_ERR_ABI_MISMATCH;
        result->message = L"ABI 版本不匹配";
        result->message_chars = 13;
        return HOYO_ERR_ABI_MISMATCH;
    }
    if (request->game_executable_path == nullptr || request->game_executable_path_chars == 0)
    {
        result->error_code = HOYO_ERR_INVALID_ARGUMENT;
        result->message = L"game_executable_path 为空";
        result->message_chars = 22;
        return HOYO_ERR_INVALID_ARGUMENT;
    }
    if (request->target_fps < 10 || request->target_fps > 1000)
    {
        result->error_code = HOYO_ERR_INVALID_ARGUMENT;
        result->message = L"target_fps 超出 10-1000";
        result->message_chars = 22;
        return HOYO_ERR_INVALID_ARGUMENT;
    }

    {
        std::lock_guard<std::mutex> lock(g_session_mutex);
        if (g_has_session)
        {
            result->error_code = HOYO_ERR_SESSION_NOT_ACTIVE;
            result->message = L"已有活动会话";
            result->message_chars = 12;
            return HOYO_ERR_SESSION_NOT_ACTIVE;
        }
        g_fps_value.store(request->target_fps, std::memory_order_relaxed);
        g_has_session = true;
    }

    // 占位句柄：实际会话状态由后续接入步骤填充。
    *session_out = reinterpret_cast<void*>(1);
    result->error_code = HOYO_OK;
    result->stage = HOYO_STAGE_VALIDATION;
    result->message = nullptr;
    result->message_chars = 0;
    return HOYO_OK;
}

HoyoTouchError hoyo_launch(void* session, uint32_t game_pid, HoyoResult* result)
{
    if (session == nullptr || result == nullptr)
    {
        return HOYO_ERR_INVALID_ARGUMENT;
    }
    result->size = sizeof(HoyoResult);
    // 未接入上游扫描/Patch 前的占位实现：始终失败，避免误报成功。
    result->stage = HOYO_STAGE_SCAN_TOUCH;
    result->error_code = HOYO_ERR_NOT_SUPPORTED;
    result->message = L"Hoyo launch 尚未接入";
    result->message_chars = 19;
    return HOYO_ERR_NOT_SUPPORTED;
}

HoyoTouchError hoyo_set_target_fps(void* session, int32_t target_fps, HoyoResult* result)
{
    if (session == nullptr || result == nullptr)
    {
        return HOYO_ERR_INVALID_ARGUMENT;
    }
    result->size = sizeof(HoyoResult);
    result->stage = HOYO_STAGE_VALIDATION;
    if (target_fps < 10 || target_fps > 1000)
    {
        result->error_code = HOYO_ERR_INVALID_ARGUMENT;
        result->message = L"target_fps 超出 10-1000";
        result->message_chars = 22;
        return HOYO_ERR_INVALID_ARGUMENT;
    }

    {
        std::lock_guard<std::mutex> lock(g_session_mutex);
        if (!g_has_session)
        {
            result->error_code = HOYO_ERR_SESSION_NOT_ACTIVE;
            result->message = L"会话未激活";
            result->message_chars = 11;
            return HOYO_ERR_SESSION_NOT_ACTIVE;
        }
        g_fps_value.store(target_fps, std::memory_order_relaxed);
    }

    result->error_code = HOYO_OK;
    result->message = nullptr;
    result->message_chars = 0;
    return HOYO_OK;
}

HoyoTouchError hoyo_wait_game_exit(void* session, uint32_t timeout_ms, HoyoResult* result)
{
    if (session == nullptr || result == nullptr)
    {
        return HOYO_ERR_INVALID_ARGUMENT;
    }
    result->size = sizeof(HoyoResult);
    result->stage = HOYO_STAGE_WAIT;
    result->error_code = HOYO_ERR_NOT_SUPPORTED;
    result->message = L"Hoyo wait 尚未接入";
    result->message_chars = 17;
    return HOYO_ERR_NOT_SUPPORTED;
}

HoyoTouchError hoyo_release(void* session)
{
    if (session == nullptr)
    {
        return HOYO_ERR_INVALID_ARGUMENT;
    }
    std::lock_guard<std::mutex> lock(g_session_mutex);
    g_has_session = false;
    return HOYO_OK;
}

} // extern "C"

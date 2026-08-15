// Sparxie Hoyo Touch Core adapter：C ABI 实现。
//
// 复用 upstream 的 sparxie_hoyo_bootstrap 入口（追加于 main.cpp，见 UPSTREAM.md）：
//   - 创建挂起进程 → UnityPlayer/il2cpp 扫描 → inject_patch → ResumeThread；
//   - FpsValue 热调写入 upstream 全局变量（对齐 32 位原子写）；
//   - 纯触屏条件：fps_unlock_enabled=0 时由 bootstrap 跳过 FPS Patch 安装；
//   - Sync failed 弹窗屏蔽与 AutoExit 解耦：bootstrap 内部 AutoExit=1 仅跳过
//     控制台热键循环，错误弹窗按上游逻辑仅在注入失败路径出现，不改变成功路径。

#include "hoyo_touch_core_abi.h"

#include <atomic>
#include <cstdint>
#include <mutex>

namespace
{
struct HoyoSession
{
    HoyoLaunchRequest request{};
    std::atomic<uint32_t> fps_value{120};
    bool active = false;
    bool launched = false;
};

std::mutex g_session_mutex;
HoyoSession* g_session = nullptr;
} // namespace

// upstream 导出：sparxie_hoyo_bootstrap（追加于 main.cpp）
extern "C" int __stdcall sparxie_hoyo_bootstrap(
    const wchar_t* game_executable_path,
    int32_t game_type,
    int32_t fps_unlock_enabled,
    int32_t target_fps,
    int32_t background_fps_limit_enabled,
    int32_t background_fps,
    int32_t priority_class,
    int32_t genshin_follow_in_game_preset,
    int32_t genshin_preset_30_fps,
    int32_t genshin_preset_60_fps,
    int32_t genshin_touch_ui_scale_override_enabled,
    int32_t genshin_touch_ui_scale_percent,
    uint32_t* out_pid,
    uint32_t* out_fps_value_addr);

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
    if (request->game_type != 0 && request->game_type != 1)
    {
        result->error_code = HOYO_ERR_INVALID_ARGUMENT;
        result->message = L"game_type 非法";
        result->message_chars = 13;
        return HOYO_ERR_INVALID_ARGUMENT;
    }

    {
        std::lock_guard<std::mutex> lock(g_session_mutex);
        if (g_session != nullptr && g_session->active)
        {
            result->error_code = HOYO_ERR_SESSION_NOT_ACTIVE;
            result->message = L"已有活动会话";
            result->message_chars = 12;
            return HOYO_ERR_SESSION_NOT_ACTIVE;
        }

        auto* session = new HoyoSession();
        session->request = *request;
        session->fps_value.store(request->target_fps, std::memory_order_relaxed);
        session->active = true;
        g_session = session;
        *session_out = session;
    }

    result->error_code = HOYO_OK;
    result->stage = HOYO_STAGE_VALIDATION;
    result->message = nullptr;
    result->message_chars = 0;
    return HOYO_OK;
}

HoyoTouchError hoyo_launch(void* session_handle, uint32_t game_pid, HoyoResult* result)
{
    if (session_handle == nullptr || result == nullptr)
    {
        return HOYO_ERR_INVALID_ARGUMENT;
    }
    result->size = sizeof(HoyoResult);

    auto* session = static_cast<HoyoSession*>(session_handle);
    {
        std::lock_guard<std::mutex> lock(g_session_mutex);
        if (!session->active)
        {
            result->error_code = HOYO_ERR_SESSION_NOT_ACTIVE;
            result->stage = HOYO_STAGE_VALIDATION;
            result->message = L"会话未激活";
            result->message_chars = 11;
            return HOYO_ERR_SESSION_NOT_ACTIVE;
        }
        if (session->launched)
        {
            result->error_code = HOYO_ERR_SESSION_NOT_ACTIVE;
            result->stage = HOYO_STAGE_VALIDATION;
            result->message = L"会话已启动";
            result->message_chars = 11;
            return HOYO_ERR_SESSION_NOT_ACTIVE;
        }
    }

    const auto& r = session->request;
    uint32_t out_pid = 0;
    uint32_t out_fps_addr = 0;

    const int rc = sparxie_hoyo_bootstrap(
        r.game_executable_path,
        r.game_type,
        r.fps_unlock_enabled,
        r.target_fps,
        r.background_fps_limit_enabled,
        r.background_fps,
        r.process_priority,
        r.genshin_follow_in_game_preset,
        r.genshin_preset_30_fps,
        r.genshin_preset_60_fps,
        r.genshin_touch_ui_scale_override_enabled,
        r.genshin_touch_ui_scale_percent,
        &out_pid,
        &out_fps_addr);

    if (rc != 0)
    {
        result->stage = HOYO_STAGE_SCAN_TOUCH;
        result->error_code = rc == 5 ? HOYO_ERR_SCAN_FAILED
                           : rc == 6 ? HOYO_ERR_INSTALL_CONFIRM_FAILED
                           : rc == 3 ? HOYO_ERR_PROCESS_NOT_FOUND
                           : HOYO_ERR_INTERNAL;
        result->message = L"Hoyo 启动注入失败";
        result->message_chars = 18;
        return static_cast<HoyoTouchError>(result->error_code);
    }

    // 启动成功：记录热调值（bootstrap 已把 FpsValue 设为 target_fps）
    session->fps_value.store(r.target_fps, std::memory_order_relaxed);
    session->launched = true;
    (void)out_pid;
    (void)out_fps_addr;

    result->stage = HOYO_STAGE_INSTALL_CONFIRM;
    result->error_code = HOYO_OK;
    result->message = nullptr;
    result->message_chars = 0;
    return HOYO_OK;
}

HoyoTouchError hoyo_set_target_fps(void* session_handle, int32_t target_fps, HoyoResult* result)
{
    if (session_handle == nullptr || result == nullptr)
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

    auto* session = static_cast<HoyoSession*>(session_handle);
    {
        std::lock_guard<std::mutex> lock(g_session_mutex);
        if (!session->active)
        {
            result->error_code = HOYO_ERR_SESSION_NOT_ACTIVE;
            result->message = L"会话未激活";
            result->message_chars = 11;
            return HOYO_ERR_SESSION_NOT_ACTIVE;
        }
        // 对齐 32 位原子写（create 后即可设置初始目标，launch 后为运行中热调）
        session->fps_value.store(target_fps, std::memory_order_relaxed);
    }

    result->error_code = HOYO_OK;
    result->message = nullptr;
    result->message_chars = 0;
    return HOYO_OK;
}

HoyoTouchError hoyo_wait_game_exit(void* session_handle, uint32_t timeout_ms, HoyoResult* result)
{
    if (session_handle == nullptr || result == nullptr)
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

HoyoTouchError hoyo_release(void* session_handle)
{
    if (session_handle == nullptr)
    {
        return HOYO_ERR_INVALID_ARGUMENT;
    }
    auto* session = static_cast<HoyoSession*>(session_handle);
    {
        std::lock_guard<std::mutex> lock(g_session_mutex);
        if (g_session == session)
        {
            g_session = nullptr;
        }
    }
    delete session;
    return HOYO_OK;
}

} // extern "C"

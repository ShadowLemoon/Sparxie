/* SPDX-License-Identifier: MIT */
/*
 * 测试用 mock 游戏模块：id = ext.mock.game，EXE = Mock.exe，能力 = HOT_PARAMS。
 * 每次调用把"函数名 线程id"追加写到 data_dir\calls.log，用来验证宿主的专用线程规则。
 * settings_json 含 "fail-install" 时 install 失败；含 "slow-install" 时 install 先睡 3 秒；
 * 含 "bad-result" 时 prepare_launch 返回带控制字符的错误信息。
 */
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <wchar.h>
#include "sparxie_game_module.h"

typedef struct Session {
    wchar_t data_dir[MAX_PATH];
    int fail_install;
    int slow_install;
    int bad_result;
    int reg_set;
    int reg_bad;
    int reg_delete;
    int reg_long;
    int reg_nullres;
    int slow_reg;
    const SpxHostApi* host;
    int released;
} Session;

static const wchar_t kId[] = L"ext.mock.game";
static const wchar_t kName[] = L"Mock";
static const wchar_t kVersion[] = L"1.0";
static const wchar_t kExe[] = L"Mock.exe";
static SpxStr g_exe = { kExe, 8 };
static SpxModuleInfo g_info;
static SpxModuleApi g_api;
static wchar_t g_log_path[MAX_PATH];

static void log_call(const char* fn)
{
    FILE* f;
    if (g_log_path[0] == 0) return;
    if (_wfopen_s(&f, g_log_path, L"a") == 0 && f) {
        fprintf(f, "%s %lu\n", fn, GetCurrentThreadId());
        fclose(f);
    }
}

static void set_msg(SpxResult* r, SpxError code, const wchar_t* msg)
{
    if (!r) return;
    r->error_code = code;
    wcsncpy_s(r->message, 512, msg, _TRUNCATE);
}

static SpxError get_info(const SpxModuleInfo** out)
{
    *out = &g_info;
    return SPX_OK;
}

static int contains(SpxStr s, const wchar_t* needle)
{
    size_t n = wcslen(needle), i;
    if (!s.p || s.chars < n) return 0;
    for (i = 0; i + n <= s.chars; i++) {
        if (wcsncmp(s.p + i, needle, n) == 0) return 1;
    }
    return 0;
}

static SpxError create_session(const SpxLaunchRequest* req, SpxResult* r, void** out)
{
    Session* s;
    if (!req || !out) { set_msg(r, SPX_ERR_INVALID_ARGUMENT, L"null"); return SPX_ERR_INVALID_ARGUMENT; }
    s = (Session*)calloc(1, sizeof(Session));
    if (!s) return SPX_ERR_INTERNAL;
    wcsncpy_s(s->data_dir, MAX_PATH, req->data_dir.p, req->data_dir.chars);
    swprintf_s(g_log_path, MAX_PATH, L"%ls\\calls.log", s->data_dir);
    s->fail_install = contains(req->settings_json, L"fail-install");
    s->slow_install = contains(req->settings_json, L"slow-install");
    s->bad_result = contains(req->settings_json, L"bad-result");
    s->reg_set = contains(req->settings_json, L"reg-set");
    s->reg_bad = contains(req->settings_json, L"reg-bad");
    s->reg_delete = contains(req->settings_json, L"reg-delete");
    s->reg_long = contains(req->settings_json, L"reg-long");
    s->reg_nullres = contains(req->settings_json, L"reg-nullres");
    s->slow_reg = contains(req->settings_json, L"slow-reg");
    if (req->size >= offsetof(SpxLaunchRequest, host) + sizeof(req->host)) s->host = req->host;
    log_call("create_session");
    {
        /* 把宿主传来的 settings_json 原样写到 data_dir\\settings.json，测试据此验证宿主的归一化 */
        wchar_t path[MAX_PATH];
        FILE* sf;
        swprintf_s(path, MAX_PATH, L"%ls\\settings.json", s->data_dir);
        if (_wfopen_s(&sf, path, L"wb") == 0 && sf) {
            uint32_t i;
            for (i = 0; i < req->settings_json.chars; i++) fputc((int)(req->settings_json.p[i] & 0x7F), sf);
            fclose(sf);
        }
    }
    *out = s;
    return SPX_OK;
}

static void log_code(const char* what, SpxError rc)
{
    FILE* f;
    if (g_log_path[0] == 0) return;
    if (_wfopen_s(&f, g_log_path, L"a") == 0 && f) {
        fprintf(f, "%s %d\n", what, (int)rc);
        fclose(f);
    }
}

static void mk(SpxStr* s, const wchar_t* text)
{
    s->p = text;
    s->chars = (uint32_t)wcslen(text);
}

static void use_host_registry(Session* s)
{
    SpxStr key, name, bad;
    SpxResult r;
    if (!s->host) { log_code("no_host", SPX_ERR_INTERNAL); return; }
    memset(&r, 0, sizeof(r)); r.size = sizeof(r);
    mk(&name, L"MockValue");
    if (s->reg_set) {
        mk(&key, L"Software\\SparxieMock\\Game");
        log_code("reg_set", s->host->registry_set_dword(s->host->context, key, name, 7, &r));
    }
    if (s->reg_delete) {
        mk(&key, L"Software\\SparxieMock\\Game");
        log_code("reg_delete", s->host->registry_delete_value(s->host->context, key, name, &r));
    }
    if (s->reg_long) {
        static wchar_t big[5001];
        SpxStr value;
        int i;
        for (i = 0; i < 5000; i++) big[i] = L'a';
        big[5000] = 0;
        mk(&key, L"Software\\SparxieMock\\Game");
        mk(&value, big);
        log_code("reg_long", s->host->registry_set_string(s->host->context, key, name, value, &r));
    }
    if (s->reg_nullres) {
        mk(&bad, L"Software\\SparxieOther\\Evil");
        log_code("reg_nullres", s->host->registry_set_dword(s->host->context, bad, name, 1, NULL));
    }
    if (s->reg_bad) {
        mk(&bad, L"Software\\SparxieOther\\Evil");
        log_code("reg_bad", s->host->registry_set_dword(s->host->context, bad, name, 1, &r));
    }
}

static SpxError prepare_launch(void* h, SpxResult* r)
{
    Session* s = (Session*)h;
    log_call("prepare_launch");
    if (s->reg_set || s->reg_bad || s->reg_delete || s->reg_long || s->reg_nullres) use_host_registry(s);
    if (s->bad_result) {
        set_msg(r, SPX_ERR_INTERNAL, L"bad\r\nline\x01\x02 end");
        return SPX_ERR_INTERNAL;
    }
    return SPX_OK;
}

static SpxError attach_job(void* h, void* job, SpxResult* r)
{
    (void)h; (void)job; (void)r;
    log_call("attach_job");
    return SPX_OK;
}

static SpxError install(void* h, uint32_t pid, SpxResult* r)
{
    Session* s = (Session*)h;
    (void)pid;
    log_call("install");
    if (s->slow_install) Sleep(3000);
    if (s->slow_reg && s->host) {
        SpxStr key, name;
        SpxResult r2;
        memset(&r2, 0, sizeof(r2)); r2.size = sizeof(r2);
        mk(&key, L"Software\\SparxieMock\\Game");
        mk(&name, L"LateValue");
        Sleep(6000); /* 宿主早已超时并 Dispose：回调表必须仍然有效 */
        log_code("reg_late", s->host->registry_set_dword(s->host->context, key, name, 9, &r2));
    }
    if (s->fail_install) {
        if (r) { r->stage = 3; r->detail = 7; } /* 插件自定义的阶段/详情码，宿主只展示 */
        set_msg(r, SPX_ERR_INJECT_FAILED, L"mock inject failed");
        return SPX_ERR_INJECT_FAILED;
    }
    return SPX_OK;
}

static SpxError post_install(void* h, SpxResult* r) { (void)h; (void)r; log_call("post_install"); return SPX_OK; }

static SpxError wait_exit(void* h, uint32_t timeout_ms, SpxResult* r)
{
    (void)h; (void)r;
    log_call("wait_exit");
    /* mock：timeout_ms == 0 视为游戏已退出，否则报告超时 */
    return timeout_ms == 0 ? SPX_OK : SPX_ERR_TIMEOUT;
}

static SpxError set_param(void* h, SpxStr key, int64_t value, SpxResult* r)
{
    (void)h; (void)key; (void)value; (void)r;
    log_call("set_param");
    return SPX_OK;
}

static SpxError abort_session(void* h, SpxResult* r) { (void)h; (void)r; log_call("abort"); return SPX_OK; }

static SpxError release(void* h)
{
    Session* s = (Session*)h;
    if (!s || s->released) return SPX_OK; /* 幂等（会话内存不释放，避免重复 release 时悬空） */
    s->released = 1;
    log_call("release");
    return SPX_OK;
}

__declspec(dllexport) SpxError __cdecl sparxie_module_query(
    uint32_t host_abi_major, uint32_t host_abi_minor, const SpxModuleApi** out)
{
    (void)host_abi_minor;
    if (host_abi_major != SPX_MODULE_ABI_MAJOR) return SPX_ERR_ABI_MISMATCH;

    memset(&g_info, 0, sizeof(g_info));
    g_info.size = sizeof(g_info);
    g_info.abi_major = SPX_MODULE_ABI_MAJOR;
    g_info.abi_minor = SPX_MODULE_ABI_MINOR;
    g_info.id.p = kId; g_info.id.chars = 13;
    g_info.display_name.p = kName; g_info.display_name.chars = 4;
    g_info.version.p = kVersion; g_info.version.chars = 3;
    g_info.caps = SPX_CAP_HOT_PARAMS;
    g_info.exe_names = &g_exe;
    g_info.exe_count = 1;

    memset(&g_api, 0, sizeof(g_api));
    g_api.size = sizeof(g_api);
    g_api.abi_major = SPX_MODULE_ABI_MAJOR;
    g_api.abi_minor = SPX_MODULE_ABI_MINOR;
    g_api.get_info = get_info;
    g_api.create_session = create_session;
    g_api.prepare_launch = prepare_launch;
    g_api.attach_job = attach_job;
    g_api.install = install;
    g_api.post_install = post_install;
    g_api.wait_exit = wait_exit;
    g_api.set_param = set_param;
    g_api.abort = abort_session;
    g_api.release = release;
    *out = &g_api;
    return SPX_OK;
}

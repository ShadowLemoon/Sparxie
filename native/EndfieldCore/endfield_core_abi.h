#ifndef SPARXIE_ENDFIELD_CORE_ABI_H
#define SPARXIE_ENDFIELD_CORE_ABI_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(ENDFIELD_CORE_BUILD)
#define ENDFIELD_API __declspec(dllexport)
#elif defined(_WIN32)
#define ENDFIELD_API __declspec(dllimport)
#else
#define ENDFIELD_API
#endif

#define ENDFIELD_ABI_VERSION 1u

typedef enum EndfieldError {
  ENDFIELD_OK = 0,
  ENDFIELD_ERR_INVALID_ARGUMENT = 1,
  ENDFIELD_ERR_ABI_MISMATCH = 2,
  ENDFIELD_ERR_CONFIG_INVALID = 3,
  ENDFIELD_ERR_SCAN_FAILED = 4,
  ENDFIELD_ERR_PATCH_FAILED = 5,
  ENDFIELD_ERR_PROCESS_NOT_FOUND = 7,
  ENDFIELD_ERR_SESSION_NOT_ACTIVE = 8,
  ENDFIELD_ERR_NOT_SUPPORTED = 9,
  ENDFIELD_ERR_INTERNAL = 10,
} EndfieldError;

typedef struct EndfieldLaunchRequest {
  uint32_t size;
  uint32_t abi_version;
  int32_t fps_unlock_enabled;
  int32_t target_fps;
  int32_t touch_ui_enabled;
  int32_t width;
  int32_t height;
  int32_t refresh_rate;
  const wchar_t *game_executable_path;
  uint32_t game_executable_path_chars;
} EndfieldLaunchRequest;

typedef struct EndfieldResult {
  uint32_t size;
  int32_t error_code;
  uint32_t stage;
  uint32_t detail;
  const wchar_t *message;
  uint32_t message_chars;
} EndfieldResult;

#define ENDFIELD_STAGE_VALIDATION 1u
#define ENDFIELD_STAGE_SCAN_TOUCH 2u
#define ENDFIELD_STAGE_SCAN_FPS 3u
#define ENDFIELD_STAGE_PATCH 4u
#define ENDFIELD_STAGE_WAIT 6u

ENDFIELD_API
    EndfieldError endfield_create_session(const EndfieldLaunchRequest *request,
                                          EndfieldResult *result,
                                          void **session_out);
ENDFIELD_API EndfieldError endfield_attach_job(void *session, void *job_handle,
                                               EndfieldResult *result);
ENDFIELD_API EndfieldError endfield_launch(void *session,
                                           EndfieldResult *result);
ENDFIELD_API EndfieldError endfield_set_target_fps(void *session,
                                                   int32_t target_fps,
                                                   EndfieldResult *result);
ENDFIELD_API EndfieldError endfield_wait_game_exit(void *session,
                                                   uint32_t timeout_ms,
                                                   EndfieldResult *result);
ENDFIELD_API EndfieldError endfield_release(void *session);

#ifdef __cplusplus
}
#endif

#endif

cmake_minimum_required(VERSION 3.15)
project(SparxieHoyoTouchCore LANGUAGES C CXX ASM_MASM)

# Sparxie Hoyo Touch Core：C ABI DLL。
# upstream/ 是 Git subtree 导入的 winTEuser/Genshin_StarRail_fps_unlocker（MIT），
# adapter/ 是本项目 C ABI 与 SessionHost 对接代码（闭源专有）。
# 本 CMake 位于 subtree 外，不修改上游文件。

set(UPSTREAM_DIR ${CMAKE_CURRENT_SOURCE_DIR}/upstream)
set(ADAPTER_DIR ${CMAKE_CURRENT_SOURCE_DIR}/adapter)

# 上游源码（保持原结构、UTF-16 编码与 CRLF，不重排）
set(UPSTREAM_SOURCES
    ${UPSTREAM_DIR}/src/main.cpp
    ${UPSTREAM_DIR}/src/NTSYSAPI.h
    ${UPSTREAM_DIR}/src/inireader.h
    ${UPSTREAM_DIR}/src/shellcode_header.h
    ${UPSTREAM_DIR}/src/mempeload.code
    ${UPSTREAM_DIR}/src/syscall.asm
)

add_library(HoyoTouchCore SHARED
    ${UPSTREAM_SOURCES}
    ${ADAPTER_DIR}/src/hoyo_adapter.cpp
)

set_property(TARGET HoyoTouchCore PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
target_compile_options(HoyoTouchCore PRIVATE /d2vzeroupper- /utf-8 /EHa)
target_compile_definitions(HoyoTouchCore PRIVATE HOYO_BUILDING_DLL=1 SPARXIE_DISABLE_TLS_INIT=1)
target_include_directories(HoyoTouchCore PRIVATE
    ${ADAPTER_DIR}/include
    ${UPSTREAM_DIR}/src
)

target_link_options(HoyoTouchCore PRIVATE /FUNCTIONPADMIN)

# 导出 ABI 头：安装到构建输出，供 SessionHost P/Invoke 契约核验。
install(FILES ${ADAPTER_DIR}/include/hoyo_touch_core_abi.h
    DESTINATION include)

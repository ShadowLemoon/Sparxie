/* SPDX-License-Identifier: MIT */
/*
 * 测试用假游戏：什么都不做，睡 60 秒后退出。构建成 Mock.exe（mock 模块 manifest 里的 EXE 白名单名），
 * 供端到端冒烟测试让 SessionHost 启动一个真实的、可被结束的进程。
 */
#include <windows.h>

int main(void)
{
    Sleep(60000);
    return 0;
}

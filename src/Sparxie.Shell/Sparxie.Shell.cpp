// Sparxie 壳启动器：
// 根目录只放这个很小的 Sparxie.exe + version.ini，程序本体全部在 app-<版本>\ 目录里。
// 壳按 version.ini 的 exe_path 找到本体并启动；没有 version.ini 时取最新的 app-* 目录。
// 启动成功后清理旧版本目录，因此更新只需要放入新的 app-<版本> 并改 version.ini。
// 用户数据（config.json / desktop.json / logs / cache）放在根目录，不随版本目录被清理。

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <filesystem>
#include <string>

#pragma comment(linker, "/subsystem:windows /entry:wmainCRTStartup")
#pragma comment(lib, "user32.lib")

namespace fs = std::filesystem;

namespace
{
    constexpr wchar_t AppExeName[] = L"Sparxie.Desktop.exe";
    constexpr wchar_t AppDirPrefix[] = L"app-";

    // 读取 version.ini 里的 exe_path=...（只做最简单的 key=value 解析，不依赖 INI 库）。
    std::wstring ReadExePathFromVersionIni(const fs::path& iniPath)
    {
        HANDLE file = CreateFileW(iniPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return L"";
        }

        std::string bytes;
        char buffer[512];
        DWORD read = 0;
        while (ReadFile(file, buffer, sizeof(buffer), &read, nullptr) && read > 0)
        {
            bytes.append(buffer, read);
        }
        CloseHandle(file);

        if (bytes.size() >= 3 && static_cast<unsigned char>(bytes[0]) == 0xEF)
        {
            bytes.erase(0, 3); // UTF-8 BOM
        }

        size_t pos = 0;
        while (pos < bytes.size())
        {
            size_t end = bytes.find('\n', pos);
            if (end == std::string::npos)
            {
                end = bytes.size();
            }

            std::string line = bytes.substr(pos, end - pos);
            pos = end + 1;
            while (!line.empty() && (line.back() == '\r' || line.back() == ' '))
            {
                line.pop_back();
            }

            const std::string key = "exe_path=";
            if (line.compare(0, key.size(), key) == 0)
            {
                std::string value = line.substr(key.size());
                int length = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0);
                std::wstring wide(length, L'\0');
                MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), wide.data(), length);
                return wide;
            }
        }

        return L"";
    }

    // 没有可用的 version.ini 时：在 app-* 目录里按本体 exe 的修改时间取最新的。
    fs::path FindNewestApp(const fs::path& baseFolder)
    {
        fs::path best;
        fs::file_time_type bestTime{};
        std::error_code ec;
        for (const auto& entry : fs::directory_iterator(baseFolder, ec))
        {
            if (!entry.is_directory(ec))
            {
                continue;
            }

            const fs::path exe = entry.path() / AppExeName;
            if (!fs::exists(exe, ec))
            {
                continue;
            }

            const auto time = fs::last_write_time(exe, ec);
            if (best.empty() || time > bestTime)
            {
                best = exe;
                bestTime = time;
            }
        }

        return best;
    }

    // 命令行里去掉壳自己的路径，其余原样转给本体。
    std::wstring ForwardedArguments()
    {
        const wchar_t* commandLine = GetCommandLineW();
        if (*commandLine == L'"')
        {
            ++commandLine;
            while (*commandLine && *commandLine != L'"')
            {
                ++commandLine;
            }
            if (*commandLine)
            {
                ++commandLine;
            }
        }
        else
        {
            while (*commandLine && *commandLine != L' ')
            {
                ++commandLine;
            }
        }

        while (*commandLine == L' ')
        {
            ++commandLine;
        }

        return commandLine;
    }

    void CleanOldVersions(const fs::path& baseFolder, const std::wstring& keepFolderName)
    {
        std::error_code ec;
        for (const auto& entry : fs::directory_iterator(baseFolder, ec))
        {
            const std::wstring name = entry.path().filename().wstring();
            if (entry.is_directory(ec) && name.rfind(AppDirPrefix, 0) == 0 && name != keepFolderName)
            {
                // 旧版本可能仍有进程占用：删不掉就留着，下次启动再试。
                fs::remove_all(entry.path(), ec);
            }
        }
    }
}

int wmain(int, wchar_t* argv[])
{
    wchar_t modulePath[MAX_PATH * 4]{};
    GetModuleFileNameW(nullptr, modulePath, static_cast<DWORD>(std::size(modulePath)));
    const fs::path baseFolder = fs::path(modulePath).parent_path();

    fs::path target;
    std::error_code ec;
    const std::wstring configured = ReadExePathFromVersionIni(baseFolder / L"version.ini");
    if (!configured.empty())
    {
        const fs::path candidate = baseFolder / configured;
        if (fs::exists(candidate, ec))
        {
            target = candidate;
        }
    }

    if (target.empty())
    {
        target = FindNewestApp(baseFolder);
    }

    if (target.empty())
    {
        SetProcessDPIAware();
        MessageBoxW(nullptr, L"没有找到 Sparxie 程序文件（app-* 目录）。\r\n请重新解压完整的发布包。", L"Sparxie", MB_ICONERROR | MB_OK);
        return 1;
    }

    std::wstring commandLine = L"\"" + target.wstring() + L"\"";
    const std::wstring forwarded = ForwardedArguments();
    if (!forwarded.empty())
    {
        commandLine += L" " + forwarded;
    }

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    const std::wstring workingDirectory = target.parent_path().wstring();
    if (!CreateProcessW(target.c_str(), commandLine.data(), nullptr, nullptr, FALSE, 0, nullptr, workingDirectory.c_str(), &startup, &process))
    {
        SetProcessDPIAware();
        MessageBoxW(nullptr, (L"无法启动：\r\n" + target.wstring()).c_str(), L"Sparxie", MB_ICONERROR | MB_OK);
        return 1;
    }

    CloseHandle(process.hProcess);
    CloseHandle(process.hThread);

    CleanOldVersions(baseFolder, target.parent_path().filename().wstring());
    return 0;
}

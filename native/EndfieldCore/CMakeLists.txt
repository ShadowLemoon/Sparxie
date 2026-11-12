cmake_minimum_required(VERSION 3.20)
project(SparxieEndfieldCore LANGUAGES CXX)

add_library(EndfieldCore SHARED endfield_core.cpp endfield_decoy.cpp)
add_library(EndfieldRuntime SHARED endfield_runtime.cpp)

set_target_properties(EndfieldCore EndfieldRuntime PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED YES
  MSVC_RUNTIME_LIBRARY "MultiThreaded")

target_compile_definitions(EndfieldCore PRIVATE ENDFIELD_CORE_BUILD=1)
target_compile_definitions(EndfieldRuntime PRIVATE ENDFIELD_RUNTIME_BUILD=1)
target_compile_options(EndfieldCore PRIVATE /utf-8)
target_compile_options(EndfieldRuntime PRIVATE /utf-8)
# decoy parent: WinVerifyTrust, CryptQueryObject / CryptHashCertificate2, UuidCreate
target_link_libraries(EndfieldCore PRIVATE wintrust crypt32 bcrypt rpcrt4)

# Core tests: request validation and session guards; no game is started.
# Built with EndfieldCore; run: build\Release\EndfieldCoreTests.exe
add_executable(EndfieldCoreTests endfield_core_tests.cpp)
set_target_properties(EndfieldCoreTests PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED YES
  MSVC_RUNTIME_LIBRARY "MultiThreaded")
target_compile_options(EndfieldCoreTests PRIVATE /utf-8)
target_link_libraries(EndfieldCoreTests PRIVATE EndfieldCore)

# Runtime tests: a dummy Unity game (window, message loop, input type and frame rate it resets) beside stand-ins for GameAssembly.dll and UnityPlayer.dll.
# Built with EndfieldCore; run: build\Release\EndfieldRuntimeTests.exe
add_library(FakeGameAssembly SHARED fake_game_assembly.cpp)
add_library(FakeUnityPlayer SHARED fake_unity_player.cpp)
add_executable(FakeUnityEndfield WIN32 fake_unity_endfield.cpp)
add_executable(EndfieldRuntimeTests endfield_runtime_tests.cpp)
add_dependencies(EndfieldRuntimeTests FakeUnityEndfield FakeGameAssembly FakeUnityPlayer EndfieldRuntime FakeEndfield)
set_target_properties(FakeGameAssembly FakeUnityPlayer FakeUnityEndfield EndfieldRuntimeTests PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED YES
  MSVC_RUNTIME_LIBRARY "MultiThreaded")
target_compile_options(FakeGameAssembly PRIVATE /utf-8)
target_compile_options(FakeUnityPlayer PRIVATE /utf-8)
target_compile_options(FakeUnityEndfield PRIVATE /utf-8)
target_compile_options(EndfieldRuntimeTests PRIVATE /utf-8)
target_link_options(FakeUnityEndfield PRIVATE /ENTRY:wWinMainCRTStartup)
target_link_libraries(EndfieldRuntimeTests PRIVATE EndfieldCore)

# Decoy-parent tests: unit tests (fake Win32 calls) and end-to-end tests (dummy Endfield.exe) of the Games.exe parent launch.
# Built with EndfieldCore; run: build\Release\EndfieldDecoyTests.exe
add_executable(FakeEndfield WIN32 fake_endfield.cpp endfield_decoy.cpp)
add_executable(EndfieldDecoyTests endfield_decoy_tests.cpp endfield_decoy.cpp)
add_dependencies(EndfieldDecoyTests FakeEndfield)
set_target_properties(FakeEndfield EndfieldDecoyTests PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED YES
  MSVC_RUNTIME_LIBRARY "MultiThreaded")
target_compile_options(EndfieldDecoyTests PRIVATE /utf-8)
target_compile_options(FakeEndfield PRIVATE /utf-8)
target_link_libraries(FakeEndfield PRIVATE wintrust crypt32 bcrypt rpcrt4)
target_link_options(FakeEndfield PRIVATE /ENTRY:wWinMainCRTStartup)
target_link_libraries(EndfieldDecoyTests PRIVATE EndfieldCore wintrust crypt32 bcrypt rpcrt4)

install(TARGETS EndfieldCore EndfieldRuntime RUNTIME DESTINATION .)

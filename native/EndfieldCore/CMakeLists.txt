cmake_minimum_required(VERSION 3.20)
project(SparxieEndfieldCore LANGUAGES CXX)

add_library(EndfieldCore SHARED endfield_core.cpp)
add_library(EndfieldRuntime SHARED endfield_runtime.cpp)

set_target_properties(EndfieldCore EndfieldRuntime PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED YES
  MSVC_RUNTIME_LIBRARY "MultiThreaded")

target_compile_definitions(EndfieldCore PRIVATE ENDFIELD_CORE_BUILD=1)
target_compile_definitions(EndfieldRuntime PRIVATE ENDFIELD_RUNTIME_BUILD=1)
target_compile_options(EndfieldCore PRIVATE /utf-8)
target_compile_options(EndfieldRuntime PRIVATE /utf-8)

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
add_dependencies(EndfieldRuntimeTests FakeUnityEndfield FakeGameAssembly FakeUnityPlayer EndfieldRuntime)
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

install(TARGETS EndfieldCore EndfieldRuntime RUNTIME DESTINATION .)

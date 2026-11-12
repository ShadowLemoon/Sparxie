cmake_minimum_required(VERSION 3.20)
project(SparxieEndfieldCore LANGUAGES CXX)

add_library(EndfieldCore SHARED endfield_core.cpp)

set_target_properties(EndfieldCore PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED YES
  MSVC_RUNTIME_LIBRARY "MultiThreaded")

target_compile_definitions(EndfieldCore PRIVATE ENDFIELD_CORE_BUILD=1)
target_compile_options(EndfieldCore PRIVATE /utf-8)

# Core tests: request validation and session guards; no game is started.
# Built with EndfieldCore; run: build\Release\EndfieldCoreTests.exe
add_executable(EndfieldCoreTests endfield_core_tests.cpp)
set_target_properties(EndfieldCoreTests PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED YES
  MSVC_RUNTIME_LIBRARY "MultiThreaded")
target_compile_options(EndfieldCoreTests PRIVATE /utf-8)
target_link_libraries(EndfieldCoreTests PRIVATE EndfieldCore)

install(TARGETS EndfieldCore RUNTIME DESTINATION .)

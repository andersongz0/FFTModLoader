cmake_minimum_required(VERSION 3.24)
project(FFTModLoaderPrototype LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include(FetchContent)

FetchContent_Declare(
    minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    GIT_TAG v1.3.4
)
FetchContent_MakeAvailable(minhook)

FetchContent_Declare(
    nlohmann_json
    GIT_REPOSITORY https://github.com/nlohmann/json.git
    GIT_TAG v3.11.3
)
FetchContent_MakeAvailable(nlohmann_json)

add_executable(FFTModLoader
    src/Launcher/main.cpp
    src/Launcher/FFTModLoader.rc
)
add_executable(RuntimeLogRelayTests EXCLUDE_FROM_ALL tests/runtime_log_relay.cpp)
add_executable(ModLoadOrderTests EXCLUDE_FROM_ALL tests/mod_load_order.cpp)

target_include_directories(FFTModLoader PRIVATE
    src/Launcher
)

target_link_libraries(
    FFTModLoader
    PRIVATE
        nlohmann_json::nlohmann_json
)

target_compile_definitions(FFTModLoader PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

add_library(FFTModLoaderNative SHARED
    src/Native/dllmain.cpp
    src/Native/Logger.cpp
    src/Native/SignatureScanner.cpp
    src/Native/RuntimeConfig.cpp
    src/Native/ContentTypes.cpp
    src/Native/ContentResolver.cpp
    src/Native/LanguageHooks.cpp
    src/Native/FFTPackHooks.cpp
    src/Native/G2DHooks.cpp
    src/Native/JobDataAPI.cpp
    src/Native/JobCommandAPI.cpp
    src/Native/MesTextAPI.cpp
    src/Native/JobNeedLevelAPI.cpp
    src/Native/JobMenuExpansion.cpp
    src/Native/PackMountHooks.cpp
    src/Native/PathHook.cpp
)

target_include_directories(FFTModLoaderNative PRIVATE
    src/Native
)

target_link_libraries(
    FFTModLoaderNative
    PRIVATE
        minhook
        nlohmann_json::nlohmann_json
)

target_compile_definitions(FFTModLoaderNative PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

set_target_properties(FFTModLoaderNative PROPERTIES
    OUTPUT_NAME "FFTModLoader.Native"
)

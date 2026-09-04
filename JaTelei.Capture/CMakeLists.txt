cmake_minimum_required(VERSION 3.20)
project(JaTelei.Capture CXX)

set(CMAKE_CXX_STANDARD 20)   # C++20 for WinRT coroutines / co_await
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(JaTelei.Capture SHARED
    dxgi_capture.cpp
)

target_include_directories(JaTelei.Capture PRIVATE .)

# Windows 10+ (required for WGC, ID3D11Multithread, modern DXGI)
target_compile_definitions(JaTelei.Capture PRIVATE
    _WIN32_WINNT=0x0A00
    WINVER=0x0A00
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    # WinRT / C++/WinRT
    WINRT_LEAN_AND_MEAN
)

# C++/WinRT requires /await or /std:c++20 and these flags on MSVC
if(MSVC)
    target_compile_options(JaTelei.Capture PRIVATE
        /await:strict     # coroutine support
        /W3               # warnings
        /MP               # parallel compilation
        /EHsc             # exception handling
        /Zc:__cplusplus   # correct __cplusplus macro
    )
endif()

# Windows SDK / system libraries
target_link_libraries(JaTelei.Capture PRIVATE
    # D3D / DXGI
    d3d11
    dxgi
    dxguid

    # Media Foundation
    mfplat
    mf
    mfuuid
    evr

    # WASAPI / Audio
    ole32
    avrt
    winmm

    # WinRT / Windows.Graphics.Capture
    windowsapp     # links Windows.winmd umbrella import lib
    runtimeobject  # RoInitialize / WinRT activation

    # Codec API (ICodecAPI for bitrate control)
    wmcodecdspuuid
)

# DLL output to client folder
set_target_properties(JaTelei.Capture PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../JaTelei.Client"
    OUTPUT_NAME "JaTelei.Capture"
)

cmake_minimum_required(VERSION 3.20)
project(JaTelei.Capture CXX)

set(CMAKE_CXX_STANDARD 17)

add_library(JaTelei.Capture SHARED
    dxgi_capture.cpp
)

target_include_directories(JaTelei.Capture PRIVATE .)

# Garante Windows 10+ para ID3D11Multithread e outras APIs modernas
target_compile_definitions(JaTelei.Capture PRIVATE
    _WIN32_WINNT=0x0A00
    WINVER=0x0A00
)

# Windows SDK e Media Foundation
target_link_libraries(JaTelei.Capture PRIVATE
    d3d11
    dxgi
    dxguid
    mfplat
    mf
    mfuuid
    evr
)

# DLL vai direto para a pasta do cliente após build
set_target_properties(JaTelei.Capture PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../JaTelei.Client"
    OUTPUT_NAME "JaTelei.Capture"
)

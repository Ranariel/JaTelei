cmake_minimum_required(VERSION 3.20)
project(JaClipei.Capture CXX)

set(CMAKE_CXX_STANDARD 17)

add_library(JaClipei.Capture SHARED
    dxgi_capture.cpp
)

target_include_directories(JaClipei.Capture PRIVATE .)

# Linka com libs do Windows necessárias
target_link_libraries(JaClipei.Capture PRIVATE
    d3d11
    dxgi
    dxguid
)

# Coloca a DLL na pasta do cliente após build
set_target_properties(JaClipei.Capture PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../JaClipei.Client"
    OUTPUT_NAME "JaClipei.Capture"
)

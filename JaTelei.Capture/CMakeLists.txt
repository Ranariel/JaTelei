cmake_minimum_required(VERSION 3.20)
project(JaTelei.Capture CXX)

set(CMAKE_CXX_STANDARD 17)

add_library(JaTelei.Capture SHARED
    dxgi_capture.cpp
)

target_include_directories(JaTelei.Capture PRIVATE .)

# Linka com libs do Windows necessárias
target_link_libraries(JaTelei.Capture PRIVATE
    d3d11
    dxgi
    dxguid
)

# Coloca a DLL na pasta do cliente após build
set_target_properties(JaTelei.Capture PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../JaTelei.Client"
    OUTPUT_NAME "JaTelei.Capture"
)

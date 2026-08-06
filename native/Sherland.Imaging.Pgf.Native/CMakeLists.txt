cmake_minimum_required(VERSION 3.20)
project(SherlandImagingPgfNative CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(WIN32)
    add_definitions(-DWIN32)
endif()

add_library(SherlandImagingPgfNative SHARED
    shim.cpp
    libpgf/Decoder.cpp
    libpgf/Encoder.cpp
    libpgf/PGFimage.cpp
    libpgf/PGFstream.cpp
    libpgf/Subband.cpp
    libpgf/WaveletTransform.cpp
)

target_include_directories(SherlandImagingPgfNative PRIVATE libpgf)
target_compile_definitions(SherlandImagingPgfNative PRIVATE LIBPGF_DISABLE_OPENMP)

cmake_minimum_required(VERSION 3.22)
project(cavernize_proaudio LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(LIBUSB_ROOT "" CACHE PATH "Optional path to unpacked libusb-1.0 source tree for Path B UAC2")
set(CAVERNIZE_LIBUSB_DIR "" CACHE PATH "Alias for LIBUSB_ROOT")
if(NOT LIBUSB_ROOT AND CAVERNIZE_LIBUSB_DIR)
    set(LIBUSB_ROOT "${CAVERNIZE_LIBUSB_DIR}")
endif()

add_library(cavernize_proaudio SHARED
    cavernize_uac2_core.c
    cavernize_path_a_aaudio.cpp
    cavernize_path_b_libusb.cpp
    cavernize_proaudio_jni.cpp
)

target_compile_options(cavernize_proaudio PRIVATE -O3 -Wall -Wextra -fvisibility=default)

if(ANDROID)
    target_link_options(cavernize_proaudio PRIVATE
        -Wl,-z,max-page-size=16384
        -Wl,-z,common-page-size=16384
    )
    find_library(AAUDIO_LIB aaudio REQUIRED)
    find_library(LOG_LIB log REQUIRED)
    target_link_libraries(cavernize_proaudio PRIVATE ${AAUDIO_LIB} ${LOG_LIB})
endif()

if(LIBUSB_ROOT AND EXISTS "${LIBUSB_ROOT}/libusb/core.c")
    message(STATUS "Building embedded unrooted Android libusb-1.0 from ${LIBUSB_ROOT}")
    add_library(usb1_static STATIC
        "${LIBUSB_ROOT}/libusb/core.c"
        "${LIBUSB_ROOT}/libusb/descriptor.c"
        "${LIBUSB_ROOT}/libusb/hotplug.c"
        "${LIBUSB_ROOT}/libusb/io.c"
        "${LIBUSB_ROOT}/libusb/strerror.c"
        "${LIBUSB_ROOT}/libusb/sync.c"
        "${LIBUSB_ROOT}/libusb/os/events_posix.c"
        "${LIBUSB_ROOT}/libusb/os/threads_posix.c"
        "${LIBUSB_ROOT}/libusb/os/linux_usbfs.c"
        "${LIBUSB_ROOT}/libusb/os/linux_netlink.c"
    )
    target_include_directories(usb1_static PUBLIC
        "${LIBUSB_ROOT}"
        "${LIBUSB_ROOT}/libusb"
        "${LIBUSB_ROOT}/android"
    )
    target_compile_definitions(usb1_static PRIVATE
        HAVE_CONFIG_H=1
    )
    target_compile_options(usb1_static PRIVATE -O3 -Wno-unused-parameter)
    if(ANDROID)
        target_link_libraries(usb1_static PRIVATE ${LOG_LIB})
    endif()
    target_compile_definitions(cavernize_proaudio PRIVATE CAVERNIZE_HAVE_LIBUSB=1)
    target_include_directories(cavernize_proaudio PRIVATE "${LIBUSB_ROOT}" "${LIBUSB_ROOT}/libusb")
    target_link_libraries(cavernize_proaudio PRIVATE usb1_static)
endif()

cmake_minimum_required(VERSION 3.22)
project(cavernize_proaudio LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(LIBUSB_ROOT "" CACHE PATH "Optional path to unpacked libusb-1.0 source tree for Path B UAC2")

add_library(cavernize_proaudio SHARED
    cavernize_uac2_core.c
    cavernize_path_a_aaudio.cpp
    cavernize_path_b_libusb.cpp
    cavernize_proaudio_jni.cpp
)

target_compile_options(cavernize_proaudio PRIVATE -O3 -Wall -Wextra -fvisibility=default)

if(ANDROID)
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
        PLATFORM_POSIX=1
        OS_LINUX=1
        HAVE_SYS_TIME_H=1
        HAVE_CLOCK_GETTIME=1
        HAVE_EVENTFD=1
        HAVE_TIMERFD=1
        HAVE_PIPE2=1
        HAVE_POLL_NFDS_TYPE=1
        DEFAULT_VISIBILITY=__attribute__\(\(visibility\(\"hidden\"\)\)\)
        PRINTF_FORMAT\(a,b\)=__attribute__\(\(__format__\(__printf__,a,b\)\)\)
    )
    if(ANDROID)
        target_link_libraries(usb1_static PRIVATE ${LOG_LIB})
    endif()
    target_compile_definitions(cavernize_proaudio PRIVATE CAVERNIZE_HAVE_LIBUSB=1)
    target_include_directories(cavernize_proaudio PRIVATE "${LIBUSB_ROOT}" "${LIBUSB_ROOT}/libusb")
    target_link_libraries(cavernize_proaudio PRIVATE usb1_static)
endif()

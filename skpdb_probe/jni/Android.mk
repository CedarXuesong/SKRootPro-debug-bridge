# SKPDB native build.
#
# One ndk-build invocation produces the wireless module:
#   libs/<abi>/libskpdb_probe.so   wireless module (TCP transport)
#
# src/common is transport independent and is compiled once into
# libskpdb_core.a, which the shared module links against.

LOCAL_PATH := $(call my-dir)
SRC := $(LOCAL_PATH)/../src

SKROOT_SDK ?=
ifeq ($(strip $(SKROOT_SDK)),)
$(error Pass SKROOT_SDK=/path/to/authorized/sdk)
endif
SKROOT_KIT := $(SKROOT_SDK)/lib/libkernel_module_kit_static.a

COMMON_CPPFLAGS := -std=c++20 -fPIC -fvisibility=hidden -fno-stack-protector
COMMON_CPPFLAGS += -DSKPDB_EXPERIMENTAL_TOUCH_GUARD=$(if $(SKPDB_EXPERIMENTAL_TOUCH_GUARD),$(SKPDB_EXPERIMENTAL_TOUCH_GUARD),0)
COMMON_LDFLAGS := -Wl,-O2 -Wl,--gc-sections -Wl,-s

# The SKRoot kit ships outside the project tree, so it is declared as a
# prebuilt static library instead of being passed through LOCAL_LDFLAGS. This
# keeps the link order correct and makes the kit a real dependency.
include $(CLEAR_VARS)
LOCAL_MODULE := kernel_module_kit
LOCAL_SRC_FILES := $(SKROOT_KIT)
include $(PREBUILT_STATIC_LIBRARY)

# ---------------------------------------------------------------------------
# Protocol core (framing, authentication, channel operations, session loop)
# ---------------------------------------------------------------------------
include $(CLEAR_VARS)
LOCAL_MODULE := skpdb_core
LOCAL_SRC_FILES := \
    ../src/service/admin.cpp \
    ../src/common/auth.cpp \
    ../src/common/channel.cpp \
    ../src/display/cursor.cpp \
    ../src/display/cursor_surface.cpp \
    ../src/input/touch.cpp ../src/input/touch_guard.cpp \
    ../src/input/gyro.cpp \
    ../src/service/config.cpp \
    ../src/common/crypto.cpp \
    ../src/common/files.cpp \
    ../src/common/frame.cpp \
    ../src/service/knock.cpp \
    ../src/memory/kernel_channel.cpp ../src/memory/memread.cpp ../src/memory/memwrite.cpp ../src/memory/memscan.cpp \
    ../src/common/process.cpp \
    ../src/common/session.cpp \
    ../src/common/stream.cpp ../src/memory/symtab.cpp
# memread.cpp enters the kernel through the SKRoot kit, so the protocol core
# needs the SDK headers even though the other units are SDK independent.
LOCAL_C_INCLUDES := $(SRC) $(SKROOT_SDK)/include
LOCAL_CPPFLAGS += $(COMMON_CPPFLAGS)
include $(BUILD_STATIC_LIBRARY)

# ---------------------------------------------------------------------------
# Wireless module: TCP listener
# ---------------------------------------------------------------------------
include $(CLEAR_VARS)
LOCAL_MODULE := skpdb_probe
LOCAL_SRC_FILES := \
    ../src/wireless/entry.cpp \
    ../src/wireless/server.cpp \
    ../src/wireless/socket_stream.cpp \
    ../src/wireless/webui.cpp
LOCAL_SRC_FILES += ../src/wireless/diagnostics.cpp
LOCAL_C_INCLUDES := $(SKROOT_SDK)/include $(SRC) $(LOCAL_PATH)/..
LOCAL_CPPFLAGS += $(COMMON_CPPFLAGS)
LOCAL_STATIC_LIBRARIES := skpdb_core kernel_module_kit
LOCAL_LDFLAGS += $(COMMON_LDFLAGS)
LOCAL_LDLIBS += -ldl -lz
include $(BUILD_SHARED_LIBRARY)

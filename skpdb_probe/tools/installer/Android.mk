LOCAL_PATH := $(call my-dir)
include $(CLEAR_VARS)
LOCAL_MODULE := skpdb_install
LOCAL_SRC_FILES := install_once_lib.cpp
KERNEL_MODULE_KIT := $(SKROOT_SDK)
ifeq ($(strip $(KERNEL_MODULE_KIT)),)
$(error Pass SKROOT_SDK=/path/to/authorized/sdk)
endif
LOCAL_C_INCLUDES += $(KERNEL_MODULE_KIT)/include
LOCAL_LDFLAGS += -Wl,--start-group $(KERNEL_MODULE_KIT)/lib/libkernel_module_kit_static.a -Wl,--end-group
LOCAL_CPPFLAGS += -std=c++20 -fPIC -fvisibility=hidden -fno-stack-protector
LOCAL_LDFLAGS += -Wl,-O2 -Wl,--gc-sections -Wl,-s
LOCAL_LDLIBS += -lz
include $(BUILD_SHARED_LIBRARY)

# Device-side tools

These programs live next to the module but are not part of it. They are built
the same way as the module (`ndk-build` with the NDK used for `libskpdb_probe`),
and pushed to `/data/local/tmp/`.

| Tool | Build | Use |
| --- | --- | --- |
| `loader/loader.cpp` | Built by `../build.ps1` to `libs/arm64-v8a/skpdb_loader`; uses the NDK C driver with `-ldl` and dynamic Bionic, with no libc++ dependency. | Reads the root key from stdin, `dlopen`s the module, then calls the supplied entry point: `loader module.so module_dir __skroot_module_main_18fdf393885363712349ef3de5411bd5`. Keep the input channel open until the loader exits; ADB forwarding does not preserve an early TCP half-close reliably. |
| `installer/` | `ndk-build APP_BUILD_SCRIPT=tools/installer/Android.mk` | One-shot `libskpdb_install.so` that hands the module zip to the SKRoot installer (`skpdb_install_once`). |
| `uninstaller/` | `ndk-build APP_BUILD_SCRIPT=tools/uninstaller/Android.mk` | One-shot `libskpdb_uninstall.so` that calls `skroot_env::uninstall_module` for the given id32. |
| `lister/` | `ndk-build APP_BUILD_SCRIPT=tools/lister/Android.mk` | Prints `name \| version \| state \| id32` for every installed module (`skpdb_list_modules`). |

The installer and uninstaller only work when they are driven from the module's
own SELinux context (a running module session), because the SKRoot kernel
channel is not reachable from `adb shell su` (`ERR_MODULE_NOT_KADDR`).

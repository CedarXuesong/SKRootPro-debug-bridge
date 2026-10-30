#include <dlfcn.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
int main(int argc, char** argv) {
    if (argc != 4 && argc != 5) return 64;
    char* key = nullptr;
    size_t capacity = 0;
    const ssize_t length = getline(&key, &capacity, stdin);
    if (length <= 0) { std::fputs("loader: missing key on stdin\n", stderr); return 65; }
    key[std::strcspn(key, "\r\n")] = '\0';
    void* lib = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!lib) { std::fprintf(stderr, "loader dlopen: %s\n", dlerror()); return 66; }
    using Fn = int(*)(const char*, const char*);
    auto fn = reinterpret_cast<Fn>(dlsym(lib, argv[3]));
    if (!fn) { std::fprintf(stderr, "loader dlsym: %s\n", dlerror()); return 67; }
    const int result = fn(key, argv[2]);
    // Some SDK helpers redirect their standard streams. A separate result
    // file makes completion observable without relying on inherited stdio.
    if (argc == 5) {
        FILE* status = std::fopen(argv[4], "w");
        if (status) { std::fprintf(status, "result=%d\n", result); std::fclose(status); }
    }
    std::memset(key, 0, capacity);
    std::free(key);
    std::fprintf(stderr, "loader: entry returned %d\n", result);
    return result;
}

cmake_minimum_required(VERSION 3.20)
project(skpdb LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Self-contained executable: no VC++ redistributable required.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

if(MSVC)
    add_compile_options(/W4 /permissive- /utf-8)
    add_compile_definitions(WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS)
endif()

# Wire protocol, knock packet and TCP transport. Everything here is reusable
# from a test without pulling in the command implementations.
add_library(skpdb_core STATIC
    src/knock.cpp
    src/protocol.cpp
    src/tcp.cpp
)
target_include_directories(skpdb_core PUBLIC src)
target_link_libraries(skpdb_core PUBLIC bcrypt ws2_32)

# The client: option parsing and dispatch in main.cpp, one translation unit per
# command group next to it.
add_executable(skpdb
    src/main.cpp
    src/commands_session.cpp
    src/commands_memory.cpp
    src/commands_admin.cpp
)
target_link_libraries(skpdb PRIVATE skpdb_core)

enable_testing()
add_executable(skpdb_tests tests/protocol_test.cpp)
target_link_libraries(skpdb_tests PRIVATE skpdb_core)
add_test(NAME protocol COMMAND skpdb_tests)

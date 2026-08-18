/**
 * @file mock_log.cpp
 * @brief Stub logging function for mock_server builds
 *
 * In the real CS2 plugin, Log() is defined in server_plugin.cpp and uses HL2SDK logging.
 * For the mock_server, we provide a simple stdout implementation.
 */

#include <cstdarg>
#include <cstdio>
#include <iostream>

void Log(const char *msg, ...)
{
    va_list args;
    va_start(args, msg);

    std::cout << "[LOG] ";
    vprintf(msg, args);
    std::cout << std::endl;

    va_end(args);
}

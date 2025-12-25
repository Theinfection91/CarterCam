#include <iostream>
#include <vector>
#include <cstdint>

#ifdef _WIN32
#include <windows.h>
#include <fcntl.h>
#include <io.h>
#endif

int main() {
#ifdef _WIN32
    // Set stdin to binary mode to prevent newline translation and EOF on 0x1A
    _setmode(_fileno(stdin), _O_BINARY);
#endif

    std::ios::sync_with_stdio(false);
    std::cin.tie(nullptr);

    std::cerr << "Ingestor: Starting, connecting to encoder pipe...\n";

    // Connect to named pipe (encoder must be listening)
    HANDLE pipe = CreateFileA(
        "\\\\.\\pipe\\CarterCamFrames",
        GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        0,
        nullptr
    );

    if (pipe == INVALID_HANDLE_VALUE) {
        std::cerr << "Ingestor: Failed to connect to pipe. Is Encoder running?\n";
        return 1;
    }

    std::cerr << "Ingestor: Connected to encoder pipe.\n";

    while (true) {
        uint32_t len = 0;
        std::cin.read(reinterpret_cast<char*>(&len), sizeof(len));

        if (!std::cin) {
            if (std::cin.eof()) {
                std::cerr << "Ingestor: stdin EOF, exiting.\n";
                break;
            }
            std::cin.clear();
            continue;
        }

        // Sanity check to prevent huge allocations
        if (len == 0 || len > 50 * 1024 * 1024) {
            std::cerr << "Ingestor: Invalid frame length: " << len << ", skipping.\n";
            continue;
        }

        std::vector<char> frame(len);
        std::cin.read(frame.data(), len);
        if (!std::cin) {
            std::cin.clear();
            continue;
        }

        // Write length prefix + frame to pipe
        DWORD written;
        WriteFile(pipe, &len, sizeof(len), &written, nullptr);
        WriteFile(pipe, frame.data(), len, &written, nullptr);

        //std::cerr << "Ingestor: Forwarded frame (" << len << " bytes)\n";
    }

    CloseHandle(pipe);
    return 0;
}

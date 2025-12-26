#include <iostream>
#include <vector>
#include <cstdint>
#include <cstdio>
#include <cstring>

#ifdef _WIN32
#include <windows.h>
#endif

int main() {
    std::cerr << "Encoder: Creating named pipe...\n";

    // Create named pipe (server side)
    HANDLE pipe = CreateNamedPipeA(
        "\\\\.\\pipe\\CarterCamFrames",
        PIPE_ACCESS_INBOUND,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        1,          // max instances
        0,          // out buffer
        1024 * 1024, // in buffer (1MB)
        0,
        nullptr
    );

    if (pipe == INVALID_HANDLE_VALUE) {
        std::cerr << "Encoder: Failed to create pipe.\n";
        return 1;
    }

    std::cerr << "Encoder: Waiting for Ingestor to connect...\n";

    if (!ConnectNamedPipe(pipe, nullptr)) {
        std::cerr << "Encoder: Failed to connect client.\n";
        CloseHandle(pipe);
        return 1;
    }

    std::cerr << "Encoder: Ingestor connected. Starting FFmpeg...\n";

    // Use CreateProcess for better control and error handling
    SECURITY_ATTRIBUTES sa = { sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE };
    HANDLE ffmpegStdinRead, ffmpegStdinWrite;

    if (!CreatePipe(&ffmpegStdinRead, &ffmpegStdinWrite, &sa, 0)) {
        std::cerr << "Encoder: Failed to create FFmpeg stdin pipe.\n";
        CloseHandle(pipe);
        return 1;
    }

    SetHandleInformation(ffmpegStdinWrite, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOA si = { sizeof(STARTUPINFOA) };
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = ffmpegStdinRead;
    si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    si.hStdError = GetStdHandle(STD_ERROR_HANDLE);

    PROCESS_INFORMATION pi = {};

    // Use CUDA for JPEG decoding + NVENC for encoding
    // -threads 0: auto-detect threads for faster decoding
    // -hwaccel cuda: use GPU for decoding if possible
    // -use_wallclock_as_timestamps 1: real-time timestamps
    char cmdLine[1024];
    snprintf(cmdLine, sizeof(cmdLine),
        "C:\\ffmpeg\\bin\\ffmpeg.exe -hide_banner -loglevel info "
        "-threads 4 "
        "-use_wallclock_as_timestamps 1 "
        "-f mjpeg -i pipe:0 "
        "-c:v h264_nvenc -preset p1 -tune ll -rc cbr -b:v 2M "
        "-f segment -segment_time 10 -reset_timestamps 1 "
        "\"C:\\Chase\\output\\output_%%03d.mp4\""
    );

    std::cerr << "Encoder: Running command: " << cmdLine << "\n";

    BOOL success = CreateProcessA(
        nullptr,
        cmdLine,
        nullptr,
        nullptr,
        TRUE,       // inherit handles
        0,
        nullptr,
        nullptr,
        &si,
        &pi
    );

    CloseHandle(ffmpegStdinRead); // Close read end in parent

    if (!success) {
        DWORD err = GetLastError();
        std::cerr << "Encoder: Failed to start FFmpeg. Error code: " << err << "\n";
        CloseHandle(pipe);
        return 1;
    }

    std::cerr << "Encoder: FFmpeg started successfully (PID: " << pi.dwProcessId << ")\n";

    while (true) {
        uint32_t len = 0;
        DWORD bytesRead;

        if (!ReadFile(pipe, &len, sizeof(len), &bytesRead, nullptr) || bytesRead == 0) {
            std::cerr << "Encoder: Pipe closed.\n";
            break;
        }

        if (len == 0 || len > 50 * 1024 * 1024) continue;

        std::vector<char> frame(len);
        DWORD totalRead = 0;
        while (totalRead < len) {
            DWORD chunkRead;
            if (!ReadFile(pipe, frame.data() + totalRead, len - totalRead, &chunkRead, nullptr)) break;
            totalRead += chunkRead;
        }

        // Write JPEG to FFmpeg
        DWORD written;
        if (!WriteFile(ffmpegStdinWrite, frame.data(), len, &written, nullptr)) {
            std::cerr << "Encoder: Failed to write to FFmpeg.\n";
            break;
        }
    }

    CloseHandle(ffmpegStdinWrite);
    WaitForSingleObject(pi.hProcess, 5000);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    CloseHandle(pipe);
    return 0;
}

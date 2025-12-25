#include <iostream>
#include <vector>
#include <cstdint>

int main() {
    std::ios::sync_with_stdio(true);
    std::cout.setf(std::ios::unitbuf); // automatic flush

    while (true) {
        uint32_t len;
        std::cin.read(reinterpret_cast<char*>(&len), sizeof(len));
        if (!std::cin) continue;

        // Optional sanity check to prevent huge allocations
        if (len > 50 * 1024 * 1024) continue;

        std::vector<char> frame(len);
        std::cin.read(frame.data(), len);
        if (!std::cin) continue;

        std::cout << "C++: Received frame of length: " << len << "\n";
    }

    return 0;
}

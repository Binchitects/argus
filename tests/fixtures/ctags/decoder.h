#pragma once
namespace eal {
struct DecoderConfig {
    int max_frames;
};
int DecodeFrame(const char* buf, int len);
namespace detail {
int ScratchBuffer(int n);
}
}

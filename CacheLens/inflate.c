#include "inflate.h"
#include <zlib.h>
#include <brotli/decode.h>
long cache_brotli(const uint8_t *src, size_t count, uint8_t *dest, size_t capacity) {
    size_t length = capacity;
    return BrotliDecoderDecompress(count, src, &length, dest) == BROTLI_DECODER_RESULT_SUCCESS ? (long)length : -1;
}
long cache_inflate(const uint8_t *src, size_t count, uint8_t *dest, size_t capacity, int window) {
    z_stream s = {0};
    s.next_in = (Bytef *)src; s.avail_in = (uInt)count;
    s.next_out = dest; s.avail_out = (uInt)capacity;
    if (inflateInit2(&s, window) != Z_OK) return -1;
    int result = inflate(&s, Z_FINISH);
    long length = result == Z_STREAM_END ? (long)s.total_out : -1;
    inflateEnd(&s);
    return length;
}

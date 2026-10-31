#include <stdint.h>
#include <stddef.h>
long cache_inflate(const uint8_t *src, size_t count, uint8_t *dest, size_t capacity, int window);
long cache_brotli(const uint8_t *src, size_t count, uint8_t *dest, size_t capacity);

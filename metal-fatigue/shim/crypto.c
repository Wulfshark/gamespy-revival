/* mfnet crypto: X25519 key agreement and ChaCha20-Poly1305 (RFC 7748, RFC 8439).
 * Freestanding (no C runtime). X25519 follows TweetNaCl's public-domain design. */
#include "crypto.h"

typedef long long i64;
typedef unsigned long long u64;
typedef i64 gf[16];

static const gf K121665 = {0xDB41, 1};

static void car(gf o) {
    for (int i = 0; i < 16; i++) {
        o[i] += (i64)1 << 16;
        i64 c = o[i] >> 16;
        if (i < 15) o[i + 1] += c - 1; else o[0] += 38 * (c - 1);
        o[i] -= c << 16;
    }
}
static void sel(gf p, gf q, int b) {
    i64 c = ~((i64)b - 1);
    for (int i = 0; i < 16; i++) { i64 t = c & (p[i] ^ q[i]); p[i] ^= t; q[i] ^= t; }
}
static void pack(u8 *o, const gf n) {
    gf m, t; int b;
    for (int i = 0; i < 16; i++) t[i] = n[i];
    car(t); car(t); car(t);
    for (int j = 0; j < 2; j++) {
        m[0] = t[0] - 0xffed;
        for (int i = 1; i < 15; i++) { m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1); m[i - 1] &= 0xffff; }
        m[15] = t[15] - 0x7fff - ((m[14] >> 16) & 1);
        b = (int)((m[15] >> 16) & 1);
        m[14] &= 0xffff;
        sel(t, m, 1 - b);
    }
    for (int i = 0; i < 16; i++) { o[2 * i] = (u8)(t[i] & 0xff); o[2 * i + 1] = (u8)(t[i] >> 8); }
}
static void unpack(gf o, const u8 *n) {
    for (int i = 0; i < 16; i++) o[i] = n[2 * i] + ((i64)n[2 * i + 1] << 8);
    o[15] &= 0x7fff;
}
static void A(gf o, const gf a, const gf b) { for (int i = 0; i < 16; i++) o[i] = a[i] + b[i]; }
static void Z(gf o, const gf a, const gf b) { for (int i = 0; i < 16; i++) o[i] = a[i] - b[i]; }
static void M(gf o, const gf a, const gf b) {
    i64 t[31];
    for (int i = 0; i < 31; i++) t[i] = 0;
    for (int i = 0; i < 16; i++) for (int j = 0; j < 16; j++) t[i + j] += a[i] * b[j];
    for (int i = 0; i < 15; i++) t[i] += 38 * t[i + 16];
    for (int i = 0; i < 16; i++) o[i] = t[i];
    car(o); car(o);
}
static void S(gf o, const gf a) { M(o, a, a); }
static void inv(gf o, const gf i) {
    gf c;
    for (int a = 0; a < 16; a++) c[a] = i[a];
    for (int a = 253; a >= 0; a--) { S(c, c); if (a != 2 && a != 4) M(c, c, i); }
    for (int a = 0; a < 16; a++) o[a] = c[a];
}

void x25519(u8 out[32], const u8 scalar[32], const u8 point[32]) {
    u8 z[32]; gf x, a, b, c, d, e, f;
    for (int i = 0; i < 32; i++) z[i] = scalar[i];
    z[31] = (u8)((z[31] & 127) | 64); z[0] &= 248;
    unpack(x, point);
    for (int i = 0; i < 16; i++) { b[i] = x[i]; d[i] = a[i] = c[i] = 0; }
    a[0] = d[0] = 1;
    for (int i = 254; i >= 0; --i) {
        int r = (z[i >> 3] >> (i & 7)) & 1;
        sel(a, b, r); sel(c, d, r);
        A(e, a, c); Z(a, a, c); A(c, b, d); Z(b, b, d);
        S(d, e); S(f, a); M(a, c, a); M(c, b, e);
        A(e, a, c); Z(a, a, c); S(b, a); Z(c, d, f);
        M(a, c, K121665); A(a, a, d); M(c, c, a); M(a, d, f); M(d, b, x); S(b, e);
        sel(a, b, r); sel(c, d, r);
    }
    inv(c, c); M(a, a, c); pack(out, a);
}

void x25519_base(u8 out[32], const u8 scalar[32]) {
    static const u8 nine[32] = {9};
    x25519(out, scalar, nine);
}

/* ---- ChaCha20 ---- */
static u32 ld32(const u8 *p) { return p[0] | (u32)p[1] << 8 | (u32)p[2] << 16 | (u32)p[3] << 24; }
static void st32(u8 *p, u32 v) { p[0] = (u8)v; p[1] = (u8)(v >> 8); p[2] = (u8)(v >> 16); p[3] = (u8)(v >> 24); }
#define ROTL(v, n) (((v) << (n)) | ((v) >> (32 - (n))))
#define QR(a, b, c, d) a += b; d ^= a; d = ROTL(d, 16); c += d; b ^= c; b = ROTL(b, 12); \
                       a += b; d ^= a; d = ROTL(d, 8);  c += d; b ^= c; b = ROTL(b, 7);

void chacha20_block(u8 out[64], const u8 key[32], u32 counter, const u8 nonce[12]) {
    u32 s[16], x[16];
    s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
    for (int i = 0; i < 8; i++) s[4 + i] = ld32(key + 4 * i);
    s[12] = counter; s[13] = ld32(nonce); s[14] = ld32(nonce + 4); s[15] = ld32(nonce + 8);
    for (int i = 0; i < 16; i++) x[i] = s[i];
    for (int i = 0; i < 10; i++) {
        QR(x[0], x[4], x[8], x[12]) QR(x[1], x[5], x[9], x[13]) QR(x[2], x[6], x[10], x[14]) QR(x[3], x[7], x[11], x[15])
        QR(x[0], x[5], x[10], x[15]) QR(x[1], x[6], x[11], x[12]) QR(x[2], x[7], x[8], x[13]) QR(x[3], x[4], x[9], x[14])
    }
    for (int i = 0; i < 16; i++) st32(out + 4 * i, x[i] + s[i]);
}

static void chacha20_xor(u8 *data, u32 len, const u8 key[32], u32 counter, const u8 nonce[12]) {
    u8 ks[64];
    for (u32 o = 0; o < len; o += 64, counter++) {
        chacha20_block(ks, key, counter, nonce);
        u32 n = len - o < 64 ? len - o : 64;
        for (u32 i = 0; i < n; i++) data[o + i] ^= ks[i];
    }
}

/* ---- Poly1305 (32-bit limbs) ---- */
typedef struct { u32 r[5], h[5], pad[4]; } Poly;

static void poly_init(Poly *p, const u8 key[32]) {
    p->r[0] = (ld32(key + 0)) & 0x3ffffff;
    p->r[1] = (ld32(key + 3) >> 2) & 0x3ffff03;
    p->r[2] = (ld32(key + 6) >> 4) & 0x3ffc0ff;
    p->r[3] = (ld32(key + 9) >> 6) & 0x3f03fff;
    p->r[4] = (ld32(key + 12) >> 8) & 0x00fffff;
    for (int i = 0; i < 5; i++) p->h[i] = 0;
    for (int i = 0; i < 4; i++) p->pad[i] = ld32(key + 16 + 4 * i);
}

static void poly_block(Poly *p, const u8 m[16], u32 hibit) {
    u32 r0 = p->r[0], r1 = p->r[1], r2 = p->r[2], r3 = p->r[3], r4 = p->r[4];
    u32 s1 = r1 * 5, s2 = r2 * 5, s3 = r3 * 5, s4 = r4 * 5;
    u32 h0 = p->h[0], h1 = p->h[1], h2 = p->h[2], h3 = p->h[3], h4 = p->h[4];
    h0 += (ld32(m + 0)) & 0x3ffffff;
    h1 += (ld32(m + 3) >> 2) & 0x3ffffff;
    h2 += (ld32(m + 6) >> 4) & 0x3ffffff;
    h3 += (ld32(m + 9) >> 6) & 0x3ffffff;
    h4 += (ld32(m + 12) >> 8) | hibit;
    u64 d0 = (u64)h0 * r0 + (u64)h1 * s4 + (u64)h2 * s3 + (u64)h3 * s2 + (u64)h4 * s1;
    u64 d1 = (u64)h0 * r1 + (u64)h1 * r0 + (u64)h2 * s4 + (u64)h3 * s3 + (u64)h4 * s2;
    u64 d2 = (u64)h0 * r2 + (u64)h1 * r1 + (u64)h2 * r0 + (u64)h3 * s4 + (u64)h4 * s3;
    u64 d3 = (u64)h0 * r3 + (u64)h1 * r2 + (u64)h2 * r1 + (u64)h3 * r0 + (u64)h4 * s4;
    u64 d4 = (u64)h0 * r4 + (u64)h1 * r3 + (u64)h2 * r2 + (u64)h3 * r1 + (u64)h4 * r0;
    u32 c;
    c = (u32)(d0 >> 26); h0 = (u32)d0 & 0x3ffffff; d1 += c;
    c = (u32)(d1 >> 26); h1 = (u32)d1 & 0x3ffffff; d2 += c;
    c = (u32)(d2 >> 26); h2 = (u32)d2 & 0x3ffffff; d3 += c;
    c = (u32)(d3 >> 26); h3 = (u32)d3 & 0x3ffffff; d4 += c;
    c = (u32)(d4 >> 26); h4 = (u32)d4 & 0x3ffffff;
    h0 += c * 5; c = h0 >> 26; h0 &= 0x3ffffff; h1 += c;
    p->h[0] = h0; p->h[1] = h1; p->h[2] = h2; p->h[3] = h3; p->h[4] = h4;
}

/* MACs data padded to 16 bytes (RFC 8439's AEAD layout pads each part) */
static void poly_padded(Poly *p, const u8 *m, u32 len) {
    while (len >= 16) { poly_block(p, m, 1u << 24); m += 16; len -= 16; }
    if (len) { u8 b[16]; for (u32 i = 0; i < 16; i++) b[i] = i < len ? m[i] : 0; poly_block(p, b, 1u << 24); }
}

static void poly_finish(Poly *p, u8 tag[16]) {
    u32 h0 = p->h[0], h1 = p->h[1], h2 = p->h[2], h3 = p->h[3], h4 = p->h[4], c;
    c = h1 >> 26; h1 &= 0x3ffffff; h2 += c;
    c = h2 >> 26; h2 &= 0x3ffffff; h3 += c;
    c = h3 >> 26; h3 &= 0x3ffffff; h4 += c;
    c = h4 >> 26; h4 &= 0x3ffffff; h0 += c * 5;
    c = h0 >> 26; h0 &= 0x3ffffff; h1 += c;
    u32 g0 = h0 + 5; c = g0 >> 26; g0 &= 0x3ffffff;
    u32 g1 = h1 + c; c = g1 >> 26; g1 &= 0x3ffffff;
    u32 g2 = h2 + c; c = g2 >> 26; g2 &= 0x3ffffff;
    u32 g3 = h3 + c; c = g3 >> 26; g3 &= 0x3ffffff;
    u32 g4 = h4 + c - (1u << 26);
    u32 mask = (g4 >> 31) - 1;
    g0 &= mask; g1 &= mask; g2 &= mask; g3 &= mask; g4 &= mask;
    mask = ~mask;
    h0 = (h0 & mask) | g0; h1 = (h1 & mask) | g1; h2 = (h2 & mask) | g2; h3 = (h3 & mask) | g3; h4 = (h4 & mask) | g4;
    h0 = h0 | (h1 << 26);
    h1 = (h1 >> 6) | (h2 << 20);
    h2 = (h2 >> 12) | (h3 << 14);
    h3 = (h3 >> 18) | (h4 << 8);
    u64 f;
    f = (u64)h0 + p->pad[0];             st32(tag + 0, (u32)f);
    f = (u64)h1 + p->pad[1] + (f >> 32); st32(tag + 4, (u32)f);
    f = (u64)h2 + p->pad[2] + (f >> 32); st32(tag + 8, (u32)f);
    f = (u64)h3 + p->pad[3] + (f >> 32); st32(tag + 12, (u32)f);
}

static void aead_tag(u8 tag[16], const u8 key[32], const u8 nonce[12], const u8 *ct, u32 len) {
    u8 block[64]; Poly p; u8 lens[16];
    chacha20_block(block, key, 0, nonce);
    poly_init(&p, block);
    poly_padded(&p, ct, len);
    for (int i = 0; i < 16; i++) lens[i] = 0;      /* no associated data */
    st32(lens + 8, len);
    poly_block(&p, lens, 1u << 24);
    poly_finish(&p, tag);
}

void aead_seal(u8 *data, u32 len, u8 tag[16], const u8 key[32], const u8 nonce[12]) {
    chacha20_xor(data, len, key, 1, nonce);
    aead_tag(tag, key, nonce, data, len);
}

int aead_open(u8 *data, u32 len, const u8 tag[16], const u8 key[32], const u8 nonce[12]) {
    u8 t[16]; u32 diff = 0;
    aead_tag(t, key, nonce, data, len);
    for (int i = 0; i < 16; i++) diff |= (u32)(t[i] ^ tag[i]);
    if (diff) return -1;
    chacha20_xor(data, len, key, 1, nonce);
    return 0;
}

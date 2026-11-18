#ifndef MFNET_CRYPTO_H
#define MFNET_CRYPTO_H
#include "mfnet.h"
void x25519(u8 out[32], const u8 scalar[32], const u8 point[32]);
void x25519_base(u8 out[32], const u8 scalar[32]);
void chacha20_block(u8 out[64], const u8 key[32], u32 counter, const u8 nonce[12]);
void aead_seal(u8 *data, u32 len, u8 tag[16], const u8 key[32], const u8 nonce[12]);   /* encrypts in place */
int  aead_open(u8 *data, u32 len, const u8 tag[16], const u8 key[32], const u8 nonce[12]); /* 0 ok, -1 forged */
#endif

/* Layered reference-vector generator for the AirPlay FairPlay (PlayFair) chain.
 *
 * Compiles the VERBATIM UxPlay sources, so the C# port can be checked
 * byte-for-byte without needing an iPhone.  Vectors are emitted per layer
 * (modified_md5 -> sap_hash -> garble -> session key -> playfair_decrypt) so a
 * mismatch can be bisected to a single function instead of debugged blind.
 */
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#include "fairplay.h"

void modified_md5(unsigned char *originalblockIn, unsigned char *keyIn, unsigned char *keyOut);
void sap_hash(unsigned char *blockIn, unsigned char *keyOut);
void garble(unsigned char *b0, unsigned char *b1, unsigned char *b2, unsigned char *b3, unsigned char *b4);
void generate_session_key(unsigned char *oldSap, unsigned char *messageIn, unsigned char *sessionKey);
void generate_key_schedule(unsigned char *key_material, uint32_t key_schedule[11][4]);
void cycle(unsigned char *block, uint32_t key_schedule[11][4]);
void playfair_decrypt(unsigned char *message3, unsigned char *cipherText, unsigned char *keyOut);
extern unsigned char default_sap[];
extern unsigned char initial_session_key[];

static uint32_t st = 0x12345678u;
static unsigned char rnd8(void) { st = st * 1664525u + 1013904223u; return (unsigned char)((st >> 16) & 0xFF); }
static void fill(unsigned char *b, int n) { for (int i = 0; i < n; i++) b[i] = rnd8(); }
static void hex(const unsigned char *b, int n) { for (int i = 0; i < n; i++) printf("%02x", b[i]); }
static void fp_head(unsigned char *p)
{
    p[0]=0x46; p[1]=0x50; p[2]=0x4c; p[3]=0x59; p[4]=0x03; p[5]=0x01; p[6]=0x01; p[7]=0x00;
}

/* A realistic fp-setup type-1 request: FPLY header plus a LEGAL mode byte.
   decryptMessage() reads mode from messageIn[12] and indexes message_key[mode];
   leaving byte 12 random would exercise out-of-bounds UB in the C reference. */
static void fp_msg(unsigned char *p, int mode)
{
    int i;
    for (i = 0; i < 164; i++) p[i] = rnd8();
    fp_head(p);
    p[8]=0; p[9]=0; p[10]=0; p[11]=0;
    p[12]=(unsigned char)mode;
    p[13]=0x01;
    p[14]=(unsigned char)mode;
    p[15]=0x00;
}

int main(void)
{
    fairplay_t *fp = fairplay_init(NULL);
    unsigned char req16[16], res142[142];
    unsigned char req164[164], res32[32];
    unsigned char input72[72], out16[16];
    int m, v, i, rc;

    printf("{\n");

    /* ---------- MD5 additive constants: verify (int)(2^32*|sin(i+1)|) ---------- */
    printf("  \"md5K\": [");
    for (i = 0; i < 64; i++) {
        uint32_t k = (uint32_t)(int)(long long)((1LL << 32) * fabs(sin(i + 1)));
        printf("%s%u", i ? "," : "", (unsigned)k);
    }
    printf("],\n");

    /* ---------- modified_md5 ---------- */
    printf("  \"modifiedMd5\": [\n");
    for (v = 0; v < 2; v++) {
        unsigned char blk[64], key[16], out[16];
        fill(blk, 64); fill(key, 16); memset(out, 0, 16);
        modified_md5(blk, key, out);
        printf("    {\"block\": \""); hex(blk, 64);
        printf("\", \"keyIn\": \""); hex(key, 16);
        printf("\", \"keyOut\": \""); hex(out, 16);
        printf("\"}%s\n", v < 1 ? "," : "");
    }
    printf("  ],\n");

    /* ---------- sap_hash ---------- */
    printf("  \"sapHash\": [\n");
    for (v = 0; v < 2; v++) {
        unsigned char blk[64], key[16];
        fill(blk, 64); fill(key, 16);
        sap_hash(blk, key);
        printf("    {\"block\": \""); hex(blk, 64);
        printf("\", \"keyOut\": \""); hex(key, 16);
        printf("\"}%s\n", v < 1 ? "," : "");
    }
    printf("  ],\n");

    /* ---------- garble (the delicate 443-line core) ---------- */
    printf("  \"garble\": [\n");
    for (v = 0; v < 3; v++) {
        unsigned char b0[32], b1[256], b2[64], b3[160], b4[32];
        memset(b0, 0, sizeof b0); memset(b1, 0, sizeof b1);
        memset(b2, 0, sizeof b2); memset(b3, 0, sizeof b3); memset(b4, 0, sizeof b4);
        fill(b0, 20); fill(b1, 210); fill(b2, 35); fill(b3, 132); fill(b4, 21);
        printf("    {\"in0\": \""); hex(b0, 20);
        printf("\", \"in1\": \""); hex(b1, 210);
        printf("\", \"in2\": \""); hex(b2, 35);
        printf("\", \"in3\": \""); hex(b3, 132);
        printf("\", \"in4\": \""); hex(b4, 21);
        garble(b0, b1, b2, b3, b4);
        printf("\", \"out0\": \""); hex(b0, 20);
        printf("\", \"out1\": \""); hex(b1, 210);
        printf("\", \"out2\": \""); hex(b2, 35);
        printf("\", \"out3\": \""); hex(b3, 132);
        printf("\", \"out4\": \""); hex(b4, 21);
        printf("\"}%s\n", v < 2 ? "," : "");
    }
    printf("  ],\n");

    /* ---------- generate_session_key(default_sap, msg164) ---------- */
    printf("  \"sessionKey\": [\n");
    for (v = 0; v < 4; v++) {
        unsigned char msg[164], key[16];
        fp_msg(msg, v % 4);
        memset(key, 0, 16);
        generate_session_key(default_sap, msg, key);
        printf("    {\"msg\": \""); hex(msg, 164);
        printf("\", \"key\": \""); hex(key, 16);
        printf("\"}%s\n", v < 3 ? "," : "");
    }
    printf("  ],\n");

    /* ---------- generate_key_schedule + cycle ---------- */
    printf("  \"keySchedule\": [\n");
    for (v = 0; v < 2; v++) {
        unsigned char km[16], block[16];
        uint32_t sched[11][4];
        fill(km, 16); fill(block, 16);
        memset(sched, 0, sizeof sched);
        generate_key_schedule(km, sched);
        printf("    {\"keyMaterial\": \""); hex(km, 16);
        printf("\", \"schedule\": [");
        for (i = 0; i < 11; i++) {
            printf("%s[%u,%u,%u,%u]", i ? "," : "",
                   sched[i][0], sched[i][1], sched[i][2], sched[i][3]);
        }
        printf("], \"blockIn\": \""); hex(block, 16);
        cycle(block, sched);
        printf("\", \"blockOut\": \""); hex(block, 16);
        printf("\"}%s\n", v < 1 ? "," : "");
    }
    printf("  ],\n");

    /* ---------- fp-setup replies ---------- */
    printf("  \"fpSetup\": [\n");
    for (m = 0; m < 4; m++) {
        memset(req16, 0, 16); fp_head(req16); req16[14] = (unsigned char)m;
        memset(res142, 0, 142);
        rc = fairplay_setup(fp, req16, res142);
        printf("    {\"mode\": %d, \"request\": \"", m); hex(req16, 16);
        printf("\", \"rc\": %d, \"response\": \"", rc); hex(res142, 142);
        printf("\"}%s\n", m < 3 ? "," : "");
    }
    printf("  ],\n");

    printf("  \"fpHandshake\": [\n");
    for (v = 0; v < 3; v++) {
        fill(req164, 164); fp_head(req164); memset(res32, 0, 32);
        rc = fairplay_handshake(fp, req164, res32);
        printf("    {\"request\": \""); hex(req164, 164);
        printf("\", \"rc\": %d, \"response\": \"", rc); hex(res32, 32);
        printf("\"}%s\n", v < 2 ? "," : "");
    }
    printf("  ],\n");

    /* ---------- end-to-end: playfair_decrypt ---------- */
    printf("  \"fpDecrypt\": [\n");
    for (v = 0; v < 8; v++) {
        fp_msg(req164, v % 4);
        fairplay_handshake(fp, req164, res32);
        fill(input72, 72); memset(out16, 0, 16);
        rc = fairplay_decrypt(fp, input72, out16);
        printf("    {\"keymsg\": \""); hex(req164, 164);
        printf("\", \"input\": \""); hex(input72, 72);
        printf("\", \"rc\": %d, \"key\": \"", rc); hex(out16, 16);
        printf("\"}%s\n", v < 7 ? "," : "");
    }
    printf("  ]\n");

    printf("}\n");
    fairplay_destroy(fp);
    return 0;
}

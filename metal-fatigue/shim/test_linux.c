/* Native test harness: drives the stand-in the same way MFatigue.exe does. */
#define _GNU_SOURCE
#include "mfnet.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <unistd.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <time.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>

static pthread_mutex_t mu; static const char *role = "?";
void plat_lock(void) { pthread_mutex_lock(&mu); }
void plat_unlock(void) { pthread_mutex_unlock(&mu); }
u32 plat_ms(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (u32)(t.tv_sec * 1000 + t.tv_nsec / 1000000); }
void *plat_alloc(u32 n) { return malloc(n); }
void plat_free(void *p) { free(p); }
void plat_log(const char *f, ...) { va_list ap; va_start(ap, f); fprintf(stderr, "[%s] ", role); vfprintf(stderr, f, ap); fputc('\n', stderr); va_end(ap); }
int plat_connect(const char *host, int port, int timeout_ms) {
    int s = socket(AF_INET, SOCK_STREAM, 0); struct sockaddr_in a = {0};
    a.sin_family = AF_INET; a.sin_port = htons(port); a.sin_addr.s_addr = inet_addr(host);
    fcntl(s, F_SETFL, O_NONBLOCK);
    if (connect(s, (void *)&a, sizeof a) != 0) {
        if (errno != EINPROGRESS) { close(s); return -1; }
        struct pollfd p = {s, POLLOUT, 0}; int e = 0; socklen_t l = sizeof e;
        if (poll(&p, 1, timeout_ms) <= 0 || getsockopt(s, SOL_SOCKET, SO_ERROR, &e, &l) || e) { close(s); return -1; }
    }
    int one = 1; setsockopt(s, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
    return s;
}
int plat_send(int s, const void *p, int n, int t) {
    const u8 *b = p;
    while (n > 0) { int r = send(s, b, n, MSG_NOSIGNAL); if (r > 0) { b += r; n -= r; continue; }
        if (errno != EAGAIN) return -1; struct pollfd pf = {s, POLLOUT, 0}; if (poll(&pf, 1, t) <= 0) return -1; }
    return 0;
}
int plat_recv(int s, void *p, int n) { int r = recv(s, p, n, 0); if (r > 0) return r; if (r == 0) return -1; return errno == EAGAIN ? 0 : -1; }
int plat_wait(int s, int t) { struct pollfd p = {s, POLLIN, 0}; return poll(&p, 1, t) > 0; }
void plat_close(int s) { close(s); }
int plat_random(u8 *p, u32 n) { FILE *f = fopen("/dev/urandom", "rb"); if (!f) return -1; size_t r = fread(p, 1, n, f); fclose(f); return r == n ? 0 : -1; }

/* IDirectPlay4A vtable indexes */
enum { V_Release = 2, V_Close = 4, V_CreatePlayer = 6, V_DestroyPlayer = 9, V_EnumPlayers = 12, V_EnumSessions = 13,
       V_GetSessionDesc = 22, V_Open = 24, V_Receive = 25, V_Send = 26, V_SetSessionDesc = 31, V_EnumConnections = 35, V_InitializeConnection = 38 };
typedef struct { void **v; } DP;
#define CALL(dp, idx, type) ((type)(dp)->v[idx])

static int nconn; static u8 connblob[256];
static int conn_cb(const GUID *sp, void *c, u32 size, const DPNAME *n, u32 fl, void *ctx) { nconn++; memcpy(connblob, c, size); printf("[%s] connection: %s (%u bytes)\n", role, n->lpszShortNameA, size); return 1; }
static struct { GUID inst; char name[64]; } sess[8]; static int nsess, timedout;
static int sess_cb(const DPSESSIONDESC2 *sd, u32 *to, u32 fl, void *ctx) {
    if (fl & 1) { timedout = 1; return 0; }
    strcpy(sess[nsess].name, sd->lpszSessionNameA); sess[nsess].inst = sd->guidInstance; nsess++; return 1;
}
static struct { u32 id; char name[64]; } pl[8]; static int npl;
static int play_cb(u32 id, u32 type, const DPNAME *n, u32 fl, void *ctx) { pl[npl].id = id; strcpy(pl[npl].name, n->lpszShortNameA); npl++; return 1; }

static int enum_sessions(DP *dp) {
    DPSESSIONDESC2 sd = {0}; sd.dwSize = sizeof sd; nsess = 0; timedout = 0;
    HRESULT hr = CALL(dp, V_EnumSessions, HRESULT (*)(DP *, DPSESSIONDESC2 *, u32, void *, void *, u32))(dp, &sd, 0, sess_cb, 0, 0x10);
    if (hr) { printf("EnumSessions hr=%x\n", hr); exit(2); }
    return nsess;
}
static int enum_players(DP *dp) { npl = 0; CALL(dp, V_EnumPlayers, HRESULT (*)(DP *, void *, void *, void *, u32))(dp, 0, play_cb, 0, 0); return npl; }
/* the game's two-step receive: PEEK with no buffer for the size, then the real read */
static int recv_one(DP *dp, u32 *from, char *buf, u32 cap) {
    u32 to = 0, size = 0xdeadbeef;
    HRESULT hr = CALL(dp, V_Receive, HRESULT (*)(DP *, u32 *, u32 *, u32, void *, u32 *))(dp, from, &to, 8, 0, &size);
    if (hr == DPERR_NOMESSAGES) return 0;
    if (hr != DPERR_BUFFERTOOSMALL || size > cap) { printf("peek hr=%x size=%u\n", hr, size); exit(2); }
    u32 size2 = size;
    hr = CALL(dp, V_Receive, HRESULT (*)(DP *, u32 *, u32 *, u32, void *, u32 *))(dp, from, &to, 1, buf, &size2);
    if (hr || size2 != size) { printf("recv hr=%x\n", hr); exit(2); }
    return (int)size;
}
static HRESULT send_to(DP *dp, u32 me, u32 to, const void *d, u32 n) { return CALL(dp, V_Send, HRESULT (*)(DP *, u32, u32, u32, const void *, u32))(dp, me, to, 0, d, n); }
#define CHECK(c, msg) do { if (!(c)) { printf("[%s] FAIL: %s\n", role, msg); exit(1); } } while (0)

int main(int argc, char **argv) {
    pthread_mutexattr_t a; pthread_mutexattr_init(&a); pthread_mutexattr_settype(&a, PTHREAD_MUTEX_RECURSIVE); pthread_mutex_init(&mu, &a);
    role = argv[1]; int port = atoi(argv[2]);
    mfnet_config(!strcmp(role, "typed") ? "127.0.0.2" : "127.0.0.1", port, argc > 3 ? argv[3] : "files-A", argc > 4 ? argv[4] : "");
    DP *dp = mfnet_create(); u32 me = 0; char buf[70000]; u32 from;
    CALL(dp, V_EnumConnections, HRESULT (*)(DP *, void *, void *, void *, u32))(dp, 0, conn_cb, 0, 1);
    CHECK(nconn == 1, "one connection");
    CHECK(CALL(dp, V_InitializeConnection, HRESULT (*)(DP *, void *, u32))(dp, connblob, 0) == 0, "InitializeConnection");
    DPNAME nm = {sizeof nm, 0, (char *)role, 0};

    if (!strcmp(role, "host")) {
        DPSESSIONDESC2 sd = {0}; sd.dwSize = sizeof sd; sd.dwFlags = 0x44; sd.dwMaxPlayers = 8; sd.lpszSessionNameA = "Test game";
        CHECK(CALL(dp, V_Open, HRESULT (*)(DP *, DPSESSIONDESC2 *, u32))(dp, &sd, 2) == 0, "Open create");
        CHECK(CALL(dp, V_CreatePlayer, HRESULT (*)(DP *, u32 *, DPNAME *, void *, void *, u32, u32))(dp, &me, &nm, 0, 0, 0, 0) == 0 && me, "CreatePlayer");
        CHECK(enum_players(dp) == 1 && pl[0].id == me, "host alone in roster");
        int got = 0, big = 0, locked = 0; u32 start = plat_ms();
        while (plat_ms() - start < 8000 && !(got && big && locked)) {
            int n = recv_one(dp, &from, buf, sizeof buf);
            if (n == 0) { usleep(5000); enum_players(dp); continue; }
            if (n == 60000) { int ok = 1; for (int i = 0; i < n; i++) if ((u8)buf[i] != (u8)(i * 7)) ok = 0; CHECK(ok, "big packet intact"); big = 1; continue; }
            buf[n] = 0; printf("[host] got \"%s\" from %08x\n", buf, from);
            CHECK(enum_players(dp) == 2, "host sees two players");
            CHECK(send_to(dp, me, from, "welcome", 7) == 0, "reply");
            got = 1;
            /* lock the lobby the way the game does when a match starts */
            u8 d[0x80]; u32 sz = sizeof d;
            CHECK(CALL(dp, V_GetSessionDesc, HRESULT (*)(DP *, void *, u32 *))(dp, d, &sz) == 0, "GetSessionDesc");
            DPSESSIONDESC2 *p = (void *)d; CHECK(p->dwFlags == 0x44 && !strcmp(p->lpszSessionNameA, "Test game"), "session desc contents");
            p->dwFlags |= 0x20;
            CHECK(CALL(dp, V_SetSessionDesc, HRESULT (*)(DP *, DPSESSIONDESC2 *, u32))(dp, p, 0) == 0, "SetSessionDesc");
            locked = 1;
        }
        CHECK(got && big && locked, "host saw hello, big packet and locked");
        sleep(2); /* stay up while the others run their late checks */
        CHECK(enum_players(dp) == 1, "host sees joiner leave");
    } else if (!strcmp(role, "join")) {
        int tries = 0; while (enum_sessions(dp) == 0 && tries++ < 50) usleep(100000);
        CHECK(nsess == 1 && !strcmp(sess[0].name, "Test game") && timedout, "one session listed");
        DPSESSIONDESC2 sd = {0}; sd.dwSize = sizeof sd; sd.guidInstance = sess[0].inst;
        CHECK(CALL(dp, V_Open, HRESULT (*)(DP *, DPSESSIONDESC2 *, u32))(dp, &sd, 1) == 0, "Open join");
        CHECK(CALL(dp, V_CreatePlayer, HRESULT (*)(DP *, u32 *, DPNAME *, void *, void *, u32, u32))(dp, &me, &nm, 0, 0, 0, 0) == 0 && me, "CreatePlayer");
        CHECK(enum_players(dp) == 2, "joiner sees two players");
        u32 host = pl[0].id == me ? pl[1].id : pl[0].id;
        CHECK(!strcmp(pl[0].id == me ? pl[1].name : pl[0].name, "host"), "host name in roster");
        CHECK(send_to(dp, me, host, "hello from joiner", 17) == 0, "send");
        for (int i = 0; i < 60000; i++) buf[i] = (char)(i * 7);
        CHECK(send_to(dp, me, host, buf, 60000) == 0, "send big");
        CHECK(send_to(dp, me, host, buf, 60001) == DPERR_SENDTOOBIG, "oversize refused");
        int n = 0; u32 start = plat_ms();
        while (plat_ms() - start < 5000 && (n = recv_one(dp, &from, buf, sizeof buf)) == 0) usleep(5000);
        buf[n] = 0; CHECK(n == 7 && !strcmp(buf, "welcome") && from == host, "reply from host");
        printf("[join] got \"%s\" from host\n", buf);
        usleep(300000);
        u8 d[0x80]; u32 sz = sizeof d;
        CHECK(CALL(dp, V_GetSessionDesc, HRESULT (*)(DP *, void *, u32 *))(dp, d, &sz) == 0 && (((DPSESSIONDESC2 *)d)->dwFlags & 0x20), "joiner sees lock");
        sleep(1);
        CALL(dp, V_Close, HRESULT (*)(DP *))(dp);
    } else if (!strcmp(role, "norelay")) {
        /* relay down: hosting must still succeed as a lobby of one */
        DPSESSIONDESC2 sd = {0}; sd.dwSize = sizeof sd; sd.dwFlags = 0x44; sd.dwMaxPlayers = 8; sd.lpszSessionNameA = "Alone";
        CHECK(enum_sessions(dp) == 0, "no sessions without relay");
        CHECK(CALL(dp, V_Open, HRESULT (*)(DP *, DPSESSIONDESC2 *, u32))(dp, &sd, 2) == 0, "offline Open");
        CHECK(CALL(dp, V_CreatePlayer, HRESULT (*)(DP *, u32 *, DPNAME *, void *, void *, u32, u32))(dp, &me, &nm, 0, 0, 0, 0) == 0 && me, "offline CreatePlayer");
        CHECK(enum_players(dp) == 1 && pl[0].id == me, "offline roster");
        CHECK(send_to(dp, me, 123, "x", 1) == 0, "offline send ok");
        CHECK(recv_one(dp, &from, buf, sizeof buf) == 0, "offline receive empty");
        u8 d[0x80]; u32 sz = sizeof d;
        CHECK(CALL(dp, V_GetSessionDesc, HRESULT (*)(DP *, void *, u32 *))(dp, d, &sz) == 0, "offline GetSessionDesc");
        CALL(dp, V_Close, HRESULT (*)(DP *))(dp);
    } else if (!strcmp(role, "typed")) {
        /* default relay is unreachable; typing the right address in "Search IP" must reach the host's game */
        CHECK(enum_sessions(dp) == 0, "nothing via the wrong default relay");
        static const u8 g_total[16] = {0x60,0xF5,0x18,0x13,0x2C,0x91,0xd0,0x11,0x9D,0xAA,0x00,0xA0,0xC9,0x0A,0x43,0xCB};
        static const u8 g_inet[16] = {0xA0,0x4D,0xA5,0xC4,0xAF,0xE0,0xcf,0x11,0x9C,0x4E,0x00,0xA0,0xC9,0x05,0x42,0x5E};
        u8 blob[80] = {0}; u32 tot = 24 + 20 + 10;
        memcpy(blob, g_total, 16); blob[16] = 4; memcpy(blob + 20, &tot, 4);
        memcpy(blob + 24, g_inet, 16); blob[40] = 10; memcpy(blob + 44, "127.0.0.1", 10);
        CHECK(CALL(dp, V_InitializeConnection, HRESULT (*)(DP *, void *, u32))(dp, blob, 0) == 0, "InitializeConnection typed");
        CHECK(enum_sessions(dp) == 1, "typed relay address finds the game");
        CHECK(CALL(dp, V_InitializeConnection, HRESULT (*)(DP *, void *, u32))(dp, connblob, 0) == 0, "InitializeConnection blank");
        CHECK(enum_sessions(dp) == 0, "blank box goes back to the ini relay");
    } else if (!strcmp(role, "late")) {
        /* runs after the lobby is locked: nothing should be listed */
        CHECK(enum_sessions(dp) == 0 && timedout, "locked game hidden");
    } else if (!strcmp(role, "otherroom")) {
        CHECK(enum_sessions(dp) == 0, "other room sees nothing");
    } else if (!strcmp(role, "other")) {
        /* different game files: the open game must be hidden and unjoinable */
        CHECK(enum_sessions(dp) == 0, "mismatched files hidden");
    }
    CHECK(CALL(dp, V_Release, u32 (*)(DP *))(dp) == 0, "Release");
    printf("[%s] PASS\n", role);
    return 0;
}

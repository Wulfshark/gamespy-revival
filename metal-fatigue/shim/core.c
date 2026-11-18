/* mfnet core: implements the parts of IDirectPlay4A that MFatigue.exe uses, over a relay.
 *
 * What the game does (from disassembly of the 2000-04-10 build):
 *   CoCreateInstance(CLSID_DirectPlay, IID_IDirectPlay4A) once per attempt
 *   EnumConnections, InitializeConnection(TCP/IP + typed IP)
 *   host: Open(CREATE, flags 0x44, max 8) / join: EnumSessions(ASYNC) then Open(JOIN, guidInstance)
 *   CreatePlayer(name), EnumPlayers polled, GetSessionDesc/SetSessionDesc to lock the lobby
 *   Send(from, to, flags 0, data) one player at a time; the game does its own resends
 *   Receive(PEEK, NULL) for the size, then Receive(ALL, buf); system messages are discarded
 *   Close, DestroyPlayer, Release
 */
#include "mfnet.h"

#define PROTO 1
enum { C_HELLO = 1, C_LIST, C_CREATE, C_JOIN, C_CREATE_PLAYER, C_SEND, C_SETFLAGS, C_LEAVE, C_DESTROY_PLAYER };
enum { S_HELLO = 0x81, S_LIST, S_CREATE, S_JOIN, S_PLAYER, S_DATA, S_ROSTER, S_DESC, S_LOST };

#define MAX_FRAME 65535
#define MAX_PAYLOAD 60000
#define MAX_ROSTER 16
#define MAX_LIST 8          /* the game's session table holds 8 entries */
#define NAME_MAX_ 63        /* the game copies names into 64-byte fields with strcpy */
#define LIST_CACHE_MS 1000

static const GUID APP_GUID = {0x50e00e33, 0x466b, 0x11d1, {0xbe, 0x45, 0x00, 0xa0, 0xc9, 0x03, 0x41, 0x72}};
static const GUID SP_TCPIP = {0x36E95EE0, 0x8577, 0x11cf, {0x96, 0x0C, 0x00, 0x80, 0xC7, 0x53, 0x4E, 0x82}};
static const GUID DPAID_TotalSize = {0x1318F560, 0x912C, 0x11d0, {0x9D, 0xAA, 0x00, 0xA0, 0xC9, 0x0A, 0x43, 0xCB}};
static const GUID DPAID_ServiceProvider = {0x07D916C0, 0xE0AF, 0x11cf, {0x9C, 0x4E, 0x00, 0xA0, 0xC9, 0x05, 0x42, 0x5E}};
static const GUID DPAID_INet = {0xC4A54DA0, 0xE0AF, 0x11cf, {0x9C, 0x4E, 0x00, 0xA0, 0xC9, 0x05, 0x42, 0x5E}};
static const GUID IID_IUnknown_ = {0x00000000, 0x0000, 0x0000, {0xC0, 0, 0, 0, 0, 0, 0, 0x46}};
static const GUID IID_DP2A = {0x9d460580, 0xa822, 0x11cf, {0x96, 0x0c, 0x00, 0x80, 0xc7, 0x53, 0x4e, 0x82}};
static const GUID IID_DP3A = {0x133efe41, 0x32dc, 0x11d0, {0x9c, 0xfb, 0x00, 0xa0, 0xc9, 0x0a, 0x43, 0xcb}};
static const GUID IID_DP4A = {0x0ab1c531, 0x4745, 0x11d1, {0xa7, 0xa1, 0x00, 0x00, 0xf8, 0x03, 0xab, 0xfc}};

typedef struct Msg { struct Msg *next; u32 from, to, len; u8 data[1]; } Msg;
typedef struct { u32 id; char name[NAME_MAX_ + 1]; } Player;
typedef struct { GUID inst; u32 flags; u8 cur, max; char name[NAME_MAX_ + 1]; } ListEntry;

static struct {
    char host[128]; char def_host[128]; int port; char fp[65]; char room[64];
    int sock;                    /* -1 when not connected */
    u8 rx[MAX_FRAME + 2 + 4096]; u32 rxlen;
    u8 tx[MAX_FRAME + 2];
    int resp_type; u8 resp[2048]; u32 resp_len;
    int in_session; GUID inst; u32 sd_flags; u32 sd_max; char sd_name[NAME_MAX_ + 1];
    u32 my_id;
    Player roster[MAX_ROSTER]; u32 nroster;
    Msg *qhead, *qtail; u32 qcount;
    ListEntry list[MAX_LIST]; u32 nlist; u32 list_at; int list_valid;
    u32 sent, recvd, last_stat;
    int offline;                 /* hosting with no relay: a lobby of one, so the game does not crash */
    u32 fail_at; int failed;       /* back off after a failed connect so menus don't stall */
    u8 addr[80];                 /* connection blob handed to EnumConnections */
} G = { .sock = -1 };

static void *mcpy(void *d, const void *s, u32 n) { u8 *a = d; const u8 *b = s; while (n--) *a++ = *b++; return d; }
static void mzero(void *d, u32 n) { u8 *a = d; while (n--) *a++ = 0; }
static u32 slen(const char *s) { u32 n = 0; if (s) while (s[n]) n++; return n; }
static int geq(const GUID *a, const GUID *b) { const u8 *x = (const u8 *)a, *y = (const u8 *)b; for (int i = 0; i < 16; i++) if (x[i] != y[i]) return 0; return 1; }
static void scpy(char *d, const char *s, u32 cap) { u32 i = 0; if (s) for (; s[i] && i + 1 < cap; i++) d[i] = s[i]; d[i] = 0; }
static u32 rd32(const u8 *p) { return p[0] | p[1] << 8 | p[2] << 16 | (u32)p[3] << 24; }
static void wr32(u8 *p, u32 v) { p[0] = (u8)v; p[1] = (u8)(v >> 8); p[2] = (u8)(v >> 16); p[3] = (u8)(v >> 24); }

void mfnet_config(const char *host, int port, const char *fingerprint, const char *room) {
    scpy(G.host, host, sizeof G.host); scpy(G.def_host, host, sizeof G.def_host); G.port = port; scpy(G.fp, fingerprint, sizeof G.fp);
    scpy(G.room, room, sizeof G.room);
}

static void queue_clear(void) {
    while (G.qhead) { Msg *m = G.qhead; G.qhead = m->next; plat_free(m); }
    G.qtail = 0; G.qcount = 0;
}

static void session_clear(void) {
    G.in_session = 0; G.offline = 0; G.my_id = 0; G.nroster = 0; G.sd_flags = 0; G.sd_name[0] = 0;
    queue_clear();
}

static void drop(const char *why) {
    if (G.sock >= 0) { plat_log("connection to relay closed: %s", why); plat_close(G.sock); }
    G.sock = -1; G.rxlen = 0; G.list_valid = 0;
    /* keep roster and session flags so the game keeps a stable picture; its own timeouts take over */
    G.in_session = 0;
}

static int send_frame(int type, const u8 *payload, u32 len) {
    if (G.sock < 0) return -1;
    if (len > MAX_FRAME - 1) return -1;
    G.tx[0] = (u8)(len + 1); G.tx[1] = (u8)((len + 1) >> 8); G.tx[2] = (u8)type;
    if (len) mcpy(G.tx + 3, payload, len);
    if (plat_send(G.sock, G.tx, (int)len + 3, 3000) != 0) { drop("send failed"); return -1; }
    return 0;
}

static void on_frame(int type, const u8 *p, u32 len) {
    if (type == S_DATA) {
        if (len <= 8) return;
        u32 n = len - 8;
        if (G.qcount > 4096) { plat_log("receive queue full, dropping packet"); return; }
        Msg *m = plat_alloc(sizeof(Msg) + n);
        if (!m) return;
        m->next = 0; m->from = rd32(p); m->to = rd32(p + 4); m->len = n; mcpy(m->data, p + 8, n);
        if (G.qtail) G.qtail->next = m; else G.qhead = m;
        G.qtail = m; G.qcount++; G.recvd++;
    } else if (type == S_ROSTER) {
        if (len < 1) return;
        u32 n = p[0], o = 1, k = 0;
        for (u32 i = 0; i < n && o + 5 <= len; i++) {
            u32 id = rd32(p + o), nl = p[o + 4];
            if (o + 5 + nl > len) break;
            if (k < MAX_ROSTER) {
                G.roster[k].id = id;
                u32 c = nl > NAME_MAX_ ? NAME_MAX_ : nl;
                mcpy(G.roster[k].name, p + o + 5, c); G.roster[k].name[c] = 0;
                k++;
            }
            o += 5 + nl;
        }
        G.nroster = k;
        plat_log("roster: %d player(s)", (int)k);
        for (u32 i = 0; i < k; i++) plat_log("  %08x %s%s", G.roster[i].id, G.roster[i].name, G.roster[i].id == G.my_id ? " (me)" : "");
    } else if (type == S_DESC) {
        if (len >= 4) { G.sd_flags = rd32(p); plat_log("session flags now %x", G.sd_flags); }
    } else if (type == S_LOST) {
        drop("relay ended the session");
    } else {
        G.resp_type = type;
        G.resp_len = len > sizeof G.resp ? (u32)sizeof G.resp : len;
        mcpy(G.resp, p, G.resp_len);
    }
}

/* Reads whatever the relay has sent. Stops early once a frame of type `until` has arrived. */
static void pump(int until) {
    for (;;) {
        if (G.sock < 0) return;
        /* parse complete frames already buffered */
        u32 o = 0;
        while (G.rxlen - o >= 2) {
            u32 n = G.rx[o] | G.rx[o + 1] << 8;
            if (n < 1) { drop("bad frame"); return; }
            if (G.rxlen - o < 2 + n) break;
            int type = G.rx[o + 2];
            on_frame(type, G.rx + o + 3, n - 1);
            o += 2 + n;
            if (G.sock < 0) return;
            if (until && type == until) break;
        }
        if (o) { u32 rest = G.rxlen - o; for (u32 i = 0; i < rest; i++) G.rx[i] = G.rx[o + i]; G.rxlen = rest; }
        if (until && G.resp_type == until) return;
        int r = plat_recv(G.sock, G.rx + G.rxlen, (int)(sizeof G.rx - G.rxlen));
        if (r < 0) { drop("relay closed the connection"); return; }
        if (r == 0) return;
        G.rxlen += (u32)r;
    }
}

static int request(int type, const u8 *payload, u32 len, int expect, int timeout_ms) {
    G.resp_type = 0;
    if (send_frame(type, payload, len) != 0) return -1;
    u32 start = plat_ms();
    for (;;) {
        pump(expect);
        if (G.resp_type == expect) return 0;
        if (G.sock < 0) return -1;
        u32 el = plat_ms() - start;
        if (el >= (u32)timeout_ms) { plat_log("relay did not answer request %d in time", type); return -1; }
        plat_wait(G.sock, timeout_ms - (int)el);
    }
}

static int ensure_connected(void) {
    if (G.sock >= 0) return 0;
    if (G.failed && plat_ms() - G.fail_at < 5000) return -1;
    G.rxlen = 0;
    G.sock = plat_connect(G.host, G.port, 3000);
    if (G.sock < 0) { plat_log("cannot reach relay %s:%d", G.host, G.port); G.failed = 1; G.fail_at = plat_ms(); return -1; }
    G.failed = 0;
    u8 b[4 + 16 + 1 + 64 + 1 + 64]; u32 n = slen(G.fp); if (n > 64) n = 64;
    u32 rn = slen(G.room); if (rn > 63) rn = 63;
    wr32(b, PROTO); mcpy(b + 4, &APP_GUID, 16); b[20] = (u8)n; mcpy(b + 21, G.fp, n);
    b[21 + n] = (u8)rn; mcpy(b + 22 + n, G.room, rn);
    if (request(C_HELLO, b, 22 + n + rn, S_HELLO, 3000) != 0) { drop("no hello"); return -1; }
    if (rn) plat_log("private room code in use");
    plat_log("connected to relay %s:%d (relay protocol %d, game files %s)", G.host, G.port, (int)rd32(G.resp), G.fp);
    return 0;
}

static void disconnect(void) {
    if (G.sock >= 0) { send_frame(C_LEAVE, 0, 0); plat_close(G.sock); }
    G.sock = -1; G.rxlen = 0; G.list_valid = 0;
    session_clear();
}

/* ---- IDirectPlay4A ---- */

typedef struct { void **vtbl; i32 ref; } Obj;
static Obj g_obj;

static HRESULT STDCALL m_QueryInterface(Obj *t, const GUID *iid, void **out) {
    if (!out) return E_INVALIDARG;
    if (iid && (geq(iid, &IID_IUnknown_) || geq(iid, &IID_DP4A) || geq(iid, &IID_DP3A) || geq(iid, &IID_DP2A))) {
        t->ref++; *out = t; return DP_OK;
    }
    *out = 0; return E_NOINTERFACE;
}
static u32 STDCALL m_AddRef(Obj *t) { return (u32)++t->ref; }
static u32 STDCALL m_Release(Obj *t) {
    plat_lock();
    i32 r = --t->ref;
    if (r <= 0) { t->ref = 0; r = 0; plat_log("DirectPlay object released"); disconnect(); }
    plat_unlock();
    return (u32)r;
}

static HRESULT STDCALL m_Close(Obj *t) {
    (void)t;
    plat_lock();
    plat_log("Close (sent %d, received %d packets)", (int)G.sent, (int)G.recvd);
    if (G.sock >= 0 && G.in_session) send_frame(C_LEAVE, 0, 0);
    session_clear();
    G.list_valid = 0;
    plat_unlock();
    return DP_OK;
}

static HRESULT STDCALL m_CreatePlayer(Obj *t, u32 *id, DPNAME *name, void *ev, void *data, u32 size, u32 flags) {
    (void)t; (void)ev; (void)data; (void)size; (void)flags;
    HRESULT hr = DPERR_CANTCREATEPLAYER;
    plat_lock();
    if (id && G.in_session && G.offline) {
        G.my_id = 0x00010001; *id = G.my_id;
        G.roster[0].id = G.my_id; scpy(G.roster[0].name, name ? name->lpszShortNameA : "", sizeof G.roster[0].name); G.nroster = 1;
        hr = DP_OK;
    } else if (id && G.in_session && G.sock >= 0) {
        u8 b[1 + NAME_MAX_]; const char *s = name ? name->lpszShortNameA : 0;
        u32 n = slen(s); if (n > NAME_MAX_) n = NAME_MAX_;
        b[0] = (u8)n; mcpy(b + 1, s, n);
        if (request(C_CREATE_PLAYER, b, 1 + n, S_PLAYER, 3000) == 0 && G.resp_len >= 5 && G.resp[0] == 0) {
            G.my_id = rd32(G.resp + 1); *id = G.my_id;
            plat_log("CreatePlayer \"%s\" -> id %08x", s ? s : "", G.my_id);
            pump(0); /* pick up the roster that follows */
            hr = DP_OK;
        }
    }
    if (hr != DP_OK) plat_log("CreatePlayer failed");
    plat_unlock();
    return hr;
}

static HRESULT STDCALL m_DestroyPlayer(Obj *t, u32 id) {
    (void)t;
    plat_lock();
    plat_log("DestroyPlayer %08x", id);
    if (id == G.my_id && G.sock >= 0) { u8 b[4]; wr32(b, id); send_frame(C_DESTROY_PLAYER, b, 4); G.my_id = 0; }
    plat_unlock();
    return DP_OK;
}

static HRESULT STDCALL m_EnumPlayers(Obj *t, GUID *inst, EnumPlayCB cb, void *ctx, u32 flags) {
    (void)t; (void)inst; (void)flags;
    Player copy[MAX_ROSTER]; u32 n;
    plat_lock();
    pump(0);
    n = G.nroster; mcpy(copy, G.roster, sizeof copy);
    plat_unlock();
    if (!cb) return E_INVALIDARG;
    for (u32 i = 0; i < n && i < 8; i++) {   /* the game's player table holds 8 */
        DPNAME nm; nm.dwSize = sizeof nm; nm.dwFlags = 0; nm.lpszShortNameA = copy[i].name; nm.lpszLongNameA = 0;
        if (!cb(copy[i].id, 1 /* DPPLAYERTYPE_PLAYER */, &nm, 0, ctx)) break;
    }
    return DP_OK;
}

static HRESULT STDCALL m_EnumSessions(Obj *t, DPSESSIONDESC2 *sd, u32 timeout, EnumSessCB cb, void *ctx, u32 flags) {
    (void)t; (void)sd;
    ListEntry copy[MAX_LIST]; u32 n = 0;
    if (flags & DPENUMSESSIONS_STOPASYNC) return DP_OK;
    plat_lock();
    if (ensure_connected() == 0) {
        u32 now = plat_ms();
        if (!G.list_valid || now - G.list_at >= LIST_CACHE_MS) {
            if (request(C_LIST, 0, 0, S_LIST, 2000) == 0 && G.resp_len >= 1) {
                u32 cnt = G.resp[0], o = 1, k = 0;
                for (u32 i = 0; i < cnt && o + 23 <= G.resp_len; i++) {
                    u32 nl = G.resp[o + 22];
                    if (o + 23 + nl > G.resp_len) break;
                    if (k < MAX_LIST) {
                        mcpy(&G.list[k].inst, G.resp + o, 16);
                        G.list[k].flags = rd32(G.resp + o + 16);
                        G.list[k].cur = G.resp[o + 20]; G.list[k].max = G.resp[o + 21];
                        u32 c = nl > NAME_MAX_ ? NAME_MAX_ : nl;
                        mcpy(G.list[k].name, G.resp + o + 23, c); G.list[k].name[c] = 0;
                        k++;
                    }
                    o += 23 + nl;
                }
                u32 hidden = o < G.resp_len ? G.resp[o] : 0;
                if (!G.list_valid || k != G.nlist) {
                    plat_log("session list: %d game(s)", (int)k);
                    for (u32 i = 0; i < k; i++) plat_log("  \"%s\" %d/%d", G.list[i].name, G.list[i].cur, G.list[i].max);
                    if (hidden) plat_log("  %d more game(s) hidden: their game files differ from yours", (int)hidden);
                }
                G.nlist = k; G.list_at = now; G.list_valid = 1;
            }
        }
        if (G.list_valid) { n = G.nlist; mcpy(copy, G.list, sizeof copy); }
    }
    plat_unlock();
    if (!cb) return E_INVALIDARG;
    for (u32 i = 0; i < n; i++) {
        DPSESSIONDESC2 d; mzero(&d, sizeof d);
        d.dwSize = sizeof d; d.dwFlags = copy[i].flags; d.guidInstance = copy[i].inst; d.guidApplication = APP_GUID;
        d.dwMaxPlayers = copy[i].max; d.dwCurrentPlayers = copy[i].cur; d.lpszSessionNameA = copy[i].name;
        if (!cb(&d, &timeout, 0, ctx)) return DP_OK;
    }
    cb(0, &timeout, DPESC_TIMEDOUT, ctx);
    return DP_OK;
}

static HRESULT STDCALL m_GetSessionDesc(Obj *t, void *data, u32 *size) {
    (void)t;
    if (!size) return E_INVALIDARG;
    plat_lock();
    pump(0);
    u32 nl = slen(G.sd_name) + 1, need = sizeof(DPSESSIONDESC2) + nl;
    HRESULT hr = DP_OK;
    /* the game passes a 128-byte buffer; shorten the name instead of failing on long ones */
    if (data && *size > sizeof(DPSESSIONDESC2) && *size < need) { nl = *size - sizeof(DPSESSIONDESC2); need = *size; }
    if (!data || *size < need) { *size = need; hr = DPERR_BUFFERTOOSMALL; }
    else {
        DPSESSIONDESC2 *d = data; mzero(d, sizeof *d);
        d->dwSize = sizeof *d; d->dwFlags = G.sd_flags; d->guidInstance = G.inst; d->guidApplication = APP_GUID;
        d->dwMaxPlayers = G.sd_max; d->dwCurrentPlayers = G.nroster;
        d->lpszSessionNameA = (char *)(d + 1); mcpy(d->lpszSessionNameA, G.sd_name, nl);
        d->lpszSessionNameA[nl - 1] = 0;
        *size = need;
    }
    plat_unlock();
    return hr;
}

static HRESULT STDCALL m_SetSessionDesc(Obj *t, DPSESSIONDESC2 *sd, u32 flags) {
    (void)t; (void)flags;
    if (!sd) return E_INVALIDARG;
    plat_lock();
    if (sd->dwFlags != G.sd_flags) {
        G.sd_flags = sd->dwFlags;
        plat_log("SetSessionDesc flags %x%s", G.sd_flags, (G.sd_flags & 0x20) ? " (lobby locked)" : "");
        if (G.sock >= 0 && G.in_session) { u8 b[4]; wr32(b, G.sd_flags); send_frame(C_SETFLAGS, b, 4); }
    }
    plat_unlock();
    return DP_OK;
}

static HRESULT STDCALL m_Open(Obj *t, DPSESSIONDESC2 *sd, u32 flags) {
    (void)t;
    if (!sd) return E_INVALIDARG;
    HRESULT hr;
    plat_lock();
    if (ensure_connected() != 0) {
        if (!(flags & DPOPEN_CREATE)) { plat_unlock(); return DPERR_NOSESSIONS; }
        /* the game crashes if hosting fails, so open a lobby nobody else can see instead */
        session_clear();
        G.offline = 1; G.in_session = 1; G.sd_flags = sd->dwFlags; G.sd_max = 8; mzero(&G.inst, sizeof G.inst);
        scpy(G.sd_name, sd->lpszSessionNameA, sizeof G.sd_name);
        plat_log("RELAY NOT REACHABLE at %s:%d - hosting offline; nobody can see or join this game", G.host, G.port);
        plat_unlock();
        return DP_OK;
    }
    session_clear();
    if (flags & DPOPEN_CREATE) {
        u8 b[6 + NAME_MAX_]; u32 n = slen(sd->lpszSessionNameA); if (n > NAME_MAX_) n = NAME_MAX_;
        wr32(b, sd->dwFlags); b[4] = (u8)(sd->dwMaxPlayers > 8 || !sd->dwMaxPlayers ? 8 : sd->dwMaxPlayers); b[5] = (u8)n;
        mcpy(b + 6, sd->lpszSessionNameA, n);
        hr = DPERR_CANTCREATESESSION;
        if (request(C_CREATE, b, 6 + n, S_CREATE, 3000) == 0 && G.resp_len >= 17 && G.resp[0] == 0) {
            mcpy(&G.inst, G.resp + 1, 16); G.sd_flags = sd->dwFlags; G.sd_max = b[4];
            mcpy(G.sd_name, sd->lpszSessionNameA, n); G.sd_name[n] = 0;
            G.in_session = 1; hr = DP_OK;
            plat_log("hosting \"%s\" (max %d players)", G.sd_name, (int)G.sd_max);
        } else plat_log("host failed");
    } else {
        hr = DPERR_NOSESSIONS;
        if (request(C_JOIN, (const u8 *)&sd->guidInstance, 16, S_JOIN, 3000) == 0 && G.resp_len >= 7) {
            static const char *why[] = {"ok", "that game no longer exists", "the game is full", "the game has already started", "your game files differ from the host's"};
            u32 st = G.resp[0];
            if (st == 0) {
                G.inst = sd->guidInstance; G.sd_flags = rd32(G.resp + 1); G.sd_max = G.resp[5];
                u32 nl = G.resp[6]; if (nl > NAME_MAX_) nl = NAME_MAX_; if (7 + nl > G.resp_len) nl = 0;
                mcpy(G.sd_name, G.resp + 7, nl); G.sd_name[nl] = 0;
                G.in_session = 1; hr = DP_OK;
                plat_log("joined \"%s\"", G.sd_name);
                pump(0);
            } else plat_log("join refused: %s", st < 5 ? why[st] : "unknown reason");
        } else plat_log("join failed: no answer from relay");
    }
    G.list_valid = 0;
    plat_unlock();
    return hr;
}

static HRESULT STDCALL m_Receive(Obj *t, u32 *from, u32 *to, u32 flags, void *data, u32 *size) {
    (void)t;
    if (!size) return E_INVALIDARG;
    HRESULT hr;
    plat_lock();
    if (!G.qhead) pump(0);
    Msg *m = G.qhead;
    if (!m) hr = DPERR_NOMESSAGES;
    else if (!data || *size < m->len) { *size = m->len; hr = DPERR_BUFFERTOOSMALL; }
    else {
        mcpy(data, m->data, m->len); *size = m->len;
        if (from) *from = m->from;
        if (to) *to = m->to ? m->to : G.my_id;
        if (!(flags & DPRECEIVE_PEEK)) {
            G.qhead = m->next; if (!G.qhead) G.qtail = 0;
            G.qcount--; plat_free(m);
        }
        hr = DP_OK;
    }
    u32 now = plat_ms();
    if (G.in_session && now - G.last_stat >= 30000) { G.last_stat = now; plat_log("traffic: sent %d, received %d packets", (int)G.sent, (int)G.recvd); }
    plat_unlock();
    return hr;
}

static HRESULT STDCALL m_Send(Obj *t, u32 from, u32 to, u32 flags, void *data, u32 size) {
    (void)t; (void)from; (void)flags;
    if (!data || !size) return E_INVALIDARG;
    if (size > MAX_PAYLOAD) return DPERR_SENDTOOBIG;
    static u8 b[4 + MAX_PAYLOAD];
    HRESULT hr = DP_OK;
    plat_lock();
    if (G.offline) hr = DP_OK;
    else if (G.sock < 0 || !G.in_session || !G.my_id) hr = DPERR_NOCONNECTION;
    else {
        wr32(b, to); mcpy(b + 4, data, size);
        if (send_frame(C_SEND, b, 4 + size) != 0) hr = DPERR_NOCONNECTION; else G.sent++;
    }
    plat_unlock();
    return hr;
}

static HRESULT STDCALL m_EnumConnections(Obj *t, const GUID *app, EnumConnCB cb, void *ctx, u32 flags) {
    (void)t; (void)app; (void)flags;
    if (!cb) return E_INVALIDARG;
    /* compound address: [TotalSize][ServiceProvider = TCP/IP], as real DirectPlay hands out */
    u8 *a = G.addr; u32 total = 20 + 4 + 20 + 16;
    mcpy(a, &DPAID_TotalSize, 16); wr32(a + 16, 4); wr32(a + 20, total);
    mcpy(a + 24, &DPAID_ServiceProvider, 16); wr32(a + 40, 16); mcpy(a + 44, &SP_TCPIP, 16);
    DPNAME nm; nm.dwSize = sizeof nm; nm.dwFlags = 0; nm.lpszShortNameA = "Internet TCP/IP Connection For DirectPlay"; nm.lpszLongNameA = 0;
    cb(&SP_TCPIP, a, total, &nm, 1 /* DPCONNECTION_DIRECTPLAY */, ctx);
    return DP_OK;
}

static HRESULT STDCALL m_InitializeConnection(Obj *t, void *conn, u32 flags) {
    (void)t; (void)flags;
    /* The game passes TCP/IP plus whatever was typed in its "Search IP" box. A typed address is
     * used as the relay's address; a blank box means the relay from mfnet.ini. */
    const u8 *p = conn; char ip[64]; ip[0] = 0;
    if (p && geq((const GUID *)p, &DPAID_TotalSize)) {
        u32 total = rd32(p + 20), o = 24;
        while (total <= 4096 && o + 20 <= total) {
            u32 sz = rd32(p + o + 16);
            if (o + 20 + sz > total) break;
            if (geq((const GUID *)(p + o), &DPAID_INet)) scpy(ip, (const char *)(p + o + 20), sz + 1 < sizeof ip ? sz + 1 : sizeof ip);
            o += 20 + sz;
        }
    }
    plat_lock();
    const char *want = ip[0] ? ip : G.def_host; int same = 1;
    for (u32 i = 0;; i++) { if (want[i] != G.host[i]) { same = 0; break; } if (!want[i]) break; }
    if (!same) {
        if (G.sock >= 0) plat_close(G.sock);
        G.sock = -1; G.rxlen = 0; G.list_valid = 0; G.failed = 0;
        session_clear();
        scpy(G.host, want, sizeof G.host);
    }
    plat_log("InitializeConnection: relay is %s:%d%s", G.host, G.port, ip[0] ? " (typed in the Search IP box)" : " (from mfnet.ini)");
    plat_unlock();
    return DP_OK;
}

/* Everything below is never called by the game; each stub pops the right number of arguments. */
#define STUB(name, ...) static HRESULT STDCALL name(Obj *t, ##__VA_ARGS__) { (void)t; plat_log("unsupported DirectPlay call: " #name); return E_NOTIMPL; }
typedef u32 A; typedef void *P;
STUB(u_AddPlayerToGroup, A a, A b) STUB(u_CreateGroup, P a, P b, P c, A d, A e)
STUB(u_DeletePlayerFromGroup, A a, A b) STUB(u_DestroyGroup, A a)
STUB(u_EnumGroupPlayers, A a, P b, P c, P d, A e) STUB(u_EnumGroups, P a, P b, P c, A d)
STUB(u_GetCaps, P a, A b) STUB(u_GetGroupData, A a, P b, P c, A d) STUB(u_GetGroupName, A a, P b, P c)
STUB(u_GetMessageCount, A a, P b) STUB(u_GetPlayerAddress, A a, P b, P c) STUB(u_GetPlayerCaps, A a, P b, A c)
STUB(u_GetPlayerData, A a, P b, P c, A d) STUB(u_GetPlayerName, A a, P b, P c) STUB(u_Initialize, P a)
STUB(u_SetGroupData, A a, P b, A c, A d) STUB(u_SetGroupName, A a, P b, A c)
STUB(u_SetPlayerData, A a, P b, A c, A d) STUB(u_SetPlayerName, A a, P b, A c)
STUB(u_AddGroupToGroup, A a, A b) STUB(u_CreateGroupInGroup, A a, P b, P c, P d, A e, A f)
STUB(u_DeleteGroupFromGroup, A a, A b) STUB(u_EnumGroupsInGroup, A a, P b, P c, P d, A e)
STUB(u_GetGroupConnectionSettings, A a, A b, P c, P d) STUB(u_SecureOpen, P a, A b, P c, P d)
STUB(u_SendChatMessage, A a, A b, A c, P d) STUB(u_SetGroupConnectionSettings, A a, A b, P c)
STUB(u_StartSession, A a, A b) STUB(u_GetGroupFlags, A a, P b) STUB(u_GetGroupParent, A a, P b)
STUB(u_GetPlayerAccount, A a, A b, P c, P d) STUB(u_GetPlayerFlags, A a, P b)
STUB(u_GetGroupOwner, A a, P b) STUB(u_SetGroupOwner, A a, A b)
STUB(u_SendEx, A a, A b, A c, P d, A e, A f, A g, P h, P i) STUB(u_GetMessageQueue, A a, A b, A c, P d, P e)
STUB(u_CancelMessage, A a, A b) STUB(u_CancelPriority, A a, A b, A c)

static void *g_vtbl[53] = {
    m_QueryInterface, m_AddRef, m_Release,
    u_AddPlayerToGroup, m_Close, u_CreateGroup, m_CreatePlayer, u_DeletePlayerFromGroup, u_DestroyGroup,
    m_DestroyPlayer, u_EnumGroupPlayers, u_EnumGroups, m_EnumPlayers, m_EnumSessions, u_GetCaps,
    u_GetGroupData, u_GetGroupName, u_GetMessageCount, u_GetPlayerAddress, u_GetPlayerCaps, u_GetPlayerData,
    u_GetPlayerName, m_GetSessionDesc, u_Initialize, m_Open, m_Receive, m_Send, u_SetGroupData,
    u_SetGroupName, u_SetPlayerData, u_SetPlayerName, m_SetSessionDesc,
    u_AddGroupToGroup, u_CreateGroupInGroup, u_DeleteGroupFromGroup, m_EnumConnections, u_EnumGroupsInGroup,
    u_GetGroupConnectionSettings, m_InitializeConnection, u_SecureOpen, u_SendChatMessage,
    u_SetGroupConnectionSettings, u_StartSession, u_GetGroupFlags, u_GetGroupParent, u_GetPlayerAccount,
    u_GetPlayerFlags,
    u_GetGroupOwner, u_SetGroupOwner, u_SendEx, u_GetMessageQueue, u_CancelMessage, u_CancelPriority,
};

void *mfnet_create(void) {
    plat_lock();
    disconnect();              /* the game makes a fresh object for every attempt */
    G.sent = G.recvd = 0;
    g_obj.vtbl = g_vtbl; g_obj.ref = 1;
    plat_log("DirectPlay object created (stand-in)");
    plat_unlock();
    return &g_obj;
}

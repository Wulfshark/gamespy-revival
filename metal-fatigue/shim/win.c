/* mfnet Windows glue: loads as DINPUT.dll beside MFatigue.exe, forwards DirectInput to the
 * real system DLL, and redirects the game's CoCreateInstance(CLSID_DirectPlay) to the stand-in.
 * Freestanding: no CRT and no Windows SDK headers, so the few APIs used are declared here. */
#include "mfnet.h"

typedef void *HANDLE;
#define W STDCALL
__declspec(dllimport) HANDLE W GetModuleHandleA(const char *);
__declspec(dllimport) void *W GetProcAddress(HANDLE, const char *);
__declspec(dllimport) HANDLE W LoadLibraryA(const char *);

static struct {
    u32 (W *GetModuleFileNameA)(HANDLE, char *, u32);
    int (W *VirtualProtect)(void *, u32, u32, u32 *);
    HANDLE (W *GetProcessHeap)(void);
    void *(W *HeapAlloc)(HANDLE, u32, u32);
    int (W *HeapFree)(HANDLE, u32, void *);
    HANDLE (W *CreateFileA)(const char *, u32, u32, void *, u32, u32, HANDLE);
    int (W *WriteFile)(HANDLE, const void *, u32, u32 *, void *);
    int (W *ReadFile)(HANDLE, void *, u32, u32 *, void *);
    int (W *CloseHandle)(HANDLE);
    u32 (W *SetFilePointer)(HANDLE, i32, i32 *, u32);
    u32 (W *GetTickCount)(void);
    u32 (W *GetPrivateProfileStringA)(const char *, const char *, const char *, char *, u32, const char *);
    u32 (W *GetPrivateProfileIntA)(const char *, const char *, i32, const char *);
    u32 (W *GetSystemDirectoryA)(char *, u32);
    void (W *InitializeCriticalSection)(void *);
    void (W *EnterCriticalSection)(void *);
    void (W *LeaveCriticalSection)(void *);
    void (W *GetLocalTime)(void *);
    u32 (W *GetCurrentProcessId)(void);
    int (W *wvsprintfA)(char *, const char *, __builtin_va_list);
    i32 (W *DefWindowProcA)(HANDLE, u32, u32, i32);
} K;

static struct {
    int (W *WSAStartup)(u16, void *);
    int (W *WSAGetLastError)(void);
    u32 (W *socket)(int, int, int);
    int (W *connect)(u32, const void *, int);
    int (W *send)(u32, const void *, int, int);
    int (W *recv)(u32, void *, int, int);
    int (W *closesocket)(u32);
    int (W *select)(int, void *, void *, void *, void *);
    int (W *ioctlsocket)(u32, i32, u32 *);
    int (W *setsockopt)(u32, int, int, const void *, int);
    u32 (W *inet_addr)(const char *);
    u16 (W *htons)(u16);
    void *(W *gethostbyname)(const char *);
    int ok;
} S;

static u8 g_cs[64];
static char g_dir[300], g_ini[320], g_logpath[320], g_exe[300];
static int g_log = 1, g_inited;
static HRESULT (W *g_realCoCreate)(const GUID *, void *, u32, const GUID *, void **);
static const GUID CLSID_DirectPlay = {0xD1EB6D20, 0x8923, 0x11d0, {0x9D, 0x97, 0x00, 0xA0, 0xC9, 0x0A, 0x43, 0xCB}};

/* clang may emit calls to these even in freestanding mode */
void *memcpy(void *d, const void *s, u32 n) { u8 *a = d; const u8 *b = s; while (n--) *a++ = *b++; return d; }
void *memset(void *d, int c, u32 n) { u8 *a = d; while (n--) *a++ = (u8)c; return d; }

static u32 zlen(const char *s) { u32 n = 0; while (s[n]) n++; return n; }
static void zcat(char *d, const char *s, u32 cap) { u32 n = zlen(d); while (*s && n + 1 < cap) d[n++] = *s++; d[n] = 0; }
static int ieq(const char *a, const char *b) {
    for (;; a++, b++) { char x = *a, y = *b; if (x >= 'A' && x <= 'Z') x += 32; if (y >= 'A' && y <= 'Z') y += 32; if (x != y) return 0; if (!x) return 1; }
}

void plat_lock(void) { K.EnterCriticalSection(g_cs); }
void plat_unlock(void) { K.LeaveCriticalSection(g_cs); }
u32 plat_ms(void) { return K.GetTickCount(); }
void *plat_alloc(u32 n) { return K.HeapAlloc(K.GetProcessHeap(), 0, n); }
void plat_free(void *p) { K.HeapFree(K.GetProcessHeap(), 0, p); }

static int fmt(char *out, const char *f, ...) {
    __builtin_va_list ap; __builtin_va_start(ap, f); int n = K.wvsprintfA(out, f, ap); __builtin_va_end(ap); return n;
}

void plat_log(const char *f, ...) {
    static char line[1200];
    if (!g_log || !K.wvsprintfA) return;
    K.EnterCriticalSection(g_cs);
    u16 st[8]; K.GetLocalTime(st);
    int n = fmt(line, "%02d:%02d:%02d.%03d  [%d] ", (int)st[4], (int)st[5], (int)st[6], (int)st[7], (int)K.GetCurrentProcessId());
    __builtin_va_list ap; __builtin_va_start(ap, f);
    n += K.wvsprintfA(line + n, f, ap);   /* wvsprintfA never writes more than 1024 characters */
    __builtin_va_end(ap);
    line[n++] = '\r'; line[n++] = '\n';
    HANDLE h = K.CreateFileA(g_logpath, 0x40000000 /* GENERIC_WRITE */, 3, 0, 4 /* OPEN_ALWAYS */, 0x80, 0);
    if (h != (HANDLE)-1) { u32 wr; K.SetFilePointer(h, 0, 0, 2 /* FILE_END */); K.WriteFile(h, line, (u32)n, &wr, 0); K.CloseHandle(h); }
    K.LeaveCriticalSection(g_cs);
}

/* ---- sockets ---- */
static int sock_init(void) {
    if (S.ok) return 0;
    HANDLE w = LoadLibraryA("ws2_32.dll");
    if (!w) return -1;
#define R(n) if (!(*(void **)&S.n = GetProcAddress(w, #n))) return -1;
    R(WSAStartup) R(WSAGetLastError) R(socket) R(connect) R(send) R(recv) R(closesocket) R(select)
    R(ioctlsocket) R(setsockopt) R(inet_addr) R(htons) R(gethostbyname)
#undef R
    static u8 wsadata[512];
    if (S.WSAStartup(0x0202, wsadata) != 0) return -1;
    S.ok = 1;
    return 0;
}

typedef struct { u32 count; u32 arr[64]; } FdSet;
typedef struct { i32 sec, usec; } TimeVal;

int plat_connect(const char *host, int port, int timeout_ms) {
    if (sock_init() != 0) { plat_log("winsock unavailable"); return -1; }
    u32 ip = S.inet_addr(host);
    if (ip == 0xffffffffu) {
        void **he = S.gethostbyname(host);   /* hostent: name, aliases, type/len, addr_list */
        if (!he) { plat_log("cannot resolve %s", host); return -1; }
        u8 **list = (u8 **)he[3];
        if (!list || !list[0]) return -1;
        memcpy(&ip, list[0], 4);
    }
    u32 s = S.socket(2, 1, 6);
    if (s == 0xffffffffu) return -1;
    u32 one = 1;
    S.ioctlsocket(s, (i32)0x8004667E /* FIONBIO */, &one);
    struct { u16 fam, port; u32 addr; u8 zero[8]; } sa;
    memset(&sa, 0, sizeof sa); sa.fam = 2; sa.port = S.htons((u16)port); sa.addr = ip;
    if (S.connect(s, &sa, sizeof sa) != 0) {
        if (S.WSAGetLastError() != 10035 /* WSAEWOULDBLOCK */) { S.closesocket(s); return -1; }
        FdSet wr, ex; wr.count = 1; wr.arr[0] = s; ex.count = 1; ex.arr[0] = s;
        TimeVal tv; tv.sec = timeout_ms / 1000; tv.usec = (timeout_ms % 1000) * 1000;
        int r = S.select(0, 0, &wr, &ex, &tv);
        if (r <= 0 || wr.count == 0) { S.closesocket(s); return -1; }
    }
    S.setsockopt(s, 6, 1 /* TCP_NODELAY */, &one, 4);
    return (int)s;
}

int plat_send(int s, const void *p, int n, int timeout_ms) {
    const u8 *b = p;
    while (n > 0) {
        int r = S.send((u32)s, b, n, 0);
        if (r > 0) { b += r; n -= r; continue; }
        if (S.WSAGetLastError() != 10035) return -1;
        FdSet wr; wr.count = 1; wr.arr[0] = (u32)s;
        TimeVal tv; tv.sec = timeout_ms / 1000; tv.usec = (timeout_ms % 1000) * 1000;
        if (S.select(0, 0, &wr, 0, &tv) <= 0) return -1;
    }
    return 0;
}

int plat_recv(int s, void *p, int n) {
    int r = S.recv((u32)s, p, n, 0);
    if (r > 0) return r;
    if (r == 0) return -1;
    return S.WSAGetLastError() == 10035 ? 0 : -1;
}

int plat_wait(int s, int timeout_ms) {
    FdSet rd; rd.count = 1; rd.arr[0] = (u32)s;
    if (timeout_ms < 0) timeout_ms = 0;
    TimeVal tv; tv.sec = timeout_ms / 1000; tv.usec = (timeout_ms % 1000) * 1000;
    return S.select(0, &rd, 0, 0, &tv) > 0;
}

void plat_close(int s) { S.closesocket((u32)s); }

/* RtlGenRandom (advapi32 SystemFunction036): the system's secure random source */
typedef unsigned char (W *GenRandomFn)(void *, u32);
int plat_random(u8 *p, u32 n) {
    static GenRandomFn gen;
    if (!gen) { HANDLE a = LoadLibraryA("advapi32.dll"); if (a) *(void **)&gen = GetProcAddress(a, "SystemFunction036"); }
    if (!gen) return -1;
    return gen(p, n) ? 0 : -1;
}

/* ---- setup ---- */
static void fingerprint(char *out) {
    static u8 buf[4096];
    u32 h1 = 0x811C9DC5u, h2 = 0x01000193u ^ 0xDEADBEEFu, total = 0;
    HANDLE h = K.CreateFileA(g_exe, 0x80000000u /* GENERIC_READ */, 1, 0, 3 /* OPEN_EXISTING */, 0, 0);
    if (h != (HANDLE)-1) {
        u32 got;
        while (K.ReadFile(h, buf, sizeof buf, &got, 0) && got) {
            for (u32 i = 0; i < got; i++) { h1 = (h1 ^ buf[i]) * 0x01000193u; h2 = (h2 ^ buf[i]) * 0x01000193u + (h2 >> 27); }
            total += got;
        }
        K.CloseHandle(h);
    }
    fmt(out, "%08x%08x-%d", h1, h2, (int)total);
}

static void late_init(void) {
    if (g_inited) return;
    g_inited = 1;
    char relay[160], fp[64], room[64]; int port = 27900;
    K.GetPrivateProfileStringA("mfnet", "relay", "127.0.0.1:27900", relay, sizeof relay, g_ini);
    for (u32 i = 0; relay[i]; i++) if (relay[i] == ':') {
        relay[i] = 0; port = 0;
        for (const char *d = relay + i + 1; *d >= '0' && *d <= '9'; d++) port = port * 10 + (*d - '0');
        break;
    }
    if (port <= 0 || port > 65535) port = 27900;
    fingerprint(fp);
    K.GetPrivateProfileStringA("mfnet", "room", "", room, sizeof room, g_ini);
    mfnet_config(relay, port, fp, room);
    plat_log("relay %s:%d, game files %s", relay, port, fp);
}

static HRESULT W hook_CoCreateInstance(const GUID *clsid, void *outer, u32 ctx, const GUID *iid, void **out) {
    if (clsid && out) {
        const u8 *a = (const u8 *)clsid, *b = (const u8 *)&CLSID_DirectPlay; int same = 1;
        for (int i = 0; i < 16; i++) if (a[i] != b[i]) { same = 0; break; }
        if (same) { late_init(); *out = mfnet_create(); return 0; }
    }
    return g_realCoCreate(clsid, outer, ctx, iid, out);
}

/* Finds the game's import slot for ole32!CoCreateInstance by name and points it at the hook. */
static int hook_iat(void) {
    u8 *base = (u8 *)GetModuleHandleA(0);
    if (!base || base[0] != 'M' || base[1] != 'Z') return 0;
    u8 *nt = base + *(u32 *)(base + 0x3c);
    u32 rva = *(u32 *)(nt + 0x80);
    if (!rva) return 0;
    for (u32 *d = (u32 *)(base + rva); d[3]; d += 5) {
        if (!ieq((const char *)(base + d[3]), "ole32.dll") || !d[0]) continue;
        u32 *names = (u32 *)(base + d[0]), *slots = (u32 *)(base + d[4]);
        for (u32 i = 0; names[i]; i++) {
            if (names[i] & 0x80000000u) continue;
            if (!ieq((const char *)(base + names[i] + 2), "CoCreateInstance")) continue;
            u32 old;
            *(void **)&g_realCoCreate = (void *)slots[i];
            if (!K.VirtualProtect(&slots[i], 4, 4 /* PAGE_READWRITE */, &old)) return 0;
            slots[i] = (u32)hook_CoCreateInstance;
            K.VirtualProtect(&slots[i], 4, old, &old);
            return 1;
        }
    }
    return 0;
}

/* ---- keep the game running when it is not the active window ----
 * The game's main loop skips the whole frame, network included, while its "app active" flag is
 * clear, so an Alt-Tabbed player stops answering and everyone drops. The flag is cleared only by
 * WM_ACTIVATEAPP(FALSE) in the window procedure at 0x40b200. Wrapping that procedure and keeping
 * the deactivation message away from the game leaves it running in the background. */
#define GAME_WNDPROC 0x40b200u
#define WNDPROC_SITE 0x40b536u   /* mov dword ptr [esp+0x28], 0x40b200  (WNDCLASS.lpfnWndProc) */

static i32 W mf_wndproc(HANDLE wnd, u32 msg, u32 wp, i32 lp) {
    if (msg == 0x1C /* WM_ACTIVATEAPP */ && wp == 0) {
        static int said;
        if (!said) { said = 1; plat_log("window lost focus; game kept running"); }
        return K.DefWindowProcA(wnd, msg, wp, lp);
    }
    return ((i32 (W *)(HANDLE, u32, u32, i32))GAME_WNDPROC)(wnd, msg, wp, lp);
}

static int hook_wndproc(void) {
    static const u8 expect[8] = {0xC7, 0x44, 0x24, 0x28, 0x00, 0xB2, 0x40, 0x00};
    u8 *base = (u8 *)GetModuleHandleA(0), *site = (u8 *)WNDPROC_SITE;
    if (base != (u8 *)0x400000u) return 0;
    for (int i = 0; i < 8; i++) if (site[i] != expect[i]) return 0;
    u32 old;
    if (!K.VirtualProtect(site, 8, 0x40 /* PAGE_EXECUTE_READWRITE */, &old)) return 0;
    *(u32 *)(site + 4) = (u32)mf_wndproc;
    K.VirtualProtect(site, 8, old, &old);
    return 1;
}

int W DllMain(HANDLE inst, u32 reason, void *reserved) {
    (void)inst; (void)reserved;
    if (reason != 1 /* DLL_PROCESS_ATTACH */) return 1;
    HANDLE k = GetModuleHandleA("kernel32.dll"), u = LoadLibraryA("user32.dll");
    if (!k || !u) return 1;
#define R(n) if (!(*(void **)&K.n = GetProcAddress(k, #n))) return 1;
    R(GetModuleFileNameA) R(VirtualProtect) R(GetProcessHeap) R(HeapAlloc) R(HeapFree) R(CreateFileA)
    R(WriteFile) R(ReadFile) R(CloseHandle) R(SetFilePointer) R(GetTickCount) R(GetPrivateProfileStringA)
    R(GetPrivateProfileIntA) R(GetSystemDirectoryA) R(InitializeCriticalSection) R(EnterCriticalSection)
    R(LeaveCriticalSection) R(GetLocalTime) R(GetCurrentProcessId)
#undef R
    K.InitializeCriticalSection(g_cs);
    if (!(*(void **)&K.wvsprintfA = GetProcAddress(u, "wvsprintfA"))) return 1;
    if (!(*(void **)&K.DefWindowProcA = GetProcAddress(u, "DefWindowProcA"))) return 1;

    u32 n = K.GetModuleFileNameA(0, g_exe, sizeof g_exe - 1);
    g_exe[n] = 0;
    memcpy(g_dir, g_exe, n + 1);
    while (n && g_dir[n - 1] != '\\' && g_dir[n - 1] != '/') n--;
    g_dir[n] = 0;
    memcpy(g_ini, g_dir, n + 1); zcat(g_ini, "mfnet.ini", sizeof g_ini);
    memcpy(g_logpath, g_dir, n + 1); zcat(g_logpath, "mfnet.log", sizeof g_logpath);

    g_log = (int)K.GetPrivateProfileIntA("mfnet", "log", 1, g_ini);
    int enabled = (int)K.GetPrivateProfileIntA("mfnet", "enabled", 1, g_ini);
    plat_log("---- mfnet 0.5 loaded in %s ----", g_exe);
    if (!enabled) { plat_log("disabled in mfnet.ini; the game will use normal DirectPlay"); return 1; }
    if (hook_iat()) plat_log("DirectPlay creation redirected to the relay stand-in");
    else plat_log("could not find the game's DirectPlay import; the game will use normal DirectPlay");
    if (K.GetPrivateProfileIntA("mfnet", "run_in_background", 1, g_ini)) {
        if (hook_wndproc()) plat_log("run in background: on");
        else plat_log("run in background: this MFatigue.exe is not the build mfnet knows, left alone");
    }
    return 1;
}

/* ---- DirectInput pass-through ---- */
static void *real_dinput(const char *name) {
    static HANDLE h;
    if (!h) {
        char path[320];
        u32 n = K.GetSystemDirectoryA(path, 260);
        path[n] = 0; zcat(path, "\\dinput.dll", sizeof path);
        h = LoadLibraryA(path);
        if (!h) { plat_log("cannot load %s", path); return 0; }
    }
    return GetProcAddress(h, name);
}

HRESULT W mf_DirectInputCreateA(HANDLE inst, u32 ver, void **out, void *outer) {
    static HRESULT (W *f)(HANDLE, u32, void **, void *);
    if (!f) *(void **)&f = real_dinput("DirectInputCreateA");
    return f ? f(inst, ver, out, outer) : E_NOTIMPL;
}
HRESULT W mf_DirectInputCreateW(HANDLE inst, u32 ver, void **out, void *outer) {
    static HRESULT (W *f)(HANDLE, u32, void **, void *);
    if (!f) *(void **)&f = real_dinput("DirectInputCreateW");
    return f ? f(inst, ver, out, outer) : E_NOTIMPL;
}
HRESULT W mf_DirectInputCreateEx(HANDLE inst, u32 ver, const GUID *iid, void **out, void *outer) {
    static HRESULT (W *f)(HANDLE, u32, const GUID *, void **, void *);
    if (!f) *(void **)&f = real_dinput("DirectInputCreateEx");
    return f ? f(inst, ver, iid, out, outer) : E_NOTIMPL;
}

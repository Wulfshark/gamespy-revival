/* mfnet: DirectPlay stand-in for Metal Fatigue. Portable core + small platform layer. */
#ifndef MFNET_H
#define MFNET_H

typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned int u32;
typedef int i32;

#if defined(__i386__)
#define STDCALL __attribute__((stdcall))
#else
#define STDCALL
#endif

typedef struct { u32 d1; u16 d2, d3; u8 d4[8]; } GUID;
typedef i32 HRESULT;

#define DP_OK 0
#define E_NOINTERFACE ((HRESULT)0x80004002)
#define E_NOTIMPL ((HRESULT)0x80004001)
#define E_INVALIDARG ((HRESULT)0x80070057)
#define DPERR(n) ((HRESULT)(0x88770000u + (n)))
#define DPERR_BUFFERTOOSMALL DPERR(30)
#define DPERR_CANTCREATEPLAYER DPERR(60)
#define DPERR_CANTCREATESESSION DPERR(70)
#define DPERR_INVALIDPLAYER DPERR(150)
#define DPERR_NOCONNECTION DPERR(170)
#define DPERR_NOMESSAGES DPERR(190)
#define DPERR_NOSESSIONS DPERR(220)
#define DPERR_SENDTOOBIG DPERR(230)

#define DPOPEN_JOIN 1
#define DPOPEN_CREATE 2
#define DPRECEIVE_PEEK 8
#define DPESC_TIMEDOUT 1
#define DPENUMSESSIONS_STOPASYNC 0x20

typedef struct {
    u32 dwSize, dwFlags;
    GUID guidInstance, guidApplication;
    u32 dwMaxPlayers, dwCurrentPlayers;
    char *lpszSessionNameA, *lpszPasswordA;
    u32 dwReserved1, dwReserved2, dwUser1, dwUser2, dwUser3, dwUser4;
} DPSESSIONDESC2;

typedef struct { u32 dwSize, dwFlags; char *lpszShortNameA, *lpszLongNameA; } DPNAME;

typedef int (STDCALL *EnumConnCB)(const GUID *sp, void *conn, u32 size, const DPNAME *name, u32 flags, void *ctx);
typedef int (STDCALL *EnumSessCB)(const DPSESSIONDESC2 *sd, u32 *timeout, u32 flags, void *ctx);
typedef int (STDCALL *EnumPlayCB)(u32 id, u32 type, const DPNAME *name, u32 flags, void *ctx);

/* core */
void *mfnet_create(void);                 /* returns an IDirectPlay4A-compatible object */
void mfnet_config(const char *host, int port, const char *fingerprint, const char *room);

/* platform layer */
int  plat_connect(const char *host, int port, int timeout_ms); /* socket or -1; non-blocking, nodelay */
int  plat_send(int s, const void *p, int n, int timeout_ms);   /* 0 ok, -1 failed */
int  plat_recv(int s, void *p, int n);    /* >0 bytes, 0 nothing yet, -1 closed/error */
int  plat_wait(int s, int timeout_ms);    /* 1 readable, 0 timeout */
void plat_close(int s);
u32  plat_ms(void);
void *plat_alloc(u32 n);
void plat_free(void *p);
void plat_lock(void);
void plat_unlock(void);
void plat_log(const char *fmt, ...);

#endif

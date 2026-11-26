/* Stand-in for shell32 ShellExecuteEx and the token queries Elevation.Of uses.
 *
 * ShellExecuteEx behaviour modelled from Wine 10.0 dlls/shell32/shlexec.c (not the
 * Windows 11 binary this product ships, which is not published):
 *   - SHELL_FindExecutable copies an existing absolute lpFile into the result and,
 *     when the extension is a program extension such as "exe", returns success
 *     (shlexec.c around the "programs" list and the earlier lstrcpyW(lpResult, xlpFile)).
 *   - SHELL_quote_and_execute quotes that path and calls SHELL_ExecuteW.
 *   - SHELL_ExecuteW, when lpVerb compares equal to "runas", takes the linked admin
 *     token and CreateProcessAsUserW's the quoted path (shlexec.c get_admin_token /
 *     SHELL_ExecuteW). The image is whatever bytes are at that path when CreateProcess
 *     opens it.
 * ShellExecuteEx returns TRUE when that launch returns a code greater than 32.
 *
 * This stand-in does not create a process. On runas it first replaces a writable
 * lpFile (the gap between any earlier check and CreateProcess), then records the
 * bytes that would be launched and returns TRUE.
 */
#define _GNU_SOURCE
#include <ctype.h>
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <fcntl.h>
#include <sys/stat.h>

/* .NET's Environment.SetEnvironmentVariable updates the managed block. libc getenv
 * in this process does not see those updates, so the test writes a config file.
 */
static char cfg_log[4096];
static char cfg_marker[4096];
static int cfg_off_verb = -1, cfg_off_file = -1;
static int cfg_loaded;

static void load_cfg(void)
{
    if (cfg_loaded) return;
    cfg_loaded = 1;
    FILE *f = fopen("/tmp/expanse-elevation-shell.conf", "r");
    if (!f) return;
    char line[4096];
    while (fgets(line, sizeof(line), f)) {
        char *nl = strchr(line, '\n');
        if (nl) *nl = 0;
        if (strncmp(line, "log=", 4) == 0) snprintf(cfg_log, sizeof(cfg_log), "%s", line + 4);
        else if (strncmp(line, "marker=", 7) == 0) snprintf(cfg_marker, sizeof(cfg_marker), "%s", line + 7);
        else if (strncmp(line, "off_verb=", 9) == 0) cfg_off_verb = atoi(line + 9);
        else if (strncmp(line, "off_file=", 9) == 0) cfg_off_file = atoi(line + 9);
    }
    fclose(f);
}

static void utf16_to_bytes(const uint16_t *w, char *out, size_t cap)
{
    size_t i = 0;
    if (!w) { if (cap) out[0] = 0; return; }
    while (w[i] && i + 1 < cap) {
        out[i] = w[i] < 128 ? (char)w[i] : '?';
        i++;
    }
    out[i] = 0;
}

static int verb_is_runas(const char *verb)
{
    const char *want = "runas";
    if (!verb) return 0;
    for (; *want; verb++, want++) {
        if (*verb == 0 || tolower((unsigned char)*verb) != *want) return 0;
    }
    return *verb == 0;
}

static int read_all(const char *path, unsigned char **data, size_t *len)
{
    FILE *f = fopen(path, "rb");
    if (!f) return 0;
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return 0; }
    long n = ftell(f);
    if (n < 0) { fclose(f); return 0; }
    rewind(f);
    unsigned char *buf = malloc((size_t)n + 1);
    if (!buf) { fclose(f); return 0; }
    size_t got = fread(buf, 1, (size_t)n, f);
    fclose(f);
    if (got != (size_t)n) { free(buf); return 0; }
    *data = buf;
    *len = got;
    return 1;
}

/* Directory entry can be replaced by this user: unlink and create a new inode.
 * A running image stays on the old inode (Linux), which is the same shape as
 * Windows allowing a running exe to be renamed and a new file written at its path.
 */
static int replace_file(const char *path, const unsigned char *marker, size_t n)
{
    unlink(path);
    int fd = open(path, O_CREAT | O_EXCL | O_WRONLY, 0644);
    if (fd < 0) return 0;
    size_t off = 0;
    while (off < n) {
        ssize_t w = write(fd, marker + off, n - off);
        if (w < 0) { close(fd); return 0; }
        off += (size_t)w;
    }
    close(fd);
    return 1;
}

static int dir_writable(const char *path)
{
    char dir[4096];
    size_t n = strlen(path);
    if (n == 0 || n >= sizeof(dir)) return 0;
    memcpy(dir, path, n + 1);
    char *slash = strrchr(dir, '/');
    if (!slash) return access(".", W_OK) == 0;
    if (slash == dir) return access("/", W_OK) == 0;
    *slash = 0;
    return access(dir, W_OK) == 0;
}

int ShellExecuteEx(void *info)
{
    load_cfg();
    int off_verb = cfg_off_verb;
    int off_file = cfg_off_file;
    const char *log_path = cfg_log[0] ? cfg_log : NULL;
    const char *marker_path = cfg_marker[0] ? cfg_marker : NULL;
    char verb[64] = {0};
    char path[4096] = {0};
    int writable = 0, replaced = 0, runas = 0;

    if (info && off_verb >= 0 && off_file >= 0) {
        void *verb_p = NULL, *file_p = NULL;
        memcpy(&verb_p, (unsigned char *)info + off_verb, sizeof(verb_p));
        memcpy(&file_p, (unsigned char *)info + off_file, sizeof(file_p));
        utf16_to_bytes(verb_p, verb, sizeof(verb));
        utf16_to_bytes(file_p, path, sizeof(path));
        runas = verb_is_runas(verb);
        writable = path[0] && dir_writable(path);
        if (runas && writable && marker_path) {
            unsigned char *marker = NULL;
            size_t n = 0;
            if (read_all(marker_path, &marker, &n) && n > 0 && replace_file(path, marker, n)) {
                unsigned char *back = NULL;
                size_t bn = 0;
                if (read_all(path, &back, &bn) && bn == n && memcmp(back, marker, n) == 0)
                    replaced = 1;
                free(back);
            }
            free(marker);
        }
    }

    if (log_path) {
        FILE *f = fopen(log_path, "a");
        if (f) {
            fprintf(f, "verb=%s\tpath=%s\twritable=%d\treplaced=%d\tadmin=%d\n",
                    verb, path, writable, replaced, runas);
            fclose(f);
        }
    }
    /* TRUE: Wine returns retval > 32 as success after CreateProcessAsUserW. */
    return 1;
}

int ShellExecuteExW(void *info) { return ShellExecuteEx(info); }

/* Medium integrity (SECURITY_MANDATORY_MEDIUM_RID, 0x2000). Elevation.Of reads
 * the last sub-authority of the mandatory label. A medium value makes
 * Elevation.IsElevated false, which is the unelevated start the product relaunches.
 */
static uint8_t sub_count = 1;
static int32_t medium_rid = 0x2000;

int GetCurrentProcessId(void) { return 4242; }

intptr_t OpenProcess(unsigned int access, int inherit, unsigned int pid)
{
    (void)access; (void)inherit; (void)pid;
    return (intptr_t)0x1000;
}

int CloseHandle(intptr_t h)
{
    (void)h;
    return 1;
}

int OpenProcessToken(intptr_t process, unsigned int access, intptr_t *token)
{
    (void)process; (void)access;
    if (token) *token = (intptr_t)0x2000;
    return 1;
}

int GetTokenInformation(intptr_t token, int infoClass, intptr_t info, int length, int *returned)
{
    (void)token; (void)infoClass;
    if (returned) *returned = (int)sizeof(void *);
    if (!info || length < (int)sizeof(void *)) return 0;
    intptr_t sid = (intptr_t)&sub_count;
    memcpy((void *)info, &sid, sizeof(sid));
    return 1;
}

intptr_t GetSidSubAuthorityCount(intptr_t sid)
{
    (void)sid;
    return (intptr_t)&sub_count;
}

intptr_t GetSidSubAuthority(intptr_t sid, unsigned int index)
{
    (void)sid; (void)index;
    return (intptr_t)&medium_rid;
}

/* TRUST_E_NOSIGNATURE. The test image is unsigned, as release builds are. */
int WinVerifyTrust(intptr_t hwnd, void *action, void *data)
{
    (void)hwnd; (void)action; (void)data;
    return (int)0x800B0100;
}

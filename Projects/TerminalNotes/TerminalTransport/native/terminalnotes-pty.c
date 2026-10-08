#define _DEFAULT_SOURCE
#define _POSIX_C_SOURCE 200809L
#define _XOPEN_SOURCE 700
#ifndef _DARWIN_C_SOURCE
#define _DARWIN_C_SOURCE 1
#endif
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#if defined(__APPLE__)
#include <util.h>
#else
#include <pty.h>
#endif
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

extern char **environ;

#define MAX_FRAME (16u * 1024u * 1024u)
#define MAX_QUEUE (32u * 1024u * 1024u)

typedef struct { unsigned char *data; size_t start, length, capacity; } buffer_t;
static volatile sig_atomic_t interrupted = 0;
static void on_signal(int signal_number) { (void)signal_number; interrupted = 1; }

static int append(buffer_t *b, const unsigned char *data, size_t n) {
    if (!n) return 0;
    if (b->length + n > MAX_QUEUE) return -1;
    if (b->start && b->start + b->length + n > b->capacity) {
        memmove(b->data, b->data + b->start, b->length); b->start = 0;
    }
    if (b->start + b->length + n > b->capacity) {
        size_t cap = b->capacity ? b->capacity : 8192;
        while (cap < b->start + b->length + n) cap *= 2;
        unsigned char *p = realloc(b->data, cap); if (!p) return -1;
        b->data = p; b->capacity = cap;
    }
    memcpy(b->data + b->start + b->length, data, n); b->length += n; return 0;
}
static void consume(buffer_t *b, size_t n) { if (n >= b->length) { b->start = b->length = 0; } else { b->start += n; b->length -= n; } }
static uint32_t read_u32(const unsigned char *p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
static int set_nonblock(int fd) { int flags = fcntl(fd, F_GETFL); return flags < 0 ? -1 : fcntl(fd, F_SETFL, flags | O_NONBLOCK); }
static long long now_ms(void) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000; }
static int terminal_belongs_to_session(int master, pid_t session) {
    return master >= 0 && session > 0 && tcgetsid(master) == session;
}

static int is_owned_group(int master, pid_t session, pid_t group) {
    if (!terminal_belongs_to_session(master, session) || group <= 0) return 0;
    /* The terminal's foreground group is guaranteed to belong to its session,
       even when that group's original leader has already exited. */
    if (tcgetpgrp(master) == group) return 1;
    /* forkpty creates the initial group with PGID == SID. The PTY session check
       above still anchors that group after its original leader has exited. */
    return group == session;
}

static void signal_owned_group(int master, pid_t session, pid_t group, int signal_number) {
    if (is_owned_group(master, session, group)) (void)kill(-group, signal_number);
}

static pid_t foreground_group(int master, pid_t session) {
    if (!terminal_belongs_to_session(master, session)) return -1;
    pid_t group = tcgetpgrp(master);
    return group > 0 ? group : -1;
}

static void signal_owned_process_groups(int master, pid_t session, pid_t foreground, int signal_number) {
    signal_owned_group(master, session, foreground, signal_number);
    if (foreground != session) signal_owned_group(master, session, session, signal_number);
}

static void request_shutdown(int *closing, int *stage, long long *deadline, pid_t child,
                             int master, pid_t *foreground) {
    if (*closing) return;
    *foreground = foreground_group(master, child);
    *closing = 1; *stage = 0; *deadline = now_ms() + 400;
    signal_owned_process_groups(master, child, *foreground, SIGHUP);
}

static char *resolve_exec(const char *file) {
    if (strchr(file, '/')) return strdup(file);
    const char *path = getenv("PATH"); if (!path) path = "/bin:/usr/bin";
    const char *p = path;
    while (1) {
        const char *end = strchr(p, ':'); size_t len = end ? (size_t)(end - p) : strlen(p);
        size_t need = (len ? len : 1) + 1 + strlen(file) + 1;
        char *candidate = malloc(need); if (!candidate) return NULL;
        if (len) { memcpy(candidate, p, len); candidate[len] = '/'; strcpy(candidate + len + 1, file); }
        else { candidate[0] = '.'; candidate[1] = '/'; strcpy(candidate + 2, file); }
        if (access(candidate, X_OK) == 0) return candidate;
        free(candidate);
        if (!end) break;
        p = end + 1;
    }
    return NULL;
}

static void shutdown_child(pid_t session, pid_t foreground, int master, int child_done) {
    signal_owned_process_groups(master, session, foreground, SIGHUP);
    long long deadline = now_ms() + 400;
    int reaped = child_done;
    while (!reaped && now_ms() < deadline) {
        int status; pid_t result = waitpid(session, &status, WNOHANG);
        if (result == session || (result < 0 && errno == ECHILD)) reaped = 1;
        struct pollfd p = { master, POLLIN, 0 };
        if (!reaped) (void)poll(&p, 1, 20);
        unsigned char discard[4096]; while (master >= 0 && read(master, discard, sizeof(discard)) > 0) { }
    }
    if (!reaped || is_owned_group(master, session, foreground)) {
        struct timespec pause = { .tv_sec = 0, .tv_nsec = 200000000 };
        signal_owned_process_groups(master, session, foreground, SIGTERM);
        nanosleep(&pause, NULL);
        signal_owned_process_groups(master, session, foreground, SIGKILL);
    }
    if (master >= 0) close(master);
    if (!reaped) { int status; while (waitpid(session, &status, 0) < 0 && errno == EINTR) { } }
}

int main(int argc, char **argv) {
    const char *cwd = NULL; int columns = 80, rows = 24, i = 1;
    while (i < argc && strcmp(argv[i], "--") != 0) {
        if (strcmp(argv[i], "--cwd") == 0 && i + 1 < argc) cwd = argv[++i];
        else if (strcmp(argv[i], "--columns") == 0 && i + 1 < argc) columns = atoi(argv[++i]);
        else if (strcmp(argv[i], "--rows") == 0 && i + 1 < argc) rows = atoi(argv[++i]);
        else { dprintf(STDERR_FILENO, "terminalnotes-pty: invalid helper arguments\n"); return 125; }
        i++;
    }
    if (!cwd || i + 1 >= argc || columns < 1 || columns > 65535 || rows < 1 || rows > 65535) {
        dprintf(STDERR_FILENO, "terminalnotes-pty: missing command or invalid initial size\n"); return 125;
    }
    char **child_argv = &argv[i + 1];
    char *executable = resolve_exec(child_argv[0]);
    if (!executable) { dprintf(STDERR_FILENO, "terminalnotes-pty: executable not found in PATH\n"); return 127; }

    struct winsize size = { .ws_col = (unsigned short)columns, .ws_row = (unsigned short)rows };
    int master = -1;
    pid_t child = forkpty(&master, NULL, NULL, &size);
    if (child < 0) { dprintf(STDERR_FILENO, "terminalnotes-pty: forkpty failed: %s\n", strerror(errno)); free(executable); return 125; }
    if (child == 0) {
        if (chdir(cwd) != 0) { const char msg[] = "terminalnotes-pty: cannot enter working directory\r\n"; (void)write(STDERR_FILENO, msg, sizeof(msg) - 1); _exit(126); }
        execve(executable, child_argv, environ);
        const char msg[] = "terminalnotes-pty: exec failed\r\n"; (void)write(STDERR_FILENO, msg, sizeof(msg) - 1); _exit(127);
    }
    free(executable);
    (void)set_nonblock(master); (void)set_nonblock(STDIN_FILENO); (void)set_nonblock(STDOUT_FILENO);
    signal(SIGTERM, on_signal); signal(SIGINT, on_signal); signal(SIGHUP, on_signal); signal(SIGPIPE, SIG_IGN);
    buffer_t control = {0}, to_pty = {0}, to_stdout = {0};
    int closing = 0, close_stage = -1, input_eof = 0, pty_eof = 0, child_done = 0, child_status = 0;
    pid_t owned_foreground = -1;
    long long close_deadline = 0;
    int result = 125;

    while (1) {
        if (interrupted) request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground);
        if (closing && close_stage == 0 && now_ms() >= close_deadline) {
            signal_owned_process_groups(master, child, owned_foreground, SIGTERM); close_stage = 1; close_deadline = now_ms() + 200;
        } else if (closing && close_stage == 1 && now_ms() >= close_deadline) {
            signal_owned_process_groups(master, child, owned_foreground, SIGKILL); close_stage = 2;
        }

        struct pollfd fds[3] = {
            { STDIN_FILENO, (short)(!input_eof && !closing ? POLLIN : 0), 0 },
            { master, (short)((!pty_eof ? POLLIN : 0) | (to_pty.length ? POLLOUT : 0)), 0 },
            { STDOUT_FILENO, (short)(to_stdout.length ? POLLOUT : 0), 0 }
        };
        int pr = poll(fds, 3, 50);
            if (pr < 0 && errno != EINTR) request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground);

        if (fds[0].revents & (POLLIN | POLLHUP | POLLERR)) {
            unsigned char temp[8192]; ssize_t n = read(STDIN_FILENO, temp, sizeof(temp));
            if (n <= 0) { if (n == 0 || (errno != EAGAIN && errno != EINTR)) { input_eof = 1; request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground); } }
            else if (append(&control, temp, (size_t)n) != 0) request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground);
        }

        while (control.length >= 5) {
            unsigned char *frame = control.data + control.start;
            uint8_t type = frame[0]; uint32_t length = read_u32(frame + 1);
            if (length > MAX_FRAME) { request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground); break; }
            if (control.length < (size_t)length + 5) break;
            if (type == 1) {
                if (append(&to_pty, frame + 5, length) != 0) request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground);
            } else if (type == 2 && length == 8) {
                int cols = (int)read_u32(frame + 5), lines = (int)read_u32(frame + 9);
                if (cols > 0 && cols <= 65535 && lines > 0 && lines <= 65535) {
                    struct winsize ws = { .ws_col = (unsigned short)cols, .ws_row = (unsigned short)lines };
                    (void)ioctl(master, TIOCSWINSZ, &ws);
                }
            } else if (type == 3 && length == 0) {
                request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground);
            } else request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground);
            consume(&control, (size_t)length + 5);
        }

        if ((fds[1].revents & POLLIN) && !pty_eof) {
            unsigned char temp[8192]; ssize_t n = read(master, temp, sizeof(temp));
            if (n > 0) { if (append(&to_stdout, temp, (size_t)n) != 0) request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground); }
            else if (n == 0 || (n < 0 && errno != EAGAIN && errno != EINTR && errno != EIO)) pty_eof = 1;
            else if (n < 0 && errno == EIO) pty_eof = 1;
        }
        if ((fds[1].revents & POLLOUT) && to_pty.length) {
            ssize_t n = write(master, to_pty.data + to_pty.start, to_pty.length);
            if (n > 0) consume(&to_pty, (size_t)n);
        }
        if ((fds[2].revents & POLLOUT) && to_stdout.length) {
            ssize_t n = write(STDOUT_FILENO, to_stdout.data + to_stdout.start, to_stdout.length);
            if (n > 0) consume(&to_stdout, (size_t)n);
            else if (n < 0 && errno != EAGAIN && errno != EINTR) request_shutdown(&closing, &close_stage, &close_deadline, child, master, &owned_foreground);
        }
        if (fds[1].revents & (POLLHUP | POLLERR)) {
            unsigned char temp[8192]; ssize_t n = read(master, temp, sizeof(temp));
            if (n > 0) (void)append(&to_stdout, temp, (size_t)n); else if (n == 0 || errno == EIO) pty_eof = 1;
        }

        if (!child_done) {
            pid_t w = waitpid(child, &child_status, WNOHANG);
            if (w == child) child_done = 1;
        }
        if (child_done && pty_eof && to_stdout.length == 0) break;
    }

    if (closing) shutdown_child(child, owned_foreground, master, child_done);
    else { close(master); if (!child_done) (void)waitpid(child, &child_status, 0); }
    if (WIFEXITED(child_status)) result = WEXITSTATUS(child_status);
    else if (WIFSIGNALED(child_status)) result = 128 + WTERMSIG(child_status);
    free(control.data); free(to_pty.data); free(to_stdout.data);
    return result;
}

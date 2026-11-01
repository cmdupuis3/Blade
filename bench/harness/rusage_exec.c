/* rusage_exec <outfile> <argv...>: fork + exec argv, wait4, write "maxrss_kb minflt majflt" to
 * outfile, exit with the child's status. Linux harness only (run.py run_proc): a child forked
 * straight from Python inherits the interpreter's RSS high-water mark in ru_maxrss (exec keeps
 * it), so the measured program is forked from this ~1 MB process instead. */
#include <stdio.h>
#include <stdlib.h>
#include <sys/resource.h>
#include <sys/wait.h>
#include <unistd.h>
int main(int argc, char **argv) {
    if (argc < 3) { fprintf(stderr, "usage: rusage_exec <outfile> <argv...>\n"); return 125; }
    pid_t pid = fork();
    if (pid < 0) { perror("rusage_exec: fork"); return 125; }
    if (pid == 0) { execvp(argv[2], argv + 2); perror("rusage_exec: exec"); _exit(127); }
    int st = 0; struct rusage ru;
    if (wait4(pid, &st, 0, &ru) < 0) { perror("rusage_exec: wait4"); return 125; }
    FILE *f = fopen(argv[1], "w");
    if (f) { fprintf(f, "%ld %ld %ld\n", ru.ru_maxrss, ru.ru_minflt, ru.ru_majflt); fclose(f); }
    if (WIFEXITED(st)) return WEXITSTATUS(st);
    return 128 + (WIFSIGNALED(st) ? WTERMSIG(st) : 0);
}

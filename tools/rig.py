#!/usr/bin/env python3
"""Drive the Odin steam-headless rig: deploy a mod DLL to a live TaleSpire and look at it.

    rig.py status
    rig.py deploy MapGen [--restart]
    rig.py log [--follow] [--grep PATTERN] [--lines N]
    rig.py launch
    rig.py shutdown
    rig.py screenshot out.png [--crop WxH+X+Y]
    rig.py clipboard --file slab.txt | --text '...'
    rig.py paste

TaleSpire loads plugins once at launch, so `deploy` without `--restart` leaves the old code
running. `--restart` uses the game's own quit dialog rather than a kill, because that is the
path that saves board state.
"""

import argparse
import os
import subprocess
import sys
import time

HOST      = "root@192.168.1.199"
KEY       = os.path.expanduser("~/.ssh/odin_ed25519")
GAME_DIR  = "/mnt/user/Games/Linux/steamapps/common/TaleSpire Demo"
PLUGINS   = f"{GAME_DIR}/BepInEx/plugins"
LOG       = f"{GAME_DIR}/BepInEx/LogOutput.log"
CONTAINER = "steam-headless"
DISPLAY   = ":55"
GAME_ID   = "2881860"


def ssh(cmd, *, stdin=None, capture=True, check=True):
    argv = ["ssh", "-i", KEY, "-o", "StrictHostKeyChecking=no", HOST, cmd]
    return subprocess.run(argv, stdin=stdin, check=check,
                          stdout=subprocess.PIPE if capture else None,
                          stderr=subprocess.PIPE if capture else None,
                          text=not capture is False)


def indocker(cmd, *, user=None, detach=False, capture=True, check=True):
    flags = f"-e DISPLAY={DISPLAY}"
    if user:   flags += f" -u {user}"
    if detach: flags += " -d"
    return ssh(f"docker exec {flags} {CONTAINER} {cmd}", capture=capture, check=check)


def out(r):
    return (r.stdout or "").strip()


# ── commands ────────────────────────────────────────────────────────────────

def cmd_status(a):
    r = indocker("pgrep -af TaleSpire.exe", check=False)
    running = bool(out(r))
    print(f"game:    {'running' if running else 'not running'}")
    if running:
        print(f"         {out(r).splitlines()[0]}")

    r = ssh(f"ls -l --time-style=+%Y-%m-%d\\ %H:%M '{PLUGINS}'", check=False)
    print("plugins:")
    for line in out(r).splitlines()[1:]:
        print(f"         {line}")
    return 0


def cmd_deploy(a):
    dll = a.dll if a.dll.endswith(".dll") else \
        os.path.join(os.path.dirname(__file__), "..", a.dll, "bin", "Debug", f"{a.dll}.dll")
    dll = os.path.abspath(dll)
    if not os.path.exists(dll):
        print(f"no such DLL: {dll}", file=sys.stderr)
        return 2

    name = os.path.basename(dll)
    # scp is not usable here: the game directory contains a space.
    with open(dll, "rb") as f:
        subprocess.run(["ssh", "-i", KEY, "-o", "StrictHostKeyChecking=no", HOST,
                        f"cat > '{PLUGINS}/{name}'"], stdin=f, check=True)
    print(f"deployed {name} ({os.path.getsize(dll)} bytes)")

    if a.restart:
        cmd_shutdown(a)
        time.sleep(3)
        cmd_launch(a)
    else:
        print("note: TaleSpire loads plugins at launch — restart before testing")
    return 0


def cmd_log(a):
    pat = f" | grep -a -E '{a.grep}'" if a.grep else ""
    if a.follow:
        subprocess.run(["ssh", "-i", KEY, "-o", "StrictHostKeyChecking=no", HOST,
                        f"tail -f -n {a.lines} '{LOG}'{pat}"])
        return 0
    print(out(ssh(f"tail -n {a.lines} '{LOG}'{pat}", check=False)))
    return 0


def cmd_launch(a):
    if out(indocker("pgrep -af TaleSpire.exe", check=False)):
        print("already running")
        return 0
    indocker(f"bash -c 'steam steam://rungameid/{GAME_ID}'", user="default", detach=True)
    print("launching — the main menu takes ~40s to appear")
    return 0


def _window_id():
    r = indocker("xdotool search --name TaleSpire", check=False)
    ids = out(r).splitlines()
    return ids[-1] if ids else None


def cmd_shutdown(a):
    if not out(indocker("pgrep -af TaleSpire.exe", check=False)):
        print("not running")
        return 0

    wid = _window_id()
    if not wid:
        print("game is running but has no window — refusing to kill it", file=sys.stderr)
        return 1

    # wmctrl asks the game to quit, which is what saves board state; it then blocks on a
    # confirmation dialog that has to be clicked.
    indocker(f"wmctrl -ic {wid}", check=False)
    time.sleep(2)
    indocker("xdotool key Return", check=False)

    for _ in range(20):
        time.sleep(2)
        if not out(indocker("pgrep -af TaleSpire.exe", check=False)):
            print("shut down")
            return 0
    print("still running — the quit dialog may need a click; run `rig.py screenshot`",
          file=sys.stderr)
    return 1


def cmd_click(a):
    indocker(f"xdotool mousemove {a.x} {a.y} click {a.button}", check=False)
    print(f"clicked ({a.x},{a.y})")
    return 0


def cmd_key(a):
    indocker(f"xdotool key {a.keys}", check=False)
    print(f"sent {a.keys}")
    return 0


def cmd_screenshot(a):
    remote = "/tmp/rig-shot.png"
    indocker(f"import -window root {remote}")
    if a.crop:
        indocker(f"convert {remote} -crop '{a.crop}' +repage {remote}")
    if a.zoom and a.zoom != 1:
        indocker(f"convert {remote} -resize {a.zoom * 100}% {remote}")
    with open(a.out, "wb") as f:
        subprocess.run(["ssh", "-i", KEY, "-o", "StrictHostKeyChecking=no", HOST,
                        f"docker exec {CONTAINER} cat {remote}"], stdout=f, check=True)
    indocker(f"rm -f {remote}", check=False)
    print(f"wrote {a.out} ({os.path.getsize(a.out)} bytes)")
    return 0


def cmd_clipboard(a):
    text = a.text
    if a.file:
        with open(a.file) as f:
            text = f.read().strip()
    if not text:
        print("nothing to copy", file=sys.stderr)
        return 2

    remote = "/tmp/rig-clip.txt"
    subprocess.run(["ssh", "-i", KEY, "-o", "StrictHostKeyChecking=no", HOST,
                    f"docker exec -i {CONTAINER} tee {remote} > /dev/null"],
                   input=text.encode(), check=True)
    indocker(f"bash -c 'xclip -selection clipboard < {remote}'", detach=True, check=False)
    time.sleep(1)
    print(f"copied {len(text)} chars to the container clipboard")
    return 0


def cmd_paste(a):
    wid = _window_id()
    if not wid:
        print("TaleSpire window not found", file=sys.stderr)
        return 1
    indocker(f"xdotool windowactivate --sync {wid}", check=False)
    time.sleep(1)
    indocker(f"xdotool key --window {wid} ctrl+v", check=False)
    print("sent ctrl+v — the board must be open in build mode for this to land")
    return 0


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("status").set_defaults(fn=cmd_status)

    d = sub.add_parser("deploy")
    d.add_argument("dll", help="mod name (MapGen) or a path to a .dll")
    d.add_argument("--restart", action="store_true")
    d.set_defaults(fn=cmd_deploy)

    l = sub.add_parser("log")
    l.add_argument("--follow", action="store_true")
    l.add_argument("--grep")
    l.add_argument("--lines", type=int, default=40)
    l.set_defaults(fn=cmd_log)

    sub.add_parser("launch").set_defaults(fn=cmd_launch)
    sub.add_parser("shutdown").set_defaults(fn=cmd_shutdown)

    s = sub.add_parser("screenshot")
    s.add_argument("out")
    s.add_argument("--crop", help="ImageMagick geometry, e.g. 800x600+400+200")
    s.add_argument("--zoom", type=int, default=1, help="upscale factor, for reading small UI text")
    s.set_defaults(fn=cmd_screenshot)

    k = sub.add_parser("click")
    k.add_argument("x", type=int)
    k.add_argument("y", type=int)
    k.add_argument("--button", type=int, default=1)
    k.set_defaults(fn=cmd_click)

    key = sub.add_parser("key")
    key.add_argument("keys", help="xdotool key spec, e.g. ctrl+v or Escape")
    key.set_defaults(fn=cmd_key)

    c = sub.add_parser("clipboard")
    c.add_argument("--file")
    c.add_argument("--text")
    c.set_defaults(fn=cmd_clipboard)

    sub.add_parser("paste").set_defaults(fn=cmd_paste)

    a = p.parse_args()
    return a.fn(a)


if __name__ == "__main__":
    sys.exit(main())
